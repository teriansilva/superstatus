using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Services;

namespace SuperStatus.ApiService;

/// <summary>
/// Issue #293 Phase A: operator-only CRUD for SLA targets, plus the backfill
/// preview. Sibling of <see cref="LinkedTargetsAdminApi"/> — same shape, same
/// guard semantics (409 + usage while linked, DB FK as the backstop). The
/// SlaListPanel on the console's Checks tab consumes these (Phase C).
/// </summary>
public static class SlaAdminApi
{
    public static void MapSlaAdminApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/slas", async (IRepository<Sla> slas, IStatusCheckRepository checks, CancellationToken ct) =>
        {
            var all = (await slas.GetMany(ct)).OrderBy(s => s.Id).ToList();
            var titles = await checks.GetSlaLinkedCheckTitlesAsync(ct);
            return Results.Ok(all.Select(s => new SlaViewModel(s, titles.GetValueOrDefault(s.Id))).ToList());
        }).RequireAuthorization();

        // Create (Id 0) or update (Id > 0) in one POST, like /statuscheck/edit.
        // IsDefault is NOT writable here — only the transactional PATCH below
        // moves the default, so the exactly-one invariant has a single writer.
        app.MapPost("/admin/slas", async (SlaViewModel body, IRepository<Sla> slas, CancellationToken ct) =>
        {
            string? invalid = ValidateSla(body);
            if (invalid is not null)
                return Results.UnprocessableEntity(new { message = invalid });

            if (body.Id > 0)
            {
                var existing = await slas.FirstOrDefault(s => s.Id == body.Id, ct);
                if (existing is null) return Results.NotFound();
                existing.Name = body.Name.Trim();
                existing.TargetUptimePercent = body.TargetUptimePercent;
                existing.CriticalUptimePercent = body.CriticalUptimePercent;
                existing.SlowThresholdMs = body.SlowThresholdMs;
                await slas.UpdateAndSave(existing, ct);
                return Results.Ok(new SlaViewModel(existing, null));
            }

            var entity = new Sla
            {
                Name = body.Name.Trim(),
                TargetUptimePercent = body.TargetUptimePercent,
                CriticalUptimePercent = body.CriticalUptimePercent,
                SlowThresholdMs = body.SlowThresholdMs,
                IsDefault = false,
                CreatedUtc = DateTime.UtcNow,
            };
            await slas.AddAndSave(entity, ct);
            return Results.Ok(new SlaViewModel(entity, null));
        }).RequireAuthorization();

        app.MapDelete("/admin/slas/{id:long}", async (long id, IRepository<Sla> slas, IStatusCheckRepository checks, CancellationToken ct) =>
        {
            var sla = await slas.FirstOrDefault(s => s.Id == id, ct);
            if (sla is null) return Results.NotFound();

            var titles = (await checks.GetSlaLinkedCheckTitlesAsync(ct)).GetValueOrDefault(id);
            switch (ValidateDelete(sla, titles))
            {
                // The default is categorically undeletable (new checks resolve
                // to it) — switch the default first, then delete.
                case SlaDeleteVerdict.IsDefault:
                    return Results.UnprocessableEntity(new { message = $"SLA '{sla.Name}' is the default; make another SLA the default first." });

                // Delete guard: a referenced SLA can't be deleted (409 carries
                // the same LinkedEntitySummary shape as the #291 surfaces).
                // The DB RESTRICT FK is the backstop.
                case SlaDeleteVerdict.Linked:
                    return Results.Conflict(new
                    {
                        message = $"SLA '{sla.Name}' is linked to {titles!.Count} check(s); relink them first.",
                        usage = LinkedEntitySummary.From(titles),
                    });
            }

            await slas.DeleteAndSave(sla, ct);
            return Results.NoContent();
        }).RequireAuthorization();

        // Transactional default switch (clear old + set new in one transaction;
        // the partial unique index backs the invariant at the data layer).
        app.MapPatch("/admin/slas/{id:long}/default", async (long id, ISlaNormalizationService slaService, CancellationToken ct) =>
        {
            bool found = await slaService.SetDefaultAsync(id, ct);
            return found ? Results.Ok(new { id, isDefault = true }) : Results.NotFound();
        }).RequireAuthorization();

        // The would-be legacy-ms → SLA translation, computed without writing
        // (the real backfill auto-applies at startup — same split as the #291
        // linked-targets preview).
        app.MapGet("/admin/backfill/slas/preview", async (ISlaNormalizationService slaService, CancellationToken ct) =>
        {
            var summary = await slaService.BackfillAsync(dryRun: true, ct);
            return Results.Ok(summary);
        }).RequireAuthorization();
    }

    /// <summary>#293: DELETE outcome — IsDefault wins over Linked (a default is
    /// almost always referenced too; the 422 names the real blocker).</summary>
    public enum SlaDeleteVerdict { Ok, IsDefault, Linked }

    public static SlaDeleteVerdict ValidateDelete(Sla sla, List<string>? linkedCheckTitles)
        => sla.IsDefault ? SlaDeleteVerdict.IsDefault
            : linkedCheckTitles is { Count: > 0 } ? SlaDeleteVerdict.Linked
            : SlaDeleteVerdict.Ok;

    /// <summary>#293: the SLA write rules — a name is required, percents are
    /// 0–100 with Critical ≤ Target, and the slow threshold is ≥ 1 ms.</summary>
    public static string? ValidateSla(SlaViewModel body)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return "name is required";
        if (body.TargetUptimePercent is < 0 or > 100)
            return "targetUptimePercent must be between 0 and 100";
        if (body.CriticalUptimePercent is < 0 or > 100)
            return "criticalUptimePercent must be between 0 and 100";
        if (body.CriticalUptimePercent > body.TargetUptimePercent)
            return "criticalUptimePercent must be less than or equal to targetUptimePercent";
        if (body.SlowThresholdMs < 1)
            return "slowThresholdMs must be at least 1";
        return null;
    }
}
