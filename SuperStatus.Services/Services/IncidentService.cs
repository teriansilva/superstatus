using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Extensions;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using System.Collections.Immutable;

namespace SuperStatus.Services.Services
{
    public interface IIncidentService
    {
        Task<IDictionary<DateTime, List<IncidentViewModel>>> GetIncidentViewModelSetForDays(int page = 1, int pageSize = 0);

        /// <summary>
        /// Public open incidents for the /api/status endpoint (#108).
        /// Filters Resolved=false + VisibleToPublic=true at the repo so
        /// operator drafts never leak.
        /// </summary>
        Task<List<Incident>> GetOpenPublicIncidents();

        /// <summary>
        /// Number of incidents created in the last <paramref name="daysBack"/>
        /// days. Used by the dashboard hero summary (#104) for the
        /// "Incidents 30d" telemetry chip.
        /// </summary>
        Task<int> CountIncidentsInWindowAsync(int daysBack, CancellationToken cancellationToken = default);

        /// <summary>
        /// Mean time to resolution over incidents resolved in the last
        /// <paramref name="daysBack"/> days (issue #106). Null when no
        /// incident with a measured duration resolved in the window.
        /// </summary>
        Task<TimeSpan?> GetMttrAsync(int daysBack, CancellationToken cancellationToken = default);

        /// <summary>
        /// Issue #106 PR2: create or update an incident from the operator form.
        /// Persists Title/Description/Severity/VisibleToPublic/Resolved and
        /// manages the <c>ResolvedUtc</c> lifecycle — stamped when the incident
        /// transitions to resolved, cleared if it is reopened — so MTTR (#106)
        /// only ever measures a real open→resolved duration.
        /// </summary>
        Task<IncidentViewModel> AddOrUpdateIncident(IncidentViewModel incident, CancellationToken cancellationToken = default);

        /// <summary>#168: is there already an OPEN auto-generated incident linked to
        /// this check? Cheap gate before enqueuing a draft.</summary>
        Task<bool> HasOpenLinkedAutoIncidentAsync(long statusCheckId, CancellationToken cancellationToken = default);

        /// <summary>#168: persist an AI/templated auto-incident for a check, idempotently
        /// — returns the existing open linked auto-incident if one exists (query-before-
        /// insert), and treats the partial-unique-index violation under a concurrent
        /// tick as "already drafted". Always public + auto-flagged.</summary>
        Task<IncidentViewModel> CreateAutoIncidentAsync(long statusCheckId, IncidentDraft draft, CancellationToken cancellationToken = default);

        /// <summary>#168: resolve the open auto-incident linked to this check on
        /// recovery — scoped to the linked auto-incident only, never a manual or
        /// unrelated one. No-op when there is none open.</summary>
        Task ResolveLinkedAutoIncidentAsync(long statusCheckId, CancellationToken cancellationToken = default);
    }

    public class IncidentService(IIncidentRepository incidentRepository) : IIncidentService
    {
        public async Task<IncidentViewModel> AddOrUpdateIncident(IncidentViewModel vm, CancellationToken cancellationToken = default)
        {
            if (vm.Id > 0)
            {
                Incident existing = await incidentRepository.GetIncidentById(vm.Id)
                    ?? throw new InvalidOperationException($"Incident {vm.Id} not found.");

                bool wasResolved = existing.Resolved;
                existing.Title = vm.Title;
                existing.Description = vm.Description ?? string.Empty;
                existing.VisibleToPublic = vm.VisibleToPublic;
                existing.Severity = vm.Severity;
                existing.Resolved = vm.Resolved;
                // ResolvedUtc lifecycle: stamp on the open→resolved transition,
                // clear on reopen. Don't overwrite an existing stamp when an
                // already-resolved incident is edited (preserves MTTR).
                if (vm.Resolved && !wasResolved) existing.ResolvedUtc = DateTime.UtcNow;
                else if (!vm.Resolved) existing.ResolvedUtc = null;

                Incident updated = await incidentRepository.UpdateAndSave(existing, cancellationToken);
                return new IncidentViewModel(updated);
            }

            var incident = new Incident
            {
                Title = vm.Title,
                Description = vm.Description ?? string.Empty,
                VisibleToPublic = vm.VisibleToPublic,
                Severity = vm.Severity,
                Resolved = vm.Resolved,
                AuotmaticallyGeneratedReport = false,
                Created = DateTime.UtcNow,
                ResolvedUtc = vm.Resolved ? DateTime.UtcNow : null,
            };
            Incident added = await incidentRepository.AddAndSave(incident, cancellationToken);
            return new IncidentViewModel(added);
        }

