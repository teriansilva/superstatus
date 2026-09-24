# SuperStatus design system — tactical HUD

Canonical reference for the SuperStatus.Web visual language: the design
tokens, the Blazor primitives, and the conventions for composing them.
This supersedes the Phase-0 exploration in `mockups/DESIGN.md` (kept only
as a historical record of the design decisions). **The live primitives
under `SuperStatus.Web/Components/Hud/` plus this document are the source
of truth.**

Adopted across the app in issue #95 (Phases 0–5).

## Principle

A status page is one of the few product surfaces where a tactical-readout
aesthetic is *more honest than ornamental* — incidents, uptime, latency,
and service health all read naturally as telemetry. The language is dark
frosted-glass panels with corner brackets, monospace type, and
semantic-colour LEDs. Restraint over neon: hairline borders, tabular
numerics, and motion only where it reinforces "this is live".

## Tokens

Declared on `:root` in `SuperStatus.Ui.Shared/wwwroot/hud-theme.css` (served to
every front-end as `_content/SuperStatus.Ui.Shared/hud-theme.css`), which also
remaps the MudBlazor (`--mud-palette-*`) and Fluent UI (`--neutral-*`,
`--accent-*`) variables onto these so off-the-shelf components inherit the
palette without per-component overrides.

```css
--bg-0: #0d0e10;          /* canvas base */
--bg-1: #131418;          /* nav, topbar, footer */
--bg-2: #1a1c21;          /* form fields, chips */

--text-1: #e8e9ec;        /* primary */
--text-2: #a7abb3;        /* secondary */
--text-3: #6f747d;        /* labels, hints */

--glass:        rgba(255,255,255,0.022);
--glass-strong: rgba(255,255,255,0.035);
--line:        rgba(255,255,255,0.08);
--line-strong: rgba(255,255,255,0.14);

--accent:      #3fbf6f;   /* SuperStatus brand — desaturated tactical green */
--accent-soft: rgba(63,191,111,0.55);
--accent-glow: rgba(63,191,111,0.20);

/* Semantic status — INDEPENDENT of the brand accent. Never substitute the
   accent for these. */
--status-up:        #3fbf6f;
--status-degraded:  #f59e0b;
--status-down:      #c02020;
--status-unknown:   #6f747d;

--font-mono: "JetBrains Mono", "Fira Mono", ui-monospace, monospace;
```

**Accent vs status.** The brand accent (`#3fbf6f`) intentionally coincides
with `--status-up`: SuperStatus is "operational by default". Incident /
down red (`--status-down`) and degraded amber (`--status-degraded`) live
in dedicated tokens and are *never* replaced by the accent, so a healthy
panel never reads as alarmed and a critical panel always does.

**Numerics.** Every metric uses `font-variant-numeric: tabular-nums` so
columns of latency / uptime / counts stay scannable.

## Primitives

All under `SuperStatus.Web/Components/Hud/`. Compose these rather than
hand-pasting bracket spans or one-off CSS.

| Primitive | Renders | Key params |
|---|---|---|
| `HudPanel` | Frosted panel + 4 corner brackets | `Accent` = `""` (green) / `primary` (white, one hero per page) / `critical` (red, active down/incident only); `ExtraClass`; splat attrs merged with built-in classes |
| `HudLed` | Pulsing status dot | `Status` = `up` / `degraded` / `down` / `unknown` |
| `HudTag` | Uppercase chip + optional LED | `Status` (LED), `Tone` = `accent` / `critical` |
| `HudCallsign` | `LABEL // value // meta` header | `Label`, `Value` (required), `Meta` |
| `HudTelemetryStrip` + `HudChip` | Dashed metric strip + chips | chip `K`, `V`, `Tone` = `accent` / `degraded` / `critical` |
| `HudKeyValueGrid` + `HudKeyValue` | Two-column tabular grid | `K`, `V`, `Led`, `Tone` = `up` / `degraded` / `down` |
| `UptimeStrip` | N-cell day strip | `Days` = list of `up` / `degraded` / `down` / `gap` (unknown → `gap`, never silently `up`) |
| `Ambience/HudMissionTimer` | `T+ Nd HH:MM:SS` since-anchor counter | `SinceUtc` (null → `—`), `Suffix` |

## Surface conventions

- **One `primary` (white-bracket) frame per page** — the hero / "you are
  here" panel. Everything else is the default accent frame.
- **`critical` frame is reserved for active failure** — a service that is
  currently down, or an open incident detail. Never for warnings; degraded
  uses amber tone on a default frame.
