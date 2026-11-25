// Issue #177: viewer-selectable theme (light / dark / system), persisted in
// localStorage. The HUD palette is driven by data-theme="light|dark" on <html>
// (CSS token overrides in hud-theme.css). System mode follows the OS
// prefers-color-scheme and re-applies live. A pre-paint bootstrap in App.razor
// sets the initial attribute to avoid a flash; this module owns runtime changes
// and notifies .NET (ThemeProvider) so MudBlazor surfaces follow.
(function () {
  const KEY = 'hudTheme';
  const MODES = ['system', 'light', 'dark'];
  const mql = window.matchMedia('(prefers-color-scheme: dark)');
  let dotnetRef = null;

  // localStorage can throw (SecurityError) when storage is blocked — private
  // mode, disabled cookies, sandboxed frames. Stay non-throwing everywhere:
  // reads fall back to 'system', writes are best-effort no-ops.
  function readStored() {
    try { return localStorage.getItem(KEY); } catch (e) { return null; }
  }
  function writeStored(m) {
    try { localStorage.setItem(KEY, m); } catch (e) { /* storage blocked */ }
  }

  function mode() {
    const m = readStored();
    return MODES.includes(m) ? m : 'system';
  }

  function effective(m) {
    m = m || mode();
    return m === 'system' ? (mql.matches ? 'dark' : 'light') : m;
  }

  function apply(m) {
    const eff = effective(m);
    document.documentElement.setAttribute('data-theme', eff);
    if (dotnetRef) {
      // Best-effort: tell ThemeProvider to flip MudBlazor's dark mode.
      try { dotnetRef.invokeMethodAsync('OnEffectiveThemeChanged', eff); } catch (e) { /* circuit gone */ }
    }
    return eff;
  }

  // Re-apply when the OS preference changes, but only while in system mode.
  mql.addEventListener('change', function () {
    if (mode() === 'system') apply('system');
  });

  window.hudTheme = {
    /** Current mode: 'system' | 'light' | 'dark'. */
    get: function () { return mode(); },
    /** Resolved theme actually applied: 'light' | 'dark'. */
    effective: function () { return effective(); },
    /** Set + persist a mode; returns the resolved effective theme. */
    setMode: function (m) {
      if (!MODES.includes(m)) m = 'system';
      writeStored(m);
      apply(m);
      return m;
    },
    /** Advance system → light → dark → system; returns the new mode. */
    cycle: function () {
      const next = MODES[(MODES.indexOf(mode()) + 1) % MODES.length];
      return this.setMode(next);
    },
    /** Register the ThemeProvider .NET ref + return the current effective theme. */
    init: function (ref) {
      dotnetRef = ref;
      return effective();
    },
  };

  // Reconcile the live attribute with storage in case the pre-paint bootstrap
  // and this module disagree (e.g. storage written in another tab).
  apply(mode());
})();
