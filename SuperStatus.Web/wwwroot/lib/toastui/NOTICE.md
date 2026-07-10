# Vendored: TOAST UI Editor

- **Package:** `@toast-ui/editor` v3.2.2 — <https://github.com/nhn/tui.editor>
- **License:** MIT (© NHN Cloud FE Development Lab)
- **Files:**
  - `toastui-editor-all.min.js` — self-contained IIFE bundle built from the npm
    package's ESM entry with **prosemirror bundled in** (the published
    `dist/toastui-editor.js` externalizes prosemirror, so a plain `<script>` tag
    can't use it standalone). Built once with
    `esbuild entry.js --bundle --format=iife --minify`, where `entry.js` is
    `import Editor from '@toast-ui/editor'; window.toastui = { Editor };`.
  - `toastui-editor.css` — editor styles (from `dist/`).
  - `toastui-editor-dark.css` — dark theme (from `dist/theme/`).

Loaded lazily by `wwwroot/js/md-editor.js` only when the operator incident
dialog opens (#349), so the bundle never ships on the public status page.
