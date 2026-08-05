// Issue #152 Phase 1: tiny helper for the mobile nav drawer.
//
// Blazor owns the open/close STATE; this only does the two things the circuit
// can't express declaratively across components: move keyboard focus when the
// drawer opens/closes, and lock body scroll while it's open. Kept global +
// null-safe so an early (pre-circuit) call is a harmless no-op.
window.hudDrawer = {
  scrollLock: function (on) {
    document.body.classList.toggle('nav-scroll-lock', !!on);
  },
  // Move focus to the first nav link when the drawer opens.
  focusFirst: function () {
    var el = document.querySelector('#primary-nav .nav-link');
    if (el) el.focus();
  },
  // Restore focus to the hamburger toggle when the drawer closes.
  focusToggle: function () {
    var el = document.querySelector('.hud-menu-btn');
    if (el) el.focus();
  }
};