        public async Task<bool> HasOpenLinkedAutoIncidentAsync(long statusCheckId, CancellationToken cancellationToken = default)
            => await incidentRepository.GetOpenAutoIncidentForCheck(statusCheckId) is not null;

        public async Task<IncidentViewModel> CreateAutoIncidentAsync(long statusCheckId, IncidentDraft draft, CancellationToken cancellationToken = default)
        {
            // Query-before-insert: if one is already open for this check, reuse it.
            Incident? existing = await incidentRepository.GetOpenAutoIncidentForCheck(statusCheckId);
            if (existing is not null) return new IncidentViewModel(existing);

            var incident = new Incident
            {
                Title = draft.Title,
                Description = draft.Description,
                Severity = draft.Severity,
                VisibleToPublic = true,
                Resolved = false,
                AuotmaticallyGeneratedReport = true,
                SourceStatusCheckId = statusCheckId,
                Created = DateTime.UtcNow,
            };
            try
            {
                Incident added = await incidentRepository.AddAndSave(incident, cancellationToken);
                return new IncidentViewModel(added);
            }
            catch (DbUpdateException)
            {
                // Expected ONLY when a concurrent tick won the partial-unique-index
                // race — confirmed by the winning row now existing. If no winner is
                // found this was an unrelated persistence failure: rethrow so it is
                // surfaced (and the worker logs it as an error) rather than reporting
                // a phantom success for an incident that was never saved.
                Incident? winner = await incidentRepository.GetOpenAutoIncidentForCheck(statusCheckId);
                if (winner is null) throw;
                return new IncidentViewModel(winner);
            }
        }

        public async Task ResolveLinkedAutoIncidentAsync(long statusCheckId, CancellationToken cancellationToken = default)
        {
            Incident? open = await incidentRepository.GetOpenAutoIncidentForCheck(statusCheckId);
            if (open is null) return; // nothing to resolve
            open.Resolved = true;
            open.ResolvedUtc = DateTime.UtcNow;
            await incidentRepository.UpdateAndSave(open, cancellationToken);
        }

        public async Task<IDictionary<DateTime, List<IncidentViewModel>>> GetIncidentViewModelSetForDays(int page = 1, int pageSize = 0)
        {
            IDictionary<DateTime, IList<Incident>> incidents = await incidentRepository.GetIncidentSetForDaysGroupedByDays(30, 100);
            return incidents.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Select(x => new IncidentViewModel(x)).ToList()
            );
        }

        public Task<List<Incident>> GetOpenPublicIncidents() =>
            incidentRepository.GetOpenPublicIncidents();

        public async Task<int> CountIncidentsInWindowAsync(int daysBack, CancellationToken cancellationToken = default)
        {
            IDictionary<DateTime, IList<Incident>> grouped =
                await incidentRepository.GetIncidentSetForDaysGroupedByDays(daysBack, 1000);
            return grouped.Values.Sum(list => list.Count);
        }

        public async Task<TimeSpan?> GetMttrAsync(int daysBack, CancellationToken cancellationToken = default)
        {
            var resolved = await incidentRepository.GetResolvedIncidentsInWindow(daysBack);
            // Only count incidents with a positive measured duration.
            var durations = resolved
                .Where(i => i.ResolvedUtc is not null && i.ResolvedUtc.Value >= i.Created)
                .Select(i => (i.ResolvedUtc!.Value - i.Created).Ticks)
                .ToList();
            if (durations.Count == 0) return null;
            return TimeSpan.FromTicks((long)durations.Average());
        }
    }
}