- **Status colour is load-bearing** — `up`/`degraded`/`down` always carry
  the semantic tokens; the `FailType → state` mapping is centralised
  (`PublicStatusApi.MapStateLabel`, `ServiceCardState`,
  `StatusCheckService` aggregation) so the UI and the public API never
  disagree.
- **Admin is calm** — no motion, no flashy LEDs on `/admin`; operators get
  a quiet operations console.
- **Motion respects `prefers-reduced-motion: reduce`** everywhere — LED
  pulses stop; data stays accurate.
- **The footer is static + operator-managed** (#170) — `FooterBar` renders the
  operator-set text, links, and a toggleable Admin link from site settings;
  there is no rotating classification ambience.
- **Empty / loading / unavailable states are first-class** — every data
  surface renders an honest empty/loading/fallback rather than a crash or
  misleading zeros (e.g. the dashboard hero shows "TELEMETRY //
  Unavailable" when `/statuscheck/summary` is unreachable).

## Surfaces (delivered in #95)

| Surface | Component | Phase |
|---|---|---|
| Token layer + primitives | `wwwroot/hud-theme.css`, `Components/Hud/*` | 1 |
| Shell (header / footer / error / layout) | `Components/Layout/*`, `Components/Pages/Error.razor` | 2 |
| Home hero summary | `Components/StatusCheckOverview/HudDashboardHero.razor` | 3a |
| Incident log | `Components/IncidentOverview/IncidentList.razor` | 3b |
| Service card frame | `Components/StatusCheckOverview/StatusCheckOverviewCard.razor` | 3c |
| Operator console | `Components/Pages/Admin.razor` | 4 |
| Ambience (mission timer) | `Components/Hud/Ambience/*` | (#109) |
| Operator-managed footer | `Components/Layout/FooterBar.razor` | (#170) |

## Responsive (delivered in #152)

The HUD adapts across one breakpoint axis, applied consistently in
`hud-theme.css` (the shell + primitives) and the component scoped CSS:

| Range | Behaviour |
|---|---|
| **> 900px** (desktop) | Full `.app` grid: 240px `.nav` rail + wide `.main`; full topbar (brand, status tag, clock, mission timer, actions). |
| **≤ 900px** (tablet) | `.nav` becomes an **off-canvas drawer** toggled by the topbar hamburger; the topbar drops the mission timer; service rows drop the latency stat but keep the 30-day strip. |
| **≤ 600px** (mobile) | Topbar drops the clock + status tag; service rows go LED + name + actions; hero telemetry chips become a 2-column grid; footer + section heads + page header wrap. |

Rules of the road:

- **No horizontal overflow.** Flexible grid/flex items that may hold long
  content carry `min-width: 0` (and `minmax(0, …)` tracks); long unbreakable
  strings (URLs) use `overflow-wrap: anywhere` or ellipsis. This is enforced
  by the visual pipeline (below), not left to inspection.
- **Drawer a11y.** The hamburger is a labelled `<button>` with
  `aria-expanded` + `aria-controls="primary-nav"`; the drawer closes on
  Escape, backdrop click, and nav-link selection; focus moves into the drawer
  on open and back to the toggle on close; body scroll locks while open; the
  closed drawer's links leave the tab order (`visibility: hidden`).
- **Motion.** Drawer + backdrop transitions respect
  `prefers-reduced-motion: reduce`.

## Accessibility

- WCAG AA contrast on the dark surfaces; the green accent passes on
  `--bg-1`.
- Keyboard focus is a 1.5px accent reticle outline (`:focus-visible`), not
  the default UA ring, on every interactive element.
- **Touch targets**: the hamburger and the mobile drawer links are ≥44px;
  topbar icon buttons are 32px (≥ the 24px WCAG 2.5.8 minimum).
- `.sr-only` utility provided for visually-hidden headings.

## Testing

Primitives + surfaces are covered by bUnit tests in `SuperStatus.Tests`
(`HudPrimitivesTests`, `HudShellTests`, `HudResponsiveNavTests`,
`HudDashboardHeroTests`, `UptimeStripTests`, `IncidentListReskinTests`,
`ServiceCardStateTests`, `AdminConsoleTests`, `HudAmbiencePhase2Tests`). Each
asserts the rendered class vocabulary + state mapping so the tokens/tones
can't silently regress.

UI PRs additionally ship screenshots from `web/visual/` at five viewports —
**1440 / 1024 / 768 / 414 / 320** — including a drawer-open capture. The
capture script asserts `document.scrollWidth ≤ viewport` for every non-grid
area and fails the run (naming the offending elements) if anything overflows
horizontally, so the responsive layout can't silently regress.
