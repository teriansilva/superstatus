using SuperStatus.Data.Entities;

namespace SuperStatus.Data.ViewModels
{
    /// <summary>
    /// Issue #293 Phase B: THE day classifier — one day's tick tally + the
    /// check's SLA targets → strip vocabulary ("gap" / "down" / "degraded" /
    /// "up"). Lives in Data so both the Web strips (via
    /// <c>UptimeCell</c>) and SuperStatus.Services (dashboard summary, day
    /// detail) share the single rule without a Services→Web reference.
    ///
    /// Contract (evaluated in order; down wins):
    ///   gap        — total == 0 (no samples)
    ///   down       — availability  &lt; CriticalUptimePercent, where
    ///                availability = (total − down) / total (hard failures only:
    ///                down = bad-status + unreachable)
    ///   degraded   — health &lt; TargetUptimePercent, where
    ///                health = (total − down − degraded) / total (== the day
    ///                tooltip's UptimePct)
    ///   up         — otherwise
    ///
    /// Boundary semantics are strict-less-than: a day at availability EXACTLY
    /// == Critical is NOT down; health EXACTLY == Target IS "up". To keep those
    /// boundaries exact the ratios are cross-multiplied in decimal
    /// ((total − down) · 100 vs total · critical) — no division, and the
    /// double→decimal conversion recovers the operator-entered value (99.9)
    /// instead of its binary approximation, so e.g. 999/1000 vs 99.9% compares
    /// equal rather than epsilon-under.
    ///
    /// With the Default SLA (Target 100 / Critical 100) this is bit-identical
    /// to the historical worst-of-tick rule: any down tick → availability &lt;
    /// 100% → "down"; any slow tick on an otherwise clean day → "degraded";
    /// clean day → "up".
    /// </summary>
    public static class SlaDayClassifier
    {
        public static string Classify(int total, int down, int degraded, double targetUptimePercent, double criticalUptimePercent)
        {
            if (total <= 0) return "gap";

            // availability < critical/100  ⇔  (total − down)·100 < total·critical
            if ((total - down) * 100m < total * (decimal)criticalUptimePercent) return "down";

            // health < target/100  ⇔  (total − down − degraded)·100 < total·target
            if ((total - down - degraded) * 100m < total * (decimal)targetUptimePercent) return "degraded";

            return "up";
        }

        /// <summary>Convenience over a loaded <see cref="Sla"/> link. A null
        /// SLA (pre-backfill edge: navigation not loaded) falls back to the
        /// behavior-identical 100/100, i.e. the historical worst-of-tick rule.</summary>
        public static string Classify(int total, int down, int degraded, Sla? sla)
            => Classify(total, down, degraded, sla?.TargetUptimePercent ?? 100, sla?.CriticalUptimePercent ?? 100);

        /// <summary>#293 Phase D: window-level compliance — does tick-level
        /// uptime (ok / total) meet the SLA target? Same decimal
        /// cross-multiplication as <see cref="Classify(int,int,int,double,double)"/>
        /// so an exact-target boundary (e.g. 95 ok of 100 vs a 95% target, or
        /// 999/1000 vs 99.9%) compares COMPLIANT rather than epsilon-under.
        /// total == 0 answers false — callers render that as "no data", not
        /// as a breach.</summary>
        public static bool MeetsTarget(long okTicks, long totalTicks, double targetUptimePercent)
            => totalTicks > 0 && okTicks * 100m >= totalTicks * (decimal)targetUptimePercent;
    }
}
