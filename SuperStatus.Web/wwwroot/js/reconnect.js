// Auto-reconnect for the Blazor Server reconnect toast (#348, builds on the
// custom toast from #162). Blazor toggles state classes on
// #components-reconnect-modal; there was no JS, so the terminal "Connection lost"
// state was manual-reload-only. This makes it recover on its own.
//
// State classes Blazor sets on the toast element:
//   components-reconnect-show     — transient: Blazor is retrying. Left alone
//                                   (spinner + "Reconnecting…").
//   components-reconnect-failed   — client gave up retrying, but the circuit MAY
//                                   still be alive server-side. Try ONE soft
//                                   Blazor.reconnect() (no reload → keeps page
//                                   state) and, as a fallback, count down to a
//                                   full reload.
//   components-reconnect-rejected — server rejected reconnection; the circuit is
//                                   gone and only a full reload recovers. Count
//                                   down to reload.
//   components-reconnect-hide     — reconnected; cancel everything.
//
// The countdown is written into .reconnect-toast__countdown; the manual
// "Reload now" link stays for an immediate reload. A sessionStorage guard caps
// auto-reloads in a short window so a persistently-down server can't drive a
// reload storm; it clears once the page has been healthily connected for a while
// (STABLE_MS) — i.e. a reload actually worked — or on a live reconnect.
(function () {
  'use strict';

  var COUNTDOWN_SECONDS = 5;      // visible countdown before an auto-reload
  var TICK_MS = 1000;             // countdown tick cadence
  var GUARD_MAX = 3;              // max auto-reloads ...
  var GUARD_WINDOW_MS = 60000;    // ... within this rolling window
  var STABLE_MS = 15000;          // healthy this long ⇒ clear the storm guard
  var GUARD_KEY = 'ss-reconnect-reloads';

  var reloadFn = function () { location.reload(); };

  var timer = null;            // countdown interval handle
  var stabilityTimer = null;   // "connection is healthy" timer handle
  var remaining = 0;
  var softTried = false;       // one soft reconnect per terminal episode

  function modal() { return document.getElementById('components-reconnect-modal'); }
  function countdownEl() {
    var m = modal();
    return m ? m.querySelector('.reconnect-toast__countdown') : null;
  }
  function setText(txt) {
    var c = countdownEl();
    if (c) c.textContent = txt;
  }

  function stateOf(m) {
    if (m.classList.contains('components-reconnect-rejected')) return 'rejected';
    if (m.classList.contains('components-reconnect-failed')) return 'failed';
    if (m.classList.contains('components-reconnect-show')) return 'show';
    return 'hidden';
  }

  // ---- reload-loop guard -------------------------------------------------
  function readGuard() {
    try {
      var g = JSON.parse(sessionStorage.getItem(GUARD_KEY) || '');
      // NB: coerce with +, never `| 0` — `first` is a Date.now() ms timestamp
      // (~1.7e12) and a bitwise OR would truncate it to 32 bits, corrupting the
      // rolling window and defeating the guard.
      return { count: +g.count || 0, first: +g.first || 0 };
    } catch (e) { return { count: 0, first: 0 }; }
  }
  function writeGuard(g) {
    try { sessionStorage.setItem(GUARD_KEY, JSON.stringify(g)); } catch (e) { /* storage blocked */ }
  }
  function clearGuard() {
    try { sessionStorage.removeItem(GUARD_KEY); } catch (e) { /* ignore */ }
  }
  // May we auto-reload right now? Records the reload against the window if yes.
  function mayAutoReload() {
    var now = Date.now();
    var g = readGuard();
    if (!g.first || (now - g.first) > GUARD_WINDOW_MS) g = { count: 0, first: now };
    if (g.count >= GUARD_MAX) return false;
    g.count += 1;
    writeGuard(g);
    return true;
  }

  // ---- "connection healthy" timer ---------------------------------------
  function cancelStability() {
    if (stabilityTimer !== null) { clearTimeout(stabilityTimer); stabilityTimer = null; }
  }
  function armStability() {
    cancelStability();
    stabilityTimer = setTimeout(function () { clearGuard(); stabilityTimer = null; }, STABLE_MS);
  }

  // ---- countdown ---------------------------------------------------------
  function stopCountdown() {
    if (timer !== null) { clearInterval(timer); timer = null; }
    softTried = false;
    setText('');
  }
  function tick() {
    remaining -= 1;
    if (remaining <= 0) { stopCountdown(); reloadFn(); return; }
    setText(' — reloading in ' + remaining + 's');
  }
  function startCountdown() {
    if (timer !== null) return;                 // already counting this episode
    if (!mayAutoReload()) {                      // storm guard tripped → manual only
      setText(' — auto-reload paused; reload to reconnect');
      return;
    }
    remaining = COUNTDOWN_SECONDS;
    setText(' — reloading in ' + remaining + 's');
    timer = setInterval(tick, TICK_MS);
  }

  function onTerminal(kind) {
    if (kind === 'failed' && !softTried) {
      softTried = true;
      // Circuit may still be alive — restore it without a reload (keeps page
      // state). If it doesn't take, the countdown below reloads.
      try {
        if (window.Blazor && typeof window.Blazor.reconnect === 'function') window.Blazor.reconnect();
      } catch (e) { /* ignore */ }
    }
    startCountdown();
  }

  function onState(s) {
    if (s === 'failed' || s === 'rejected') {
      cancelStability();       // in trouble — do NOT clear the guard
      onTerminal(s);
    } else {
      stopCountdown();         // 'show' (transient) or 'hidden' (reconnected)
      if (s === 'hidden') armStability();   // healthy (fresh load or reconnect) ⇒ guard clears after STABLE_MS
      else cancelStability();               // 'show' — we might yet go terminal
    }
  }

  function observe() {
    var m = modal();
    if (!m) return;
    new MutationObserver(function () { onState(stateOf(m)); })
      .observe(m, { attributes: true, attributeFilter: ['class'] });
    onState(stateOf(m));   // handle a state already present at load
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', observe);
  } else {
    observe();
  }

  // Test seam (real-browser spec, #348): tune timings / stub reload so the
  // behaviour can be driven in milliseconds instead of real seconds. No effect
  // in production — nothing calls it there.
  window.__ssReconnect = {
    config: function (o) {
      o = o || {};
      if (typeof o.countdownSeconds === 'number') COUNTDOWN_SECONDS = o.countdownSeconds;
      if (typeof o.tickMs === 'number') TICK_MS = o.tickMs;
      if (typeof o.guardMax === 'number') GUARD_MAX = o.guardMax;
      if (typeof o.guardWindowMs === 'number') GUARD_WINDOW_MS = o.guardWindowMs;
      if (typeof o.stableMs === 'number') STABLE_MS = o.stableMs;
      if (typeof o.reload === 'function') reloadFn = o.reload;
    }
  };
})();
