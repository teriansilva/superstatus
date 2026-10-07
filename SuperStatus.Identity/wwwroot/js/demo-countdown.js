// Issue #377 — tick the public demo's reset countdown.
//
// This script deliberately knows NOTHING about the reset schedule. The server stamps the
// next reset instant into a `data-reset-at` attribute from DemoMode.NextResetUtc — the
// same helper the Blazor banner uses — so the hourly cadence is defined in exactly one
// place (SuperStatus.ServiceDefaults/DemoMode.cs). All this does is format the difference
// between now and that instant.
//
// The anchor is the demo-only topbar chip (`.hud-demo-chip[data-demo-reset-at]`), found
// here by attribute rather than by element. It is NOT on <body>: Razor renders a null
// plain-HTML attribute as `attr=""` (omit-when-null is a tag-helper behaviour), so a
// <body> anchor would stamp an empty `data-demo-reset-at` onto every non-demo login page.
// Hanging it off an element that exists only in demo mode keeps a real deployment's markup
// free of any demo marker. Don't move it back.
//
// Loaded only when PUBLIC_DEMO=true (see _Layout.cshtml).
(function () {
    'use strict';

    var anchor = document.querySelector('[data-demo-reset-at]');
    if (!anchor) { return; }

    var resetAt = anchor.getAttribute('data-demo-reset-at');
    if (!resetAt) { return; }

    var target = Date.parse(resetAt);
    if (isNaN(target)) { return; }

    var targets = document.querySelectorAll('[data-demo-countdown]');
    if (targets.length === 0) { return; }

    function pad(n) { return n < 10 ? '0' + n : String(n); }

    function render() {
        var remainingMs = target - Date.now();

        // At T-0 the reset is running: containers are being recreated and the site is
        // about to 502 for a minute or two. Say so rather than counting into negatives.
        var text;
        if (remainingMs <= 0) {
            text = 'now';
        } else {
            var totalSeconds = Math.floor(remainingMs / 1000);
            text = pad(Math.floor(totalSeconds / 60)) + ':' + pad(totalSeconds % 60);
        }

        for (var i = 0; i < targets.length; i++) {
            targets[i].textContent = text;
        }

        // Past the reset instant there is nothing left to count. Stop the interval so a
        // tab left open overnight isn't ticking uselessly; the page will be reloaded
        // (or 502'd) by the reset anyway.
        if (remainingMs <= 0 && timer) {
            clearInterval(timer);
            timer = null;
        }
    }

    var timer = setInterval(render, 1000);
    render();
})();
