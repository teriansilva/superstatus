namespace SuperStatus.Services.Providers
{
    /// <summary>One pasted line that will become a check.</summary>
    /// <param name="Input">The raw line as pasted (for the per-row result).</param>
    /// <param name="CanonicalTarget">The provider's canonicalised target value.</param>
    /// <param name="Title">The derived check title (prefix + naming template).</param>
    public sealed record PlannedCheck(string Input, string CanonicalTarget, string Title);

    /// <summary>One pasted line that was dropped, with the operator-facing reason.</summary>
    public sealed record SkippedLine(string Input, string Reason);

    /// <summary>The classified paste: what will be created vs skipped (and why).</summary>
    public sealed record BatchPlan(IReadOnlyList<PlannedCheck> Valid, IReadOnlyList<SkippedLine> Skipped);

    /// <summary>
    /// Epic #342: pure, side-effect-free planning of a batch paste — parse each line via
    /// the provider, canonicalise, dedup (within the paste AND against existing checks by
    /// canonical target), and derive a title. No DB, no transaction: the caller feeds in the
    /// existing target set and persists the <see cref="BatchPlan.Valid"/> entries. Kept pure
    /// so the skip/dedup/naming rules are unit-testable without a database.
    /// </summary>
    public static class BatchCheckPlanner
    {
        public const string TemplateHost = "{host}";
        public const string TemplateHostPath = "{host}{path}";

        public static BatchPlan Plan(
            ICheckProvider provider,
            IReadOnlyList<string> rawLines,
            ISet<string> existingCanonicalTargets,
            string? namePrefix,
            string? nameTemplate)
        {
            var valid = new List<PlannedCheck>();
            var skipped = new List<SkippedLine>();
            // Within-paste dedup is canonical + case-insensitive (host casing already
            // normalised by canonicalisation; this guards path/query casing repeats).
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in rawLines)
            {
                var line = (raw ?? string.Empty).Trim();
                if (line.Length == 0)
                    continue; // blank lines are ignored, not "skipped"

                if (!provider.TryParseBatchTarget(line, out var canonical, out var error))
                {
                    skipped.Add(new SkippedLine(line, error ?? "invalid target"));
                    continue;
                }

                if (!seen.Add(canonical))
                {
                    skipped.Add(new SkippedLine(line, "duplicate in paste"));
                    continue;
                }

                if (existingCanonicalTargets.Contains(canonical))
                {
                    skipped.Add(new SkippedLine(line, "a check already monitors this target"));
                    continue;
                }

                valid.Add(new PlannedCheck(line, canonical, BuildTitle(canonical, namePrefix, nameTemplate)));
            }

            return new BatchPlan(valid, skipped);
        }

        /// <summary>Derive a check title from a canonical target: optional prefix + the host
        /// (default) or host+path. Matches the dialog's live preview so what the operator saw
        /// is what the server stores.</summary>
        public static string BuildTitle(string canonicalTarget, string? namePrefix, string? nameTemplate)
        {
            var host = BatchTargetParsing.DeriveHost(canonicalTarget);
            var body = nameTemplate == TemplateHostPath
                ? host + BatchTargetParsing.DerivePath(canonicalTarget)
                : host;
            return string.IsNullOrWhiteSpace(namePrefix) ? body : namePrefix + body;
        }
    }
}
