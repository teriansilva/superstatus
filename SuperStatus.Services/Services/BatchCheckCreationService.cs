using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Providers;
using SuperStatus.Services.Plugins;

namespace SuperStatus.Services.Services
{
    /// <summary>Result of a batch create: either a 422 rejection (the whole batch is
    /// refused, nothing written) or a 200 response with the per-line breakdown.</summary>
    public sealed class BatchCreateOutcome
    {
        public bool Rejected { get; private init; }
        public string? RejectionMessage { get; private init; }
        public BatchCreateChecksResponse? Response { get; private init; }

        public static BatchCreateOutcome Reject(string message) => new() { Rejected = true, RejectionMessage = message };
        public static BatchCreateOutcome Ok(BatchCreateChecksResponse response) => new() { Response = response };
    }

    public interface IBatchCheckCreationService
    {
        Task<BatchCreateOutcome> CreateBatchAsync(BatchCreateChecksRequest request, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Epic #342: create many checks from a pasted target list. Each planned target rides the
    /// exact same <c>AddOrUpdateStatusCheck</c> → <c>ApplyEditLinks</c> → <c>ApplyEditSla</c>
    /// sequence the <c>/statuscheck/edit</c> endpoint uses (so batch and single-create stay
    /// behaviourally identical), but the whole batch commits in ONE transaction: any
    /// per-target failure rolls back every created check AND its webhook/profile/SLA links.
    /// Shared-setting validation (unknown ids, over-cap, no valid targets) rejects the whole
    /// batch with a 422 before any write. Per-line invalid / duplicate targets are skipped
    /// with a reason; the valid remainder still creates.
    /// </summary>
    public sealed class BatchCheckCreationService(
        ICheckProviderRegistry registry,
        IStatusCheckService statusCheckService,
        ILinkedTargetNormalizationService linkedTargets,
        ISlaNormalizationService slaService,
        IRepository<Sla> slaRepository,
        SuperStatusDb db) : IBatchCheckCreationService
    {
        /// <summary>Upper bound on targets per batch — bounds the single transaction. The
        /// dialog guards client-side too; the server is authoritative.</summary>
        public const int MaxBatchSize = 200;

        private static readonly char[] LineSeparators = { '\n', '\r', ',', '\t', ' ' };

        public async Task<BatchCreateOutcome> CreateBatchAsync(BatchCreateChecksRequest request, CancellationToken cancellationToken = default)
        {
            string providerType = (request.ProviderType ?? string.Empty).Trim();
            var provider = registry.Find(providerType);
            if (provider is null)
                return BatchCreateOutcome.Reject($"Unknown provider type '{providerType}'.");

            string? targetField = provider.Descriptor.BatchTargetField;
            if (targetField is null)
                return BatchCreateOutcome.Reject($"'{provider.Descriptor.DisplayName}' does not support batch paste.");

            // Server-authoritative split — accept a pre-split list or one pasted blob.
            var lines = (request.Targets ?? new List<string>())
                .SelectMany(t => (t ?? string.Empty).Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();

            if (lines.Count > MaxBatchSize)
                return BatchCreateOutcome.Reject($"Too many targets: {lines.Count} (max {MaxBatchSize} per batch).");

            // Shared-setting validation — identical rules to /statuscheck/edit, checked
            // before any write so a bad id leaves nothing half-applied.
            if (request.WebhookIds is { Count: > 0 } webhookIds)
            {
                var missing = await linkedTargets.FindMissingWebhookIdsAsync(webhookIds, cancellationToken);
                if (missing.Count > 0)
                    return BatchCreateOutcome.Reject($"Unknown webhook id(s): {string.Join(", ", missing)}");
            }
            if (request.AlertProfileIds is { Count: > 0 } alertProfileIds)
            {
                var missing = await linkedTargets.FindMissingAlertProfileIdsAsync(alertProfileIds, cancellationToken);
                if (missing.Count > 0)
                    return BatchCreateOutcome.Reject($"Unknown alert profile id(s): {string.Join(", ", missing)}");
            }
            if (request.SlaId is long slaId && !await slaRepository.Any(s => s.Id == slaId, cancellationToken))
                return BatchCreateOutcome.Reject($"Unknown SLA id: {slaId}");

            // Existing targets of this provider, canonicalised, for cross-check dedup.
            var existing = await LoadExistingCanonicalTargetsAsync(provider, providerType, targetField, cancellationToken);

            var plan = BatchCheckPlanner.Plan(provider, lines, existing, request.NamePrefix, request.NameTemplate);
            if (plan.Valid.Count == 0)
                return BatchCreateOutcome.Reject("No valid new targets to create.");

            // Guard the provider schema's REQUIRED non-target config fields BEFORE any write
            // (the shared config is identical for every target; the target field is filled
            // per-target). Without this, an AI batch missing model / prompt / expectContains
            // would create ENABLED checks that immediately resolve as invalid — a 200 that
            // lied. We check required-ness rather than run the full ConfigSchema.Validate on
            // the built JSON, because that validator rejects a blank OPTIONAL number field
            // (ProviderConfigWriter stores it as "") — which would make batch stricter than
            // the single-create path and wrongly reject legitimate blank-optional configs.
            // Value-format for present fields stays on the same resolve-time gate single
            // create uses, so batch and single-create accept exactly the same configs.
            var missingRequired = provider.Descriptor.ConfigSchema.Fields.FirstOrDefault(f =>
                f.Required && f.Key != targetField
                && string.IsNullOrWhiteSpace(request.SharedConfig is null ? null : request.SharedConfig.GetValueOrDefault(f.Key)));
            if (missingRequired is not null)
                return BatchCreateOutcome.Reject($"Provider config is incomplete: '{missingRequired.Label}' is required for {provider.Descriptor.DisplayName}.");

            var response = new BatchCreateChecksResponse();

            await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                foreach (var planned in plan.Valid)
                {
                    var vm = BuildViewModel(request, providerType, targetField, planned);
                    StatusCheck saved = await statusCheckService.AddOrUpdateStatusCheck(vm);
                    await linkedTargets.ApplyEditLinksAsync(saved, request.WebhookIds, request.AlertProfileIds, cancellationToken);
                    await slaService.ApplyEditSlaAsync(saved, request.SlaId, isNewCheck: true, cancellationToken);

                    response.Results.Add(new BatchTargetResultViewModel
                    {
                        Input = planned.Input,
                        CanonicalTarget = planned.CanonicalTarget,
                        Title = planned.Title,
                        CreatedId = saved.Id,
                        Status = "created",
                    });
                }
                await tx.CommitAsync(cancellationToken);
            }

            foreach (var s in plan.Skipped)
                response.Results.Add(new BatchTargetResultViewModel { Input = s.Input, Status = "skipped", Reason = s.Reason });

            response.CreatedCount = plan.Valid.Count;
            response.SkippedCount = plan.Skipped.Count;
            return BatchCreateOutcome.Ok(response);
        }

        private static StatusCheckViewModelBase BuildViewModel(BatchCreateChecksRequest request, string providerType, string targetField, PlannedCheck planned)
        {
            return new StatusCheckViewModelBase
            {
                Id = 0,
                Title = planned.Title,
                Description = string.Empty,
                ServiceLogoUrl = string.Empty,
                Enabled = request.Enabled,
                ProviderType = providerType,
                ProviderConfig = BuildConfigDict(request.SharedConfig, targetField, planned.CanonicalTarget),
                // http reads url from ProviderConfig first; keep the legacy column in sync too.
                StatusCheckUrl = planned.CanonicalTarget,
                IntervalSeconds = request.IntervalSeconds,
                AutoIncidentEnabled = request.AutoIncidentEnabled,
                AlertOnFailureThreshold = request.AlertOnFailureThreshold,
                AlertOnOutageMinutes = request.AlertOnOutageMinutes,
                AlertOnRecovery = request.AlertOnRecovery,
                AlertThrottleMinutes = request.AlertThrottleMinutes,
            };
        }

        // The full per-target config: the shared fields + this target's value in the target
        // field (which wins over any shared value). Used for both the pre-write schema
        // validation and the per-check view model, so they can't diverge.
        private static Dictionary<string, string> BuildConfigDict(Dictionary<string, string>? sharedConfig, string targetField, string canonicalTarget)
        {
            var config = new Dictionary<string, string>(sharedConfig ?? new());
            config[targetField] = canonicalTarget;
            return config;
        }

        private async Task<HashSet<string>> LoadExistingCanonicalTargetsAsync(ICheckProvider provider, string providerType, string targetField, CancellationToken cancellationToken)
        {
            bool isHttp = providerType == Providers.Http.HttpCheckProvider.TypeId;
            var rows = await db.StatusCheckSet
                // http is also the stored type for pre-#312 rows with a null/empty ProviderType.
                .Where(c => c.ProviderType == providerType || (isHttp && (c.ProviderType == null || c.ProviderType == "")))
                .Select(c => new { c.StatusCheckUrl, c.ConfigJson })
                .ToListAsync(cancellationToken);

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                string? raw = ReadConfigString(row.ConfigJson, targetField)
                    ?? (isHttp ? row.StatusCheckUrl : null);
                if (!string.IsNullOrWhiteSpace(raw) && provider.TryParseBatchTarget(raw, out var canonical, out _))
                    existing.Add(canonical);
            }
            return existing;
        }

        private static string? ReadConfigString(string? configJson, string key)
        {
            if (string.IsNullOrWhiteSpace(configJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(configJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(key, out var v)
                    && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            }
            catch (JsonException) { }
            return null;
        }
    }
}
