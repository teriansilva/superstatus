namespace SuperStatus.Data.ViewModels
{
    /// <summary>
    /// Issue #249 (epic #248): the update status the operator console renders.
    /// Composed by the api from the running version, the persisted last-check state,
    /// and (since #334) the persisted auto-update policy. Never carries the updater
    /// token.
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

        /// <summary>Whether the nightly update check runs.</summary>
        public bool CheckEnabled { get; set; } = true;

        public DateTime? LastCheckedUtc { get; set; }

        /// <summary>Last-known-good latest release version (null until the first successful check).</summary>
        public string? LatestVersion { get; set; }

        public string? LatestNotesUrl { get; set; }

        /// <summary>Non-null when the most recent check couldn't complete (calm
        /// "couldn't check" state; the last-known version is retained).</summary>
        public string? LastCheckError { get; set; }

        /// <summary>Issue #334: whether the operator has switched automatic updates on.
        /// A persisted setting, editable from this panel — not an env flag, and not
        /// Watchtower's own schedule (it has none). Off ⇒ nothing updates unattended.</summary>
        public bool AutoUpdateEnabled { get; set; }

        /// <summary>Issue #334: the daily time the automatic update fires, in UTC.
        /// v1 is UTC-only and the panel labels it as such.</summary>
        public TimeOnly AutoUpdateTimeUtc { get; set; } = new(3, 0);

        /// <summary>Issue #334: when an automatic update was last accepted by the
        /// updater (null = never). Only ever stamped on acceptance.</summary>
        public DateTime? AutoUpdateLastRunUtc { get; set; }

        /// <summary>Issue #311/#334: whether the console can apply an update in-app via
        /// the "Update now" button. True whenever the update engine is present — which
        /// it is on a default install. False only when the operator opted out
        /// (SUPERSTATUS_UPDATE_ENGINE=none), in which case the panel falls back to the
        /// guided command. The app never touches the Docker socket either way: it calls
        /// Watchtower's authenticated HTTP API. Never carries the token.</summary>
        public bool CanApplyInApp { get; set; }

        /// <summary>The guided manual upgrade command — the advanced/fallback path.</summary>
        public string UpgradeCommand { get; set; } = "docker compose pull && docker compose up -d";
    }

    /// <summary>
    /// Issue #311: result of POST /api/updates/apply — whether the update trigger was
    /// accepted by Watchtower, plus a human-readable error when it wasn't. "Accepted"
    /// means the trigger was accepted, not that the update has finished (the app
    /// restarts out from under the request). Never carries the token.
    /// </summary>
    public class UpdateApplyResult
    {
        public bool Accepted { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Issue #334: body of POST /api/updates/auto — the operator's auto-update policy.
    /// <see cref="Time"/> is a 24-hour UTC time of day; anything that isn't one is
    /// rejected rather than silently coerced to midnight.
    /// </summary>
    public class AutoUpdateRequest
    {
        public bool Enabled { get; set; }

        /// <summary>Daily run time in UTC, "HH:mm" (e.g. "03:00").</summary>
        public string Time { get; set; } = "03:00";

        /// <summary>The canonical wire format — what the client sends.</summary>
        public const string WireFormat = "HH\\:mm";

        /// <summary>
        /// Accepted inputs, both unambiguous 24-hour times. The seconds-bearing form is
        /// not decoration: a browser's <c>&lt;input type="time"&gt;</c> reports its value
        /// to Blazor as <c>HH:mm:ss</c>, so the panel would otherwise reject every
        /// schedule the operator picks. Anything else (12-hour, "2:30", "25:00",
        /// culture-specific separators) is a validation error.
        /// </summary>
        private static readonly string[] AcceptedFormats = [WireFormat, "HH\\:mm\\:ss"];

        /// <summary>
        /// Strictly parse a 24-hour UTC time of day. Shared by the API endpoint, the
        /// Blazor panel, and the demo handler so they cannot drift apart.
        /// </summary>
        public static bool TryParseTime(string? value, out TimeOnly timeUtc)
            => TimeOnly.TryParseExact(
                value,
                AcceptedFormats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out timeUtc);
    }
}
