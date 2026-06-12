namespace SuperStatus.Data.ViewModels
{
    /// <summary>
    /// Issue #249 (epic #248): the update status the operator console renders.
    /// Composed by the api from the running version + persisted last-check state +
    /// the auto-update env flag. Read-only — the console never applies an update.
    /// </summary>
    public class UpdateStatusViewModel
    {
        // Status values for <see cref="Status"/>.
        public const string StatusUpToDate = "uptodate";
        public const string StatusUpdateAvailable = "available";
        public const string StatusEdge = "edge";          // dev/main build — no release comparison
        public const string StatusUnknown = "unknown";    // never had a successful check yet

        /// <summary>The running version (normalized SemVer, or "edge"/"0.0.0-dev").</summary>
        public string CurrentVersion { get; set; } = string.Empty;

        /// <summary>Release channel: "latest" or "edge".</summary>
        public string Channel { get; set; } = string.Empty;

        /// <summary>Comparison verdict — independent of the last check's success
        /// (see <see cref="LastCheckError"/> for the "couldn't check" overlay).</summary>
        public string Status { get; set; } = StatusUnknown;

        /// <summary>Whether the nightly check is enabled.</summary>
        public bool CheckEnabled { get; set; } = true;

        public DateTime? LastCheckedUtc { get; set; }

        /// <summary>Last-known-good latest release version (null until the first successful check).</summary>
        public string? LatestVersion { get; set; }

        public string? LatestNotesUrl { get; set; }

        /// <summary>Non-null when the most recent check couldn't complete (calm
        /// "couldn't check" state; the last-known version is retained).</summary>
        public string? LastCheckError { get; set; }

        /// <summary>Whether automatic updates (the opt-in Watchtower overlay, Phase 2)
        /// are active — read from the SUPERSTATUS_AUTOUPDATE env flag. Display only.</summary>
        public bool AutoUpdateActive { get; set; }

        /// <summary>The guided manual upgrade command shown in the panel.</summary>
        public string UpgradeCommand { get; set; } = "docker compose pull && docker compose up -d";
    }
}
