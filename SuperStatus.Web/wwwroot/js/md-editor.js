// #349: incident Description WYSIWYG markdown editor.
//
// Wraps the vendored TOAST UI Editor (lib/toastui/, MIT) behind a small global
// object — the same interop style as js/push.js / js/hud-drawer.js. The heavy
// (~560KB) editor bundle + CSS are loaded LAZILY on first init, so they never
// ship on the public status page; only the operator incident dialog pulls them in.
//
// TOAST UI gives WYSIWYG editing *and* a built-in "Markdown ⇄ WYSIWYG" mode
// switch — the "write raw markdown too" half of the requirement — for free.
window.mdEditor = (function () {
    'use strict';

    var editors = {};      // handle id -> Editor instance
    var libPromise = null; // one-shot lazy load of the vendored bundle + CSS

    function addCss(href) {
        if (document.querySelector('link[data-mde="' + href + '"]')) return;
        var l = document.createElement('link');
        l.rel = 'stylesheet';
        l.href = href;
        l.setAttribute('data-mde', href);
        document.head.appendChild(l);
    }

    function loadLib() {
        if (libPromise) return libPromise;
        addCss('lib/toastui/toastui-editor.css');
        addCss('lib/toastui/toastui-editor-dark.css');
        libPromise = new Promise(function (resolve, reject) {
            if (window.toastui && window.toastui.Editor) { resolve(); return; }
            var s = document.createElement('script');
            s.src = 'lib/toastui/toastui-editor-all.min.js';
            s.onload = function () { resolve(); };
            s.onerror = function () { libPromise = null; reject(new Error('toastui editor failed to load')); };
            document.head.appendChild(s);
        });
        return libPromise;
    }

    // Track the app's light/dark choice (#177 sets data-theme on <html>).
    function isDark() {
        return document.documentElement.getAttribute('data-theme') !== 'light';
    }

    return {
        // el: the container ElementReference; id: a stable handle string.
        init: async function (el, id, initialValue) {
            await loadLib();
            if (editors[id]) { try { editors[id].destroy(); } catch (e) { /* ignore */ } delete editors[id]; }
            var Editor = window.toastui.Editor;
            editors[id] = new Editor({
                el: el,
                height: '260px',
                initialEditType: 'wysiwyg',   // start WYSIWYG; bottom switch flips to Markdown
                previewStyle: 'tab',          // compact: preview is a tab in markdown mode, not a split
                initialValue: initialValue || '',
                usageStatistics: false,
                autofocus: false,
                theme: isDark() ? 'dark' : 'default',
                toolbarItems: [
                    ['heading', 'bold', 'italic', 'strike'],
                    ['ul', 'ol', 'quote'],
                    ['code', 'codeblock', 'link'],
                ],
            });
            return true;
        },

        // Read the current content back as markdown (what we persist).
        getValue: function (id) {
            return editors[id] ? editors[id].getMarkdown() : '';
        },

        destroy: function (id) {
            if (editors[id]) { try { editors[id].destroy(); } catch (e) { /* ignore */ } delete editors[id]; }
        },
    };
})();
