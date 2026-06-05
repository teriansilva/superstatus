// SuperStatus Grid renderer — Top-down v2 (issue #11, asset-pack-inspired rebuild)
//
// This is a full rewrite of the side-view renderer that shipped in Phase A.
// The user's reference image and the asset pack they shared are top-down 3/4
// pixel art (Stardew / Hyper Light Drifter vibe), not side-view facades.
// Visual cues sampled from the pack:
//   - Dark teal-purple base ground
//   - Hot-magenta neon road outlines, dark blue-grey asphalt
//   - Buildings drawn as roof slabs with a single visible south-facing wall
//     band beneath (implied 3/4), with edge highlights/shadows for depth
//   - Plaza tile with magenta ring + central hologram
//   - Cyan window dots, magenta/cyan neon signs
// All visuals are procedural — assets are inspiration only, none committed.
//
// Locked pipeline invariants from issue #11 §8-revised remain in force:
//   1. Each building owns ONE offscreen canvas representing its state.
//   2. Re-rendered on state change OR 8 fps animation tick.
//   3. Main RAF loop only blits — never draws building details directly.
//   4. Display upscale via CSS image-rendering: pixelated + integer scale.
//   5. All coordinates snap to integer pixels.
//   6. 8 fps animation tick separate from 60 fps render.
//
// Public surface (window.SuperStatusGrid) is unchanged so GridCanvas.razor
// and the modal flow work without modification.
(function () {
    'use strict';

    // =========================================================
    //                    LOCKED PALETTE
    //  Extended from Phase A with top-down-specific tokens.
    // =========================================================
    const PALETTE = {
        // Ambient ground (the void you see between blocks at low zoom).
        ground: {
            void:   '#0e0a18',
            base:   '#1c1832',
            mid:    '#2a2444',
            lit:    '#3c3458',
        },
        // Streets / asphalt — now with proper road profile (issue follow-up
        // after Grid v4 review). Sidewalk + curb + asphalt + dashed line,
        // patterned after the user's tile-B-01 cyberpunk top-down reference.
        street: {
            asphalt:    '#1a2238',
            asphaltHi:  '#27304a',
            asphaltLo:  '#11172a',  // 1-px asphalt grain (slightly darker)
            sidewalk:   '#3d3a52',  // cool grey, clearly lighter than asphalt
            sidewalkHi: '#52506a',  // tile-grout highlight
            sidewalkLo: '#2a2740',  // tile-grout shadow
            curb:       '#0a0a18',  // dark seam between sidewalk + asphalt
            edge:       '#ff3aa3',  // signature magenta neon trim, now subtle
            edgeDim:    '#7a1f54',
            lane:       '#d9d4e6',  // white dashed centerline (cool pale)
            cross:      '#d9d4e6',  // crosswalk stripes
        },
        // Plaza tile (city-centre landmark).
        plaza: {
            floor:    '#2c3a5c',
            floorHi:  '#3d527a',
            ring:     '#ff3aa3',
            ringDim:  '#a01f6a',
            tileLine: '#5a4f8a',
        },
        // Building roof palette — each archetype picks a base tone.
        roof: {
            // tenement (brick / utilitarian)
            tenement:     '#3d2848',
            tenementHi:   '#5a3a66',
            tenementLo:   '#2a1a32',
            // office-spire (steel-glass)
            office:       '#28344a',
            officeHi:     '#3e4c66',
            officeLo:     '#162236',
            // megacorp-tower (dark glass)
            megacorp:     '#1a1832',
            megacorpHi:   '#33305a',
            megacorpLo:   '#0a0612',
            // karaoke-bar (magenta-tinted)
            karaoke:      '#3a2444',
            karaokeHi:    '#5a3866',
            karaokeLo:    '#26142c',
            // ramen-stall (small, warm-tinted timber)
            ramen:        '#4a2c1f',
            ramenHi:      '#6a4533',
            ramenLo:      '#2a1610',
            // hostel (cool-blue stacked balconies)
            hostel:       '#243a52',
            hostelHi:     '#3a5878',
            hostelLo:     '#0e1828',
            // arcade (dark with magenta accent)
            arcade:       '#2a1838',
            arcadeHi:     '#4a2858',
            arcadeLo:     '#1a0a28',
            // noodle-cart (rusty mobile)
            noodleCart:   '#3a2a1c',
            noodleCartHi: '#5a4232',
            noodleCartLo: '#1a1208',
        },
        // South-facing wall band (the implied 3D strip beneath the roof).
        wall: {
            base:  '#1a1432',
            mid:   '#2a2444',
            hi:    '#3d3458',
            shade: '#0a0612',
        },
        // Window glow (cyan when OK).
        window: {
            lit:   '#22e8ff',
            litLo: '#2a8aa2',
            dark:  '#0e0a18',
            frame: '#0a0612',
        },
        // Status hues — RESERVED for actual check state. Never decorative.
        status: {
            ok:   '#2dff7c',
            warn: '#ffe22a',
            err:  '#ff8a1c',
            fail: '#ff2a3a',
        },
        // Decorative neon — never used to mean status.
        neon: {
            pink: '#ff2e7e',
            magenta: '#d028a8',
            cyan: '#22e8ff',
            violet: '#a83fff',
            // Decorative amber — distinct from status.warn (#ffe22a) so
            // NPC hair / vehicle paint can use a warm yellow without
            // claiming "this check is warning".
            amber: '#ffb84d',
        },
        // Shadow tones.
        shadow: '#04030c',
    };

    // =========================================================
    //                      CONSTANTS
    // =========================================================
    // v14 — pixel resolution doubled so each pixel represents ≈ 0.5 m of
    // ground (was ≈ 1 m). This unlocks visible detail in building facades,
    // curb stones, lane markings, traffic-light lamps, etc.
    const ZOOM_LEVELS = [1, 2, 3, 4, 6];        // halved to compensate for the larger world
    const DEFAULT_ZOOM_INDEX = 2;                // 3× still the default (was 4×)
    const TILE = 32;                             // v14 — was 16; 2 game-px per old game-px
    // City grid: each "lot" is N×N tiles. We pack lots into a grid with 2-tile
    // street margins between rows AND columns.
    // (legacy STREET_W removed; grid layout uses STREET_W defined alongside GRID_N)
    const PLAZA_LOT_SIZE = 8;                    // 8×8 ground tiles
    const ANIM_TICK_MS = 125;                    // 8 fps live FX

    // Per-archetype base footprint in ground tiles (Grid v8 — sized to
    // fill 60-85% of their plot so the city reads dense rather than
    // "building floating in empty real estate"). layoutCity clamps to
    // 85% of the plot if the footprint+jitter+tier-scale would overflow.
    // v11 — footprints scaled to the smaller BLOCK_SIZE (was 96, now 64).
    // Buildings now read as more compact urban units packed tighter, with
    // the additional density coming from the smaller streets too. Ratios
    // preserved between archetypes.
    const ARCHETYPE_FOOTPRINT = {
        'tenement':       { w: 4, h: 4 },        // 64×64 — fills 1×1 plot
        'office-spire':   { w: 5, h: 5 },        // 80×80 — fills 1×1 with overhang
        'megacorp-tower': { w: 7, h: 7 },        // 112×112; ×1.4 at tier-4 ≈ 156 px → 2×2 plot
        'karaoke-bar':    { w: 6, h: 5 },        // 96×80
        'ramen-stall':    { w: 4, h: 3 },        // 64×48 small business
        'hostel':         { w: 6, h: 4 },        // 96×64 — fills 1×1, fills 2×1
        'arcade':         { w: 5, h: 6 },        // 80×96 — fills 1×1, fills 1×2
        'noodle-cart':    { w: 3, h: 2 },        // 48×32 tiny cart in a yard
    };

    // v11 — Per-building south-wall band height pushed up so buildings
    // clearly read as TALL urban structures in 3/4 perspective rather
    // than flat roof slabs. Tier-4 megacorps now stand 30+ px tall, so
    // the visible side face dominates over the roof footprint — matching
    // tile-B-01.png reference where the south face is the dominant
    // building feature.
    function wallHeightFor(tier, seed) {
        // v14 — doubled to match TILE 16→32 scale bump so the south face
        // stays a meaningful fraction of building height.
        const base = (tier >= 4) ? 60 : (tier >= 3) ? 48 : (tier >= 2) ? 36 : 24;
        const jit = ((seed >>> 7) & 3) - 1;
        return Math.max(20, base + jit * 4);
    }

    // Per-instance footprint jitter + tier scale. Driven by the seed so the
    // result is stable across reloads. Adds the second noise layer (after
    // archetype-level differences) the user asked for.
    function buildingFootprint(archetype, tier, seed) {
        const base = ARCHETYPE_FOOTPRINT[archetype];
        const jw = ((seed >>> 22) & 3) === 0 ? -1 : ((seed >>> 22) & 3) === 1 ? 1 : 0;
        const jh = ((seed >>> 24) & 3) === 0 ? -1 : ((seed >>> 24) & 3) === 1 ? 1 : 0;
        let w = base.w + jw;
        let h = base.h + jh;
        if (tier >= 4) {
            w = Math.floor(w * 1.4);
            h = Math.floor(h * 1.4);
        } else if (tier >= 2) {
            w = Math.floor(w * 1.15);
            h = Math.floor(h * 1.15);
        }
        return { w: Math.max(3, w), h: Math.max(3, h) };
    }

    // Cap a building's footprint so it fits within its plot leaving ~15%
    // sidewalk margin. Buildings now read dense (filling most of their
    // plot) instead of floating in empty real estate.
    function clampToPlot(c) {
        const span = plotSpan(c.plotCols, c.plotRows);
        const wallH = c.wallH || 10;
        const maxW = Math.max(16, Math.floor(span.w * 0.85));
        const maxH = Math.max(16, Math.floor(span.h * 0.85) - wallH);
        // Absolute clamp — using Math.max(c.w >> 1, maxW) in the v9a draft
        // preserved the *larger* of the two when a building fell back to
        // a 1×1 plot and still overflowed, which let oversized buildings
        // spill into adjacent streets/plots (Hermes review #180).
        if (c.w > maxW) c.w = maxW;
        if (c.h > maxH) c.h = maxH;
    }
    const ARCHETYPE_NAMES = [
        'tenement', 'office-spire', 'megacorp-tower', 'karaoke-bar',
        'ramen-stall', 'hostel', 'arcade', 'noodle-cart',
    ];

    // Eight equal buckets (32 byte values per archetype) so seeds spread
    // evenly across all eight. The existing four still occupy their original
    // buckets, but now share the byte range with the four new archetypes.
    function pickArchetype(seed) {
        const r = (seed >>> 8) & 0xFF;
        if (r < 32)   return 'tenement';
        if (r < 64)   return 'office-spire';
        if (r < 96)   return 'megacorp-tower';
        if (r < 128)  return 'karaoke-bar';
        if (r < 160)  return 'ramen-stall';
        if (r < 192)  return 'hostel';
        if (r < 224)  return 'arcade';
        return 'noodle-cart';
    }

    // =========================================================
    //   v10 — TYPOLOGY DISPATCH (issue #50)
    //   Reference-faithful building silhouettes. Each archetype
    //   maps to one of a small set of typology classes; the
    //   typology drives the silhouette + window pattern + roof
    //   feature so two same-archetype buildings can read as
    //   genuinely different shapes (not just colour-jittered).
    //
    //   Typologies (added incrementally):
    //     'flat'           — legacy rectangle (default fallback)
    //     'stepped-tower'  — 3 concentric slabs w/ neon piping (v10a)
    //     'long-block'     — horizontal slot-window strip (v10b)
    //     'megablock-strip'— skinny tower w/ vertical sign (v10c)
    //     'storefront'     — small flat w/ extruded awning sign (v10d)
    //     'installation-roof' — big rooftop feature (v10e)
    // =========================================================
    function typologyFor(archetype, tier, seed, w, h) {
        // Tier-4 megacorps: stepped tower if the footprint can fit it.
        if (archetype === 'megacorp-tower' && w >= 28 && h >= 28) {
            return 'stepped-tower';
        }
        // Tier-3 office-spires: stepped tower on the larger ones.
        if (archetype === 'office-spire' && tier >= 3 && w >= 24 && h >= 24
            && ((seed >>> 26) & 1) === 0) {
            return 'stepped-tower';
        }
        // Tenements + hostels — wide rectangular footprints read as long
        // apartment blocks in the reference (cyan slot-window row along the
        // long axis, sparse HVAC, no edge piping). v10b.
        if ((archetype === 'tenement' || archetype === 'hostel')
            && w >= 24 && h >= 18) {
            return 'long-block';
        }
        // Small shops — get a neon edge-piping trim cap that reads as
        // "shopfront seen from above" without crowding the archetype's
        // own south-edge decoration (RAMEN tag, ARCADE marquee, lanterns,
        // noodle-cart wheels). v10c.
        if (archetype === 'ramen-stall' || archetype === 'arcade'
            || archetype === 'karaoke-bar' || archetype === 'noodle-cart') {
            return 'storefront';
        }
        // Office-spires that didn't take the stepped-tower route and
        // smaller megacorps get a BIG rooftop installation as their
        // identifying feature (sat dish / hologram pad / cooling tower
        // / helipad). The large megacorps + 50% of large office-spires
        // already on stepped-tower keep their slabs. v10d.
        if ((archetype === 'office-spire' || archetype === 'megacorp-tower')
            && w >= 18 && h >= 18) {
            return 'installation-roof';
        }
        return 'flat';
    }

    // =========================================================
    //                  BITMAP FONT (5×7)
    //  Issue #23 — pixel-art labels rendered glyph-by-glyph
    //  via fillRect. No fillText anywhere in the city —
    //  fillText anti-aliases under upscale and looks blurry.
    //  '#' = lit pixel, '.' = transparent.
    // =========================================================
    const FONT5x7 = {
        'A': ['.###.','#...#','#...#','#####','#...#','#...#','#...#'],
        'B': ['####.','#...#','#...#','####.','#...#','#...#','####.'],
        'C': ['.####','#....','#....','#....','#....','#....','.####'],
        'D': ['####.','#...#','#...#','#...#','#...#','#...#','####.'],
        'E': ['#####','#....','#....','####.','#....','#....','#####'],
        'F': ['#####','#....','#....','####.','#....','#....','#....'],
        'G': ['.####','#....','#....','#.###','#...#','#...#','.####'],
        'H': ['#...#','#...#','#...#','#####','#...#','#...#','#...#'],
        'I': ['#####','..#..','..#..','..#..','..#..','..#..','#####'],
        'J': ['..###','....#','....#','....#','....#','#...#','.###.'],
        'K': ['#...#','#..#.','#.#..','##...','#.#..','#..#.','#...#'],
        'L': ['#....','#....','#....','#....','#....','#....','#####'],
        'M': ['#...#','##.##','#.#.#','#.#.#','#...#','#...#','#...#'],
        'N': ['#...#','##..#','#.#.#','#.#.#','#.#.#','#..##','#...#'],
        'O': ['.###.','#...#','#...#','#...#','#...#','#...#','.###.'],
        'P': ['####.','#...#','#...#','####.','#....','#....','#....'],
        'Q': ['.###.','#...#','#...#','#...#','#.#.#','#..#.','.##.#'],
        'R': ['####.','#...#','#...#','####.','#.#..','#..#.','#...#'],
        'S': ['.####','#....','#....','.###.','....#','....#','####.'],
        'T': ['#####','..#..','..#..','..#..','..#..','..#..','..#..'],
        'U': ['#...#','#...#','#...#','#...#','#...#','#...#','.###.'],
        'V': ['#...#','#...#','#...#','#...#','#...#','.#.#.','..#..'],
        'W': ['#...#','#...#','#...#','#.#.#','#.#.#','##.##','#...#'],
        'X': ['#...#','#...#','.#.#.','..#..','.#.#.','#...#','#...#'],
        'Y': ['#...#','#...#','.#.#.','..#..','..#..','..#..','..#..'],
        'Z': ['#####','....#','...#.','..#..','.#...','#....','#####'],
        '0': ['.###.','#..##','#.#.#','#.#.#','#.#.#','##..#','.###.'],
        '1': ['..#..','.##..','..#..','..#..','..#..','..#..','.###.'],
        '2': ['.###.','#...#','....#','...#.','..#..','.#...','#####'],
        '3': ['.###.','#...#','....#','..##.','....#','#...#','.###.'],
        '4': ['...#.','..##.','.#.#.','#..#.','#####','...#.','...#.'],
        '5': ['#####','#....','####.','....#','....#','#...#','.###.'],
        '6': ['.###.','#....','#....','####.','#...#','#...#','.###.'],
        '7': ['#####','....#','...#.','..#..','.#...','#....','#....'],
        '8': ['.###.','#...#','#...#','.###.','#...#','#...#','.###.'],
        '9': ['.###.','#...#','#...#','.####','....#','....#','.###.'],
        ' ': ['.....','.....','.....','.....','.....','.....','.....'],
        '-': ['.....','.....','.....','#####','.....','.....','.....'],
        '.': ['.....','.....','.....','.....','.....','.....','..#..'],
        '?': ['.###.','#...#','....#','...#.','..#..','.....','..#..'],
        '!': ['..#..','..#..','..#..','..#..','..#..','.....','..#..'],
        ',': ['.....','.....','.....','.....','.....','..#..','.#...'],
        '/': ['....#','....#','...#.','..#..','.#...','#....','#....'],
        ':': ['.....','..#..','.....','.....','.....','..#..','.....'],
    };
    const FONT5x7_W = 5;
    const FONT5x7_H = 7;
    const FONT5x7_ADVANCE = 6;          // glyph width + 1-px inter-glyph spacing

    // Draw a string in the bitmap font at integer pixel (x, y).
    // 1-px gap between glyphs. Unknown chars fall back to '?'.
    // Returns total drawn width (no trailing spacing).
    function drawText(ctx, text, x, y, color) {
        ctx.fillStyle = color;
        const upper = text.toUpperCase();
        let cx = x;
        for (let i = 0; i < upper.length; i++) {
            const g = FONT5x7[upper[i]] || FONT5x7['?'];
            for (let row = 0; row < FONT5x7_H; row++) {
                const r = g[row];
                for (let col = 0; col < FONT5x7_W; col++) {
                    if (r.charCodeAt(col) === 35) {     // '#'
                        ctx.fillRect(cx + col, y + row, 1, 1);
                    }
                }
            }
            cx += FONT5x7_ADVANCE;
        }
        return Math.max(0, cx - x - 1);
    }

    function textWidth(text) {
        return Math.max(0, text.length * FONT5x7_ADVANCE - 1);
    }

    // Greedy soft-wrap on spaces. Single oversized word → hard-split.
    // Caps at maxLines lines; remainder is dropped.
    function wrapBitmapText(text, maxCharsPerLine, maxLines) {
        if (maxCharsPerLine <= 0 || maxLines <= 0) return [];
        if (text.length <= maxCharsPerLine) return [text];
        const words = text.split(' ');
        const lines = [];
        let cur = '';
        for (let i = 0; i < words.length; i++) {
            const w = words[i];
            const candidate = cur ? (cur + ' ' + w) : w;
            if (candidate.length <= maxCharsPerLine) {
                cur = candidate;
                continue;
            }
            if (!cur) {
                lines.push(w.slice(0, maxCharsPerLine));
                if (lines.length >= maxLines) { cur = ''; break; }
                cur = w.slice(maxCharsPerLine, maxCharsPerLine * 2);
            } else {
                lines.push(cur);
                if (lines.length >= maxLines) { cur = ''; break; }
                cur = (w.length <= maxCharsPerLine) ? w : w.slice(0, maxCharsPerLine);
            }
        }
        if (cur && lines.length < maxLines) lines.push(cur);
        return lines.slice(0, maxLines);
    }

    // Normalise a title for the bitmap font:
    //  - upper-case
    //  - strip chars we have no glyph for
    //  - collapse runs of whitespace
    function normaliseTitle(raw) {
        if (!raw) return '?';
        const cleaned = raw.toUpperCase()
            .replace(/[^A-Z0-9 \-.,!?:/]/g, '')
            .replace(/\s+/g, ' ')
            .trim();
        return cleaned || '?';
    }

    // =========================================================
    //                       STATE
    // =========================================================
    const state = {
        canvas: null,
        ctx: null,
        dotnetRef: null,

        buildings: [],
        cityWidth: 0,
        cityHeight: 0,
        ground: null,                 // pre-rendered ground+street canvas
        npcs: [],                     // overlay sprites walking each tick (issue #24)
        lampposts: [],                // lamp positions — for ground draw + lens flare (issue #25)
        particles: null,              // { steamVents, motes } — issue #25

        zoomIndex: DEFAULT_ZOOM_INDEX,
        camX: 0,
        camY: 0,

        isDragging: false,
        dragStartClientX: 0,
        dragStartClientY: 0,
        dragStartCamX: 0,
        dragStartCamY: 0,
        dragMoved: false,
        // Touch / pinch state
        pinchStartDist: 0,
        pinchStartZoomIndex: 0,
        tapStartClientX: 0,
        tapStartClientY: 0,
        tapStartTime: 0,

        rafId: null,
        running: false,
        resizeObserver: null,
        handlers: null,

        animTimer: null,
        animFrame: 0,

        diag: {
            framesDrawn: 0,
            lastFrameMs: 0,
            visibleBuildings: 0,
            buildingsRendered: 0,
            lastBuildingRenderMs: 0,
            animTicks: 0,
        },
    };

    // =========================================================
    //                       UTILITIES
    // =========================================================
    function mulberry32(seedInt) {
        let s = (seedInt | 0) >>> 0;
        return function () {
            s = (s + 0x6D2B79F5) | 0;
            let t = Math.imul(s ^ (s >>> 15), 1 | s);
            t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
            return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
        };
    }

    function createCanvas(w, h) {
        if (typeof OffscreenCanvas !== 'undefined') return new OffscreenCanvas(w, h);
        const c = document.createElement('canvas');
        c.width = w; c.height = h;
        return c;
    }

    function scale() { return ZOOM_LEVELS[state.zoomIndex]; }

    const _reducedMotionMql = (typeof window !== 'undefined' && window.matchMedia)
        ? window.matchMedia('(prefers-reduced-motion: reduce)')
        : null;
    function isReducedMotion() {
        return !!(_reducedMotionMql && _reducedMotionMql.matches);
    }

    function statusGlow(failType) {
        switch (failType) {
            case 2: return PALETTE.status.ok;
            case 1: return PALETTE.status.warn;
            case 0: return PALETTE.status.err;
            case 3: return PALETTE.status.fail;
            default: return PALETTE.status.ok;
        }
    }

    function computeTier(ageDays, uptime30d) {
        if (ageDays < 1 || uptime30d < 0.70) return 0;
        if (ageDays < 7 || uptime30d < 0.85) return 1;
        if (ageDays < 30 || uptime30d < 0.95) return 2;
        if (ageDays < 90 || uptime30d < 0.99) return 3;
        if (uptime30d >= 0.995) return 4;
        return 3;
    }

    function computeHealth(uptime7d) {
        if (uptime7d >= 0.99) return 4;
        if (uptime7d >= 0.95) return 3;
        if (uptime7d >= 0.90) return 2;
        if (uptime7d >= 0.75) return 1;
        return 0;
    }

    function signBrightnessForAnim(failType, animFrame) {
        switch (failType) {
            case 1: return ((animFrame % 8) < 6) ? 1.0 : 0.40;     // slow flicker
            case 3: return ((animFrame % 3) < 2) ? 1.0 : 0.10;     // strobe
            default: return 1.0;
        }
    }

    function dimColor(hex, brightness) {
        const h = hex.replace('#', '');
        const r = parseInt(h.substring(0, 2), 16);
        const g = parseInt(h.substring(2, 4), 16);
        const bl = parseInt(h.substring(4, 6), 16);
        const m = Math.max(0, Math.min(1, brightness));
        return 'rgb('
            + Math.round(r * m) + ','
            + Math.round(g * m) + ','
            + Math.round(bl * m) + ')';
    }

    // Issue #37 — per-instance roof tint jitter. Each channel shifts in
    // a signed range so two buildings of the same archetype never share
    // an exact palette. rng() is consumed three times.
    function jitterColor(hex, rng, amplitude) {
        const amp = amplitude == null ? 14 : amplitude;
        const h = hex.replace('#', '');
        const r = parseInt(h.substring(0, 2), 16);
        const g = parseInt(h.substring(2, 4), 16);
        const bl = parseInt(h.substring(4, 6), 16);
        const dr = Math.floor((rng() - 0.5) * 2 * amp);
        const dg = Math.floor((rng() - 0.5) * 2 * amp);
        const db = Math.floor((rng() - 0.5) * 2 * amp);
        const cr = Math.max(0, Math.min(255, r + dr));
        const cg = Math.max(0, Math.min(255, g + dg));
        const cb = Math.max(0, Math.min(255, bl + db));
        return '#'
            + cr.toString(16).padStart(2, '0')
            + cg.toString(16).padStart(2, '0')
            + cb.toString(16).padStart(2, '0');
    }

    // =========================================================
    //              CITY LAYOUT — plaza-centric radial (#21)
    //  Buildings live in 3 concentric rings around a central
    //  plaza. Tier 4 sits in the inner ring, tier 2-3 in the
    //  middle, tier 0-1 in the outer. Each ring distributes its
    //  buildings angularly with ±25 % jitter so adjacent slots
    //  don't line up like a wheel — produces the organic
    //  "tangled cityscape" feel from the issue #11 reference.
    //
    //  Streets are NOT pre-laid. The renderGround pass paints a
    //  magenta neon edge around each building's footprint, which
    //  produces the reference's "neon-outlined sidewalk" look
    //  without needing an explicit street grid.
    // =========================================================

    // Grid-of-blocks layout (v5 rewrite per user feedback on Grid v4).
    //   Streets are perpendicular, buildings snap to the centre of a block,
    //   plaza takes the centre block of the grid. The ring *feel* is
    //   preserved by assigning tier-4 landmarks to the centre rings of the
    //   block grid and tier-0/1 to the outer rings, with a small
    //   per-tier shuffle so equal-tier blocks don't form a strict pattern.
    // Grid v9 — much smaller base cell + variable rectangular plots so
    // the city reads dense and irregular. A "plot" spans N×M cells (1×1
    // up to 3×3) instead of every building owning an identical 192-px
    // square. Addresses "perfect square grid / wasted space" feedback.
    const BASE_GRID_N = 12;          // v11 — denser grid (was 10)
    const BLOCK_SIZE  = 128;         // v14 — doubled from 64 alongside TILE bump
    const STREET_W    = 48;          // v16 — doubled from 24; user feedback: streets
                                     //       were only 8 px of asphalt, no detail headroom
    const CITY_PAD    = 32;          // v16 — bumped for the wider streets
    const RING_RADII  = [256, 448, 672];        // legacy

    // v9c — per-segment street kind. Each gap between two adjacent cells
    // gets one of three kinds; the geometric width stays STREET_W (so
    // building plot positions don't change), only the visual painting
    // of the road varies.
    const STREET_KIND_STANDARD = 0;
    const STREET_KIND_AVENUE   = 1;     // wider asphalt, narrower sidewalks, two lanes
    const STREET_KIND_ALLEY    = 2;     // no asphalt — pure concrete walkway

    function pickStreetKinds(N, cityRng, plazaCol, plazaRow) {
        const colKinds = new Array(N - 1).fill(STREET_KIND_STANDARD);
        const rowKinds = new Array(N - 1).fill(STREET_KIND_STANDARD);
        // Pick one N-S avenue + one E-W avenue near (but not adjacent to) the plaza.
        const avenueColCand = plazaCol >= 2 ? plazaCol - 2 : Math.min(N - 2, plazaCol + 2);
        const avenueRowCand = plazaRow >= 2 ? plazaRow - 2 : Math.min(N - 2, plazaRow + 2);
        if (avenueColCand >= 0 && avenueColCand < N - 1) colKinds[avenueColCand] = STREET_KIND_AVENUE;
        if (avenueRowCand >= 0 && avenueRowCand < N - 1) rowKinds[avenueRowCand] = STREET_KIND_AVENUE;
        // ~12 % alleys on the remaining standard segments.
        for (let i = 0; i < N - 1; i++) {
            if (colKinds[i] === STREET_KIND_STANDARD && cityRng() < 0.12) colKinds[i] = STREET_KIND_ALLEY;
            if (rowKinds[i] === STREET_KIND_STANDARD && cityRng() < 0.12) rowKinds[i] = STREET_KIND_ALLEY;
        }
        return { colKinds, rowKinds };
    }

    // Plot sizes per (tier, archetype, seed). Heavy skew toward rectangles
    // so two same-archetype buildings adjacent on the grid look different
    // not just in colour but in footprint shape.
    function plotSizeFor(c) {
        if (c.tier >= 4) {
            if (c.archetype === 'megacorp-tower' || c.archetype === 'office-spire') {
                return [3, 3];                                  // 9-cell landmark
            }
            // Other tier-4 — wide 3×2 or tall 2×3.
            return ((c.seed >>> 28) & 1) ? [3, 2] : [2, 3];
        }
        if (c.tier >= 3) {
            const r = (c.seed >>> 26) & 3;
            if (r === 0) return [3, 2];
            if (r === 1) return [2, 3];
            return [2, 2];
        }
        if (c.tier >= 2) {
            const r = (c.seed >>> 26) & 3;
            if (r === 0) return [2, 1];
            if (r === 1) return [1, 2];
            return [2, 2];
        }
        // tier-0/1 — small, mostly 1×1 with some rectangular variety.
        const r = (c.seed >>> 26) & 3;
        if (r === 0) return [1, 2];
        if (r === 1) return [2, 1];
        return [1, 1];
    }

    // Average plot is ~3.5 cells (mix of 1, 2, 4, 6, 9). Grow the grid
    // generously so the packer never silently drops a building + leaves
    // empty cells for parks / vacant lots, especially on the outer ring.
    // v12 — bumped by ~33 % to compensate for the radial cull that marks
    // up to ~40 % of cells as wasteland. Hermes review #202 caught the
    // 71/80 drop without this growth.
    function computeGridN(n) {
        const base = Math.max(BASE_GRID_N, Math.ceil(Math.sqrt(n * 4 + 24)));
        return Math.ceil(base * 1.35);
    }

    function blockOrigin(col, row) {
        return {
            x: CITY_PAD + col * (BLOCK_SIZE + STREET_W),
            y: CITY_PAD + row * (BLOCK_SIZE + STREET_W),
            w: BLOCK_SIZE,
            h: BLOCK_SIZE,
        };
    }

    // World-space size of a plot that spans (colSpan, rowSpan) cells. The
    // streets *between* those cells become part of the building's plot
    // (paved sidewalk, no asphalt) so the plot reads as a single landmark.
    function plotSpan(colSpan, rowSpan) {
        return {
            w: colSpan * BLOCK_SIZE + (colSpan - 1) * STREET_W,
            h: rowSpan * BLOCK_SIZE + (rowSpan - 1) * STREET_W,
        };
    }

    function layoutCity(buildingDtos) {
        const positioned = [];
        const n = buildingDtos.length;
        const N = computeGridN(n);

        state.cityWidth  = CITY_PAD * 2 + N * BLOCK_SIZE + (N - 1) * STREET_W;
        state.cityHeight = state.cityWidth;
        const cx = Math.floor(state.cityWidth / 2);
        const cy = Math.floor(state.cityHeight / 2);

        if (n === 0) {
            return { positioned, plaza: null, cx, cy, ringRadii: RING_RADII };
        }

        // Stable city seed across reloads.
        let citySeed = 0xC17A;
        for (const b of buildingDtos) citySeed ^= ((b.seed | 0) >>> 0);
        const cityRng = mulberry32(citySeed | 0);

        // Pass 1 — derive each candidate's footprint, tier, health + plot
        // size (cell span). Tier-4 megacorps/office-spires get 2×2 plots,
        // tier-2/3 hostel-style get 2×1 or 1×2, rest are 1×1. Footprint
        // itself is jittered per-seed + scaled with tier.
        const candidates = buildingDtos.map(dto => {
            const seed = (dto.seed | 0) || 1;
            const archetype = pickArchetype(seed);
            const tier = computeTier(dto.ageDays || 0, dto.uptime30d ?? 1.0);
            const health = computeHealth(dto.uptime7d ?? 1.0);
            const fp = buildingFootprint(archetype, tier, seed);
            const plot = plotSizeFor({ tier, archetype, seed });
            const w = fp.w * TILE;
            const h = fp.h * TILE;
            return {
                dto, seed, archetype, tier, health,
                w, h,
                wallH: wallHeightFor(tier, seed),
                typology: typologyFor(archetype, tier, seed, w, h),
                plotCols: plot[0],
                plotRows: plot[1],
            };
        });

        // Plaza occupies a 2×2 centre patch — must be visually prominent
        // since it's the city centrepiece (was 1×1 in v9a draft and looked
        // tiny next to the 3×3 tier-4 megacorp plots).
        const PLAZA_COLS = 2;
        const PLAZA_ROWS = 2;
        const plazaCol = Math.floor((N - PLAZA_COLS) / 2);
        const plazaRow = Math.floor((N - PLAZA_ROWS) / 2);
        const plazaBlock = blockOrigin(plazaCol, plazaRow);
        const plazaSpan = plotSpan(PLAZA_COLS, PLAZA_ROWS);
        const plaza = {
            x: plazaBlock.x,
            y: plazaBlock.y,
            w: plazaSpan.w,
            h: plazaSpan.h,
        };

        // v9c — per-segment street kinds (avenue / standard / alley).
        const streetKinds = pickStreetKinds(N, cityRng, plazaCol, plazaRow);

        // Plot ownership grid. `plotMap[row][col] = candidate index` once
        // claimed, or -1 if free. Plaza claims its 2×2 cells up-front.
        const PLAZA_OWNER = -2;
        const BLOCKED_OWNER = -3;             // v12 — radial cull marker
        const plotMap = [];
        for (let r = 0; r < N; r++) {
            plotMap.push(new Array(N).fill(-1));
        }
        for (let dr = 0; dr < PLAZA_ROWS; dr++) {
            for (let dc = 0; dc < PLAZA_COLS; dc++) {
                plotMap[plazaRow + dr][plazaCol + dc] = PLAZA_OWNER;
            }
        }

        // v12 — radial colony culling. The city reads as a circular cluster
        // growing outward from the plaza rather than a rigid square grid.
        // - Inner ring (≤ innerR cells from plaza): all cells available.
        // - Middle ring (innerR..midR): some cells stochastically blocked
        //   so the urban density falls off naturally.
        // - Outer ring (> midR): all cells blocked → city ends in a soft
        //   circular boundary surrounded by empty ground.
        // The plaza-centre is the unique origin; the colony grows out.
        const plazaCentreCol = plazaCol + (PLAZA_COLS - 1) / 2;
        const plazaCentreRow = plazaRow + (PLAZA_ROWS - 1) / 2;
        const N_HALF = (N - 1) / 2;
        const outerR = N_HALF + 0.4;            // hard outer radius (rounded)
        const midR   = N_HALF * 0.75;           // middle-ring start
        const innerR = N_HALF * 0.40;           // dense-core ring end
        for (let row = 0; row < N; row++) {
            for (let col = 0; col < N; col++) {
                if (plotMap[row][col] !== -1) continue;     // plaza already
                const dx = col - plazaCentreCol;
                const dy = row - plazaCentreRow;
                const r = Math.sqrt(dx * dx + dy * dy);
                if (r > outerR) {
                    plotMap[row][col] = BLOCKED_OWNER;
                    continue;
                }
                if (r > innerR) {
                    // Stochastic block — probability ramps from 0 at innerR
                    // to ~0.55 at outerR. Stable per-cell hash so the same
                    // city renders the same way every refresh.
                    const t = (r - innerR) / (outerR - innerR);
                    const hash = ((col * 73856093) ^ (row * 19349663) ^ citySeed) >>> 0;
                    const p = (hash & 0xFFFF) / 0x10000;
                    if (p < t * 0.55) {
                        plotMap[row][col] = BLOCKED_OWNER;
                    }
                }
            }
        }

        // Sort candidates by plot area (largest first), then tier desc, then
        // seed asc. Largest plots get the choicest slots — nearest the plaza.
        candidates.sort((a, b) => {
            const sa = a.plotCols * a.plotRows;
            const sb = b.plotCols * b.plotRows;
            if (sa !== sb) return sb - sa;
            if (a.tier !== b.tier) return b.tier - a.tier;
            return (a.seed | 0) - (b.seed | 0);
        });

        function findPlot(plotCols, plotRows) {
            const slots = [];
            for (let row = 0; row <= N - plotRows; row++) {
                for (let col = 0; col <= N - plotCols; col++) {
                    let free = true;
                    for (let dr = 0; dr < plotRows && free; dr++) {
                        for (let dc = 0; dc < plotCols && free; dc++) {
                            if (plotMap[row + dr][col + dc] !== -1) free = false;
                        }
                    }
                    if (!free) continue;
                    const colCentre = col + (plotCols - 1) / 2;
                    const rowCentre = row + (plotRows - 1) / 2;
                    const d = Math.max(
                        Math.abs(colCentre - plazaCol),
                        Math.abs(rowCentre - plazaRow));
                    slots.push({ col, row, d });
                }
            }
            if (!slots.length) return null;
            slots.sort((a, b) => (a.d - b.d) || (cityRng() - 0.5));
            return slots[0];
        }

        for (let i = 0; i < candidates.length; i++) {
            const c = candidates[i];
            let slot = findPlot(c.plotCols, c.plotRows);
            // Fall back to 1×1 if the requested plot doesn't fit anywhere.
            if (!slot && (c.plotCols > 1 || c.plotRows > 1)) {
                c.plotCols = 1; c.plotRows = 1;
                slot = findPlot(1, 1);
            }
            // v12 — if still no slot, unblock the nearest wasteland cell.
            // Every input check MUST render — the radial cull is a visual
            // preference, not a hard cap. Hermes review #202.
            if (!slot) {
                let bestRow = -1, bestCol = -1, bestD2 = Infinity;
                for (let row = 0; row < N; row++) {
                    for (let col = 0; col < N; col++) {
                        if (plotMap[row][col] !== BLOCKED_OWNER) continue;
                        const dx = col - plazaCentreCol;
                        const dy = row - plazaCentreRow;
                        const d2 = dx * dx + dy * dy;
                        if (d2 < bestD2) { bestD2 = d2; bestRow = row; bestCol = col; }
                    }
                }
                if (bestRow >= 0) {
                    plotMap[bestRow][bestCol] = -1;
                    c.plotCols = 1; c.plotRows = 1;
                    slot = findPlot(1, 1);
                }
            }
            if (!slot) continue;     // grid genuinely full — shouldn't happen with computeGridN

            for (let dr = 0; dr < c.plotRows; dr++) {
                for (let dc = 0; dc < c.plotCols; dc++) {
                    plotMap[slot.row + dr][slot.col + dc] = i;
                }
            }

            const bo = blockOrigin(slot.col, slot.row);
            const span = plotSpan(c.plotCols, c.plotRows);
            // Cap building dimensions to fit within the plot (Grid v8 —
            // makes denser cities, addresses "huge waste of real estate").
            clampToPlot(c);
            // Centre the visual envelope (roof + wall band) so the south
            // wall doesn't spill into the street south of the plot.
            const bx = bo.x + Math.floor((span.w - c.w) / 2);
            const visualH = c.h + c.wallH;
            const by = bo.y + Math.floor((span.h - visualH) / 2);
            positioned.push({
                id: c.dto.id,
                title: c.dto.title || '',
                seed: c.seed,
                ageDays: c.dto.ageDays || 0,
                uptime30d: c.dto.uptime30d ?? 1.0,
                uptime7d: c.dto.uptime7d ?? 1.0,
                currentFailType: c.dto.currentFailType ?? 2,
                consecutiveFailures: c.dto.consecutiveFailures || 0,
                lastCheckIso: c.dto.lastCheckUtc || null,
                tier: c.tier, health: c.health,
                archetype: c.archetype,
                typology: c.typology,
                w: c.w, h: c.h,
                wallH: c.wallH,
                x: bx, y: by,
                blockCol: slot.col, blockRow: slot.row,
                plotCols: c.plotCols, plotRows: c.plotRows,
                ringIdx: Math.min(2, Math.max(Math.abs(slot.col - plazaCol),
                                              Math.abs(slot.row - plazaRow))),
                canvas: null,
            });
        }

        return {
            positioned, plaza, cx, cy,
            ringRadii: RING_RADII,
            grid: {
                n: N, blockSize: BLOCK_SIZE, streetW: STREET_W, pad: CITY_PAD,
                plazaCol, plazaRow,
                plazaCols: PLAZA_COLS, plazaRows: PLAZA_ROWS,
                plotMap, plazaOwner: PLAZA_OWNER, blockedOwner: BLOCKED_OWNER,
                colKinds: streetKinds.colKinds,
                rowKinds: streetKinds.rowKinds,
            },
        };
    }

    // Simple AABB overlap predicate used by layoutCity to nudge new
    // placements away from already-placed neighbours. Margin lets us
    // demand a small "sidewalk gap" between adjacent buildings.
    function overlapsAny(list, x, y, w, h, margin) {
        for (const b of list) {
            if (x + w + margin > b.x && b.x + b.w + margin > x
                && y + h + margin > b.y && b.y + b.h + margin > y) {
                return true;
            }
        }
        return false;
    }

    // =========================================================
    //                  GROUND + STREET RENDERER
    //  One canvas per city covering the entire layout. Streets
    //  thread between lots; sidewalks frame each lot. Neon
    //  road-edge outlines are the signature element of the
    //  reference's top-down look.
    // =========================================================
    function renderGround(layout) {
        const w = state.cityWidth;
        const h = state.cityHeight;
        const c = createCanvas(w, h);
        const ctx = c.getContext('2d');
        ctx.imageSmoothingEnabled = false;

        // 1. Base ground fill.
        ctx.fillStyle = PALETTE.ground.base;
        ctx.fillRect(0, 0, w, h);

        // 2. Subtle floor speckle.
        const grainRng = mulberry32(0xC17A);
        ctx.fillStyle = PALETTE.ground.mid;
        const grainCount = Math.floor((w * h) / 700);
        for (let i = 0; i < grainCount; i++) {
            ctx.fillRect(Math.floor(grainRng() * w), Math.floor(grainRng() * h), 1, 1);
        }

        // 3. Block sidewalk fill — every grid block (occupied or empty) is
        //    paved in sidewalk colour so the buildings sit on a coherent
        //    block surface rather than the void background. Empty blocks
        //    pick up a small decorative motif (planter / parking dashes /
        //    park dirt) chosen from their (col, row) seed for variation.
        if (layout.grid) {
            renderBlockSurfaces(ctx, layout);
        }

        // 4. Grid streets — perpendicular horizontal + vertical avenues
        //    between every block, with crosswalks at every intersection
        //    and at the four plaza entries.
        if (layout.grid) {
            renderRadialPaths(ctx, layout);
        }

        // 5. Plaza tile at city centre (now sits on one grid block).
        if (layout.plaza) {
            renderPlazaInto(ctx, layout.plaza);
        }

        // 6. Lampposts + vehicles distributed organically in the void areas.
        //    NPCs were moved OUT of the cached canvas (issue #24) so they
        //    can walk — see generateNpcs / renderNpcs.
        renderLamppostsOrganic(ctx, state.lampposts);
        renderVehiclesOrganic(ctx, layout);

        return c;
    }

    // Issue #37 (v5) — organic ring layout with grid-style street RENDERING.
    //
    //   The ring layout from v4 stays (4 wobbling main roads + 2 tangential
    //   cross-arcs + roundabouts at termini), but the road *graphics* are
    //   rebuilt from a 21-px cross-section profile so the asphalt no longer
    //   reads as "lazy: just a strip plus a magenta line".
    //
    //   Cross-section (per perpendicular pixel offset p from centerline):
    //       p = ±10        soft magenta neon trim (1 px each side)
    //       p = ±9 .. ±8   sidewalk + occasional sidewalkHi tile-grout speckle
    //       p = ±7         dark curb (1 px each side)
    //       p = ±6 .. ±1   asphalt + 1-px asphaltLo grain
    //       p =   0        dashed white centerline (every 8 px, 4 on / 4 off)
    //
    //   Same profile is reused for tangential arcs and the perimeter of each
    //   roundabout. Crosswalks paint white stripes at each plaza entry.
    // v16 — road cross-section profile sized to STREET_W = 48:
    //   sidewalk 10 / curb 2 / asphalt 11 / median 2 / asphalt 11 / curb 2 / sidewalk 10 = 48
    // Doubled from v15's 24 px STREET_W. The user noted v15 only had 8 px
    // of asphalt — no room for proper lane detail, traffic lights, or
    // sidewalk furniture. With 22 px of asphalt we get visible two-lane
    // detail (centre median + dashed lane separators in each direction).
    const ROAD_HALF_W      = 24;
    const ROAD_SIDEWALK_W  = 10;
    const ROAD_CURB_OFFSET = ROAD_HALF_W - ROAD_SIDEWALK_W;        // 14
    const ROAD_ASPHALT_MAX = ROAD_CURB_OFFSET - 2;                 // 12
    const ROAD_DASH_PERIOD = 16;       // longer dashes at the new scale
    const ROAD_DASH_ON     = 8;

    // Paint sidewalk under every cell + over the inter-cell street gaps
    // belonging to a multi-cell plot. Multiple noise layers (per-block grout
    // stride, speckle density, and asphalt patch flecks) keep the floor
    // from reading as a precision grid.
    function renderBlockSurfaces(ctx, layout) {
        const grid = layout.grid;
        if (!grid) return;
        const plotMap = grid.plotMap;

        // Layer 1 — every cell paved with sidewalk + per-cell noise.
        // v12 — skip cells the radial cull marked as wasteland so the
        // colony reads as a circular cluster surrounded by void ground.
        const BLOCKED_OWNER = grid.blockedOwner ?? -3;
        for (let row = 0; row < grid.n; row++) {
            for (let col = 0; col < grid.n; col++) {
                if (plotMap[row][col] === BLOCKED_OWNER) continue;
                paintCellSurface(ctx, col, row);
            }
        }

        // Layer 2 — for every multi-cell plot, paint over the inter-cell
        // street gap so the plot reads as one continuous landmark.
        for (const b of layout.positioned) {
            if (b.plotCols <= 1 && b.plotRows <= 1) continue;
            const bo = blockOrigin(b.blockCol, b.blockRow);
            const span = plotSpan(b.plotCols, b.plotRows);
            for (let dc = 0; dc < b.plotCols - 1; dc++) {
                const sx = bo.x + (dc + 1) * BLOCK_SIZE + dc * STREET_W;
                paintPatchSurface(ctx, sx, bo.y, STREET_W, span.h,
                                  ((b.blockCol + dc) * 73856093) ^ (b.blockRow * 19349663));
            }
            for (let dr = 0; dr < b.plotRows - 1; dr++) {
                const sy = bo.y + (dr + 1) * BLOCK_SIZE + dr * STREET_W;
                paintPatchSurface(ctx, bo.x, sy, span.w, STREET_W,
                                  (b.blockCol * 73856093) ^ ((b.blockRow + dr) * 19349663));
            }
            // The intersection between vertical + horizontal inter-cell
            // streets of a 2×2 plot gets a redundant fill but at the same
            // texture seed so the visual is consistent.
            if (b.plotCols > 1 && b.plotRows > 1) {
                const sx = bo.x + BLOCK_SIZE;
                const sy = bo.y + BLOCK_SIZE;
                paintPatchSurface(ctx, sx, sy, STREET_W, STREET_W,
                                  (b.blockCol * 0xA5A5) ^ (b.blockRow * 0x5A5A));
            }
        }

        // Layer 3 — empty-block decorations (only on cells that aren't
        // occupied by a plot or the plaza). Concrete ground stays plain on
        // ~60% of cells; the rest get a planter cluster or fountain.
        for (let row = 0; row < grid.n; row++) {
            for (let col = 0; col < grid.n; col++) {
                if (plotMap[row][col] !== -1) continue;     // occupied (incl. plaza)
                const cellRng = mulberry32(((col * 73856093) ^ (row * 19349663)) >>> 0);
                if (cellRng() > 0.40) continue;             // 60% stays plain concrete
                renderEmptyBlockMotif(ctx, blockOrigin(col, row), cellRng);
            }
        }

        // Layer 4 — street furniture scattered along the sidewalks around
        // each occupied building (Grid v7 — addresses the "boring grids
        // without any texture" feedback). Bench / trash can / planter box
        // / vending machine, picked per-building from the seed.
        for (const b of layout.positioned) {
            renderPlotFurniture(ctx, b);
        }
    }

    function renderPlotFurniture(ctx, b) {
        // Sidewalk space inside the plot but outside the building's south-wall
        // envelope. We drop 2-4 items along the south edge of the plot and
        // 1-2 items along the north / sides, all on the building's seed.
        const rng = mulberry32(((b.seed ^ 0xF1A7) | 0) >>> 0);
        const south = b.y + b.h + b.wallH + 3;
        const span = plotSpan(b.plotCols, b.plotRows);
        const plotX = b.x - Math.floor((span.w - b.w) / 2);
        const plotY = b.y - Math.floor((span.h - (b.h + b.wallH)) / 2);
        const plotW = span.w;
        const plotH = span.h;

        // Helpers: paint different street-furniture sprites at a base (x, y).
        function bench(x, y) {
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(x, y, 8, 2);
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(x, y, 8, 1);
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(x + 1, y + 2, 6, 1);
            // Legs
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(x + 1, y + 3, 1, 1);
            ctx.fillRect(x + 6, y + 3, 1, 1);
        }
        function trashCan(x, y) {
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(x, y, 3, 5);
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(x, y, 3, 1);
            ctx.fillStyle = PALETTE.neon.cyan;
            ctx.fillRect(x + 1, y + 1, 1, 1);     // recycle indicator
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(x, y + 5, 3, 1);
        }
        function planterBox(x, y) {
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(x, y, 6, 3);
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(x, y, 6, 1);
            ctx.fillStyle = PALETTE.neon.cyan;
            ctx.fillRect(x + 1, y + 1, 1, 1);
            ctx.fillRect(x + 3, y + 1, 1, 1);
            ctx.fillRect(x + 5, y + 1, 1, 1);
        }
        function vending(x, y) {
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(x, y, 4, 6);
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(x, y, 4, 1);
            // Lit panel face
            ctx.fillStyle = PALETTE.neon.pink;
            ctx.fillRect(x + 1, y + 1, 2, 2);
            ctx.fillStyle = PALETTE.neon.cyan;
            ctx.fillRect(x + 1, y + 4, 2, 1);
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(x, y + 6, 4, 1);
        }
        function fireHydrant(x, y) {
            ctx.fillStyle = PALETTE.neon.amber;
            ctx.fillRect(x, y, 2, 3);
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(x, y + 3, 2, 1);
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(x - 1, y + 1, 1, 1);
            ctx.fillRect(x + 2, y + 1, 1, 1);
        }
        // v9e items.
        function mailbox(x, y) {
            ctx.fillStyle = PALETTE.neon.cyan;
            ctx.fillRect(x, y, 3, 2);                  // top cap (lit)
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(x, y + 2, 3, 2);              // body
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(x + 1, y + 2, 1, 1);          // slot highlight
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(x, y + 4, 3, 1);              // shadow
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(x + 1, y + 4, 1, 2);          // pole
        }
        function trafficLight(x, y) {
            // v15 — proper 3-stack lamp box on a pole, sized for the doubled
            // resolution. Decorative palette (pink/amber/cyan); these are
            // ambient signal lights, not a live status indicator.
            // Pole — 2 px wide, runs from base to housing.
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(x + 2, y + 11, 2, 7);
            // Pole base — 4 px wide foot at the ground.
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(x + 1, y + 17, 4, 1);
            // Housing — 6×11 dark box with 1-px shadow on east + south.
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(x, y, 6, 11);
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(x + 5, y, 1, 11);                // east edge
            ctx.fillRect(x, y + 10, 6, 1);                // south edge
            // 1-px hi-light on the north + west edges.
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(x, y, 6, 1);
            ctx.fillRect(x, y, 1, 11);
            // Three lamps stacked vertically — pink (stop) / amber (wait) /
            // cyan (go). Each lamp is a 2×2 lit core with a 4×4 dark socket.
            const lamps = [
                { dy: 1, color: PALETTE.neon.pink },
                { dy: 4, color: PALETTE.neon.amber },
                { dy: 7, color: PALETTE.neon.cyan },
            ];
            for (const L of lamps) {
                // Dark socket.
                ctx.fillStyle = PALETTE.shadow;
                ctx.fillRect(x + 1, y + L.dy, 4, 3);
                // Lit core.
                ctx.fillStyle = L.color;
                ctx.fillRect(x + 2, y + L.dy + 1, 2, 2);
                // 1-px halo on the south side of the lamp for a soft glow.
                ctx.globalAlpha = 0.45;
                ctx.fillRect(x + 1, y + L.dy + 2, 1, 1);
                ctx.fillRect(x + 4, y + L.dy + 2, 1, 1);
                ctx.globalAlpha = 1.0;
            }
        }
        function parkedCar(x, y) {
            // 8×4 small vehicle. Body colour from decorative neon, dark
            // windscreen strip, headlight pair.
            const carColors = [PALETTE.neon.amber, PALETTE.neon.cyan, PALETTE.neon.pink, PALETTE.neon.violet];
            const bodyColor = carColors[Math.floor(rng() * carColors.length)];
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(x, y + 3, 8, 1);              // ground shadow
            ctx.fillStyle = bodyColor;
            ctx.fillRect(x, y, 8, 3);                  // body
            ctx.fillStyle = PALETTE.window.dark;
            ctx.fillRect(x + 1, y + 1, 6, 1);          // windscreen / cabin
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(x, y, 1, 1);                  // front fender
            ctx.fillRect(x + 7, y, 1, 1);              // back fender
            ctx.fillStyle = PALETTE.neon.cyan;
            ctx.fillRect(x, y + 2, 1, 1);              // headlights
            ctx.fillRect(x + 7, y + 2, 1, 1);
        }
        function busStop(x, y) {
            // 8×6 shelter — roof slab + posts + bench.
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(x, y, 8, 2);                  // roof
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(x, y, 8, 1);                  // roof eave
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(x, y + 2, 1, 4);              // left post
            ctx.fillRect(x + 7, y + 2, 1, 4);          // right post
            ctx.fillStyle = PALETTE.window.dark;
            ctx.fillRect(x + 1, y + 2, 6, 3);          // glass back panel
            ctx.fillStyle = PALETTE.neon.cyan;
            ctx.fillRect(x + 2, y + 5, 4, 1);          // bench top
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(x, y + 6, 8, 1);              // ground shadow
        }
        // Structured items so size + drawer can't drift apart (Hermes review
        // #172 flagged a furniture index mismatch — refactored to a single
        // table to make that impossible).
        const items = [
            { fn: bench,        w: 8,  h: 4 },
            { fn: trashCan,     w: 3,  h: 6 },
            { fn: planterBox,   w: 6,  h: 3 },
            { fn: vending,      w: 4,  h: 7 },
            { fn: fireHydrant,  w: 3,  h: 4 },
            { fn: mailbox,      w: 3,  h: 6 },
            { fn: busStop,      w: 8,  h: 7 },
            // v17 — trafficLight + parkedCar moved out of the random pool:
            // traffic lights now sit at intersections (one per crosswalk
            // entry), parked cars hug the road curb instead of being
            // scattered randomly across the plot perimeter.
        ];

        function pickItem() {
            const i = Math.min(items.length - 1, Math.floor(rng() * items.length));
            return items[i];
        }

        // South-edge furniture — 2 items spaced along the south sidewalk
        // outside the building's south wall.
        for (let i = 0; i < 2; i++) {
            const it = pickItem();
            if (it.w + 12 > plotW) continue;
            const slotX = plotX + 6 + Math.floor(rng() * Math.max(1, plotW - 12 - it.w));
            const yRoom = plotY + plotH - south - it.h - 3;
            if (yRoom < 0) continue;
            const slotY = south + Math.floor(rng() * Math.max(1, yRoom));
            if (slotY + it.h < plotY + plotH - 2) it.fn(slotX, slotY);
        }

        // Side-edge furniture — 1-2 along the LEFT or RIGHT margins between
        // the building and the plot edge. Skipped when the building fills
        // the entire plot width (no room for sidewalk furniture).
        const sideCount = 1 + Math.floor(rng() * 2);
        for (let i = 0; i < sideCount; i++) {
            const it = pickItem();
            const side = (rng() < 0.5) ? 'left' : 'right';
            const leftRoom  = b.x - plotX - 6 - it.w;
            const rightRoom = plotX + plotW - (b.x + b.w) - 4 - it.w;
            const useLeft = (side === 'left' && leftRoom > 0)
                         || (side === 'right' && rightRoom <= 0 && leftRoom > 0);
            const useRight = !useLeft && rightRoom > 0;
            if (!useLeft && !useRight) continue;
            const slotX = useLeft
                ? plotX + 4 + Math.floor(rng() * leftRoom)
                : b.x + b.w + 2 + Math.floor(rng() * rightRoom);
            const yRoom = plotH - 16 - it.h;
            if (yRoom < 0) continue;
            const slotY = plotY + 8 + Math.floor(rng() * Math.max(1, yRoom));
            if (slotX > plotX + 2 && slotX + it.w < plotX + plotW - 2) {
                it.fn(slotX, slotY);
            }
        }
    }

    function paintCellSurface(ctx, col, row) {
        paintPatchSurface(ctx,
            CITY_PAD + col * (BLOCK_SIZE + STREET_W),
            CITY_PAD + row * (BLOCK_SIZE + STREET_W),
            BLOCK_SIZE, BLOCK_SIZE,
            (col * 73856093) ^ (row * 19349663));
    }

    // Paint an arbitrary rectangle as concrete sidewalk with multiple
    // per-rect random noise layers (Grid v7 expanded):
    //   - tile-grout stride jitter + offset (so adjacent blocks misalign)
    //   - speckle in two tones (sidewalkHi light dabs + neon.cyan flecks)
    //   - sparse crack lines (1-px dark scratches at random angles)
    //   - occasional dark patch (oil-stain / repair)
    //   - occasional warm puddle tint (one rare warm spot per ~12 blocks)
    // All deterministic from `seedHash`.
    function paintPatchSurface(ctx, x, y, w, h, seedHash) {
        if (w <= 0 || h <= 0) return;
        const rng = mulberry32((seedHash | 0) >>> 0);

        // Base sidewalk fill.
        ctx.fillStyle = PALETTE.street.sidewalk;
        ctx.fillRect(x, y, w, h);

        // Per-block tile-grout stride (jittered) + offset.
        const strideY = 12 + Math.floor(rng() * 10);
        const strideX = 12 + Math.floor(rng() * 10);
        const offY = Math.floor(rng() * strideY);
        const offX = Math.floor(rng() * strideX);
        ctx.fillStyle = PALETTE.street.sidewalkLo;
        for (let yy = y + offY; yy < y + h - 2; yy += strideY) {
            ctx.fillRect(x + 1, yy, w - 2, 1);
        }
        for (let xx = x + offX; xx < x + w - 2; xx += strideX) {
            ctx.fillRect(xx, y + 1, 1, h - 2);
        }

        // Light speckle (subtle — Grid v8 toned this down because the
        // previous density read as busy noise rather than texture).
        const speckleCount = Math.floor((w * h) / 380) + Math.floor(rng() * 8);
        ctx.fillStyle = PALETTE.street.sidewalkHi;
        for (let i = 0; i < speckleCount; i++) {
            const sx = x + 1 + Math.floor(rng() * (w - 2));
            const sy = y + 1 + Math.floor(rng() * (h - 2));
            ctx.fillRect(sx, sy, 1, 1);
        }
        // Cyan flecks (broken-glass shimmer) dropped — they read as
        // prominent noise rather than concrete texture in v7 review.

        // Crack lines — 0-1 short 1-px scratches per block (was 0-2).
        const crackCount = (rng() < 0.30) ? 1 : 0;
        ctx.fillStyle = PALETTE.street.curb;
        for (let i = 0; i < crackCount; i++) {
            const cx = x + 4 + Math.floor(rng() * (w - 8));
            const cy = y + 4 + Math.floor(rng() * (h - 8));
            const len = 3 + Math.floor(rng() * 8);
            const horiz = rng() < 0.5;
            for (let k = 0; k < len; k++) {
                const kx = horiz ? cx + k : cx + (rng() < 0.4 ? (rng() < 0.5 ? -1 : 1) : 0);
                const ky = horiz ? cy + (rng() < 0.4 ? (rng() < 0.5 ? -1 : 1) : 0) : cy + k;
                if (kx > x && kx < x + w - 1 && ky > y && ky < y + h - 1) {
                    ctx.fillRect(kx, ky, 1, 1);
                }
            }
        }

        // Occasional dark patch (oil-stain / repair).
        if (rng() < 0.22 && w > 30 && h > 30) {
            const pw = 4 + Math.floor(rng() * 8);
            const ph = 4 + Math.floor(rng() * 8);
            const px = x + 4 + Math.floor(rng() * (w - 8 - pw));
            const py = y + 4 + Math.floor(rng() * (h - 8 - ph));
            ctx.fillStyle = PALETTE.street.sidewalkLo;
            ctx.globalAlpha = 0.65;
            ctx.fillRect(px, py, pw, ph);
            ctx.globalAlpha = 1.0;
        }

        // Rare warm puddle tint (sodium-light reflection / spilled neon).
        if (rng() < 0.08 && w > 30 && h > 30) {
            const pw = 5 + Math.floor(rng() * 5);
            const ph = 4 + Math.floor(rng() * 4);
            const px = x + 4 + Math.floor(rng() * (w - 8 - pw));
            const py = y + 4 + Math.floor(rng() * (h - 8 - ph));
            ctx.fillStyle = PALETTE.neon.amber;
            ctx.globalAlpha = 0.18;
            ctx.fillRect(px, py, pw, ph);
            ctx.globalAlpha = 1.0;
        }
    }

    function renderEmptyBlockMotif(ctx, bo, rng) {
        // Two motifs only (parking lot dropped per user feedback —
        // it read as a 4-lane street rather than a vacant lot).
        const variant = Math.floor(rng() * 2);
        const padding = 16;
        const ix = bo.x + padding;
        const iy = bo.y + padding;
        const iw = bo.w - padding * 2;
        const ih = bo.h - padding * 2;

        if (variant === 0) {
            // Planter row — 4 small dark squares with cyan/violet/amber tops.
            const tops = [PALETTE.neon.cyan, PALETTE.neon.violet, PALETTE.neon.amber];
            for (let i = 0; i < 4; i++) {
                const px = ix + Math.floor(iw * (i + 0.5) / 4) - 3;
                const py = iy + Math.floor(ih / 2) - 3;
                ctx.fillStyle = PALETTE.wall.shade;
                ctx.fillRect(px, py, 6, 6);
                ctx.fillStyle = tops[Math.floor(rng() * tops.length)];
                ctx.fillRect(px + 1, py + 1, 4, 2);
            }
        } else {
            // Small plaza fountain — 10×10 dark disc with cyan ripple.
            const fx = bo.x + Math.floor(bo.w / 2);
            const fy = bo.y + Math.floor(bo.h / 2);
            ctx.fillStyle = PALETTE.wall.shade;
            for (let dy = -5; dy <= 5; dy++) {
                for (let dx = -5; dx <= 5; dx++) {
                    if (dx * dx + dy * dy <= 25) ctx.fillRect(fx + dx, fy + dy, 1, 1);
                }
            }
            ctx.fillStyle = PALETTE.neon.cyan;
            ctx.fillRect(fx - 2, fy - 2, 4, 4);
            // Inner darker centre + sparkle
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(fx, fy, 1, 1);
        }
    }

    function renderRadialPaths(ctx, layout) {
        const grid = layout.grid;
        if (!grid) return;
        const N = grid.n;
        const plotMap = grid.plotMap;
        const colKinds = grid.colKinds || new Array(N - 1).fill(STREET_KIND_STANDARD);
        const rowKinds = grid.rowKinds || new Array(N - 1).fill(STREET_KIND_STANDARD);

        // v12 — skip a street segment when both neighbouring cells are
        // blocked wasteland (the colony boundary). Drawing a road through
        // emptiness breaks the "circular colony" silhouette.
        const BLOCKED = grid.blockedOwner ?? -3;
        const bothBlocked = (a, b) => a === BLOCKED && b === BLOCKED;

        // Vertical street segments — kind is taken from colKinds[i].
        for (let i = 0; i < N - 1; i++) {
            const sx = CITY_PAD + (i + 1) * BLOCK_SIZE + i * STREET_W + Math.floor(STREET_W / 2);
            const kind = colKinds[i];
            for (let row = 0; row < N; row++) {
                if (sameNonEmptyPlot(plotMap[row][i], plotMap[row][i + 1])) continue;
                if (bothBlocked(plotMap[row][i], plotMap[row][i + 1])) continue;
                const sy0 = CITY_PAD + row * (BLOCK_SIZE + STREET_W);
                const sy1 = sy0 + BLOCK_SIZE;
                drawStraightStreet(ctx, sx, sy0, sx, sy1, false, kind);
            }
        }
        // Horizontal street segments — kind is taken from rowKinds[i].
        for (let i = 0; i < N - 1; i++) {
            const sy = CITY_PAD + (i + 1) * BLOCK_SIZE + i * STREET_W + Math.floor(STREET_W / 2);
            const kind = rowKinds[i];
            for (let col = 0; col < N; col++) {
                if (sameNonEmptyPlot(plotMap[i][col], plotMap[i + 1][col])) continue;
                if (bothBlocked(plotMap[i][col], plotMap[i + 1][col])) continue;
                const sx0 = CITY_PAD + col * (BLOCK_SIZE + STREET_W);
                const sx1 = sx0 + BLOCK_SIZE;
                drawStraightStreet(ctx, sx0, sy, sx1, sy, true, kind);
            }
        }

        // Intersections — pick crosswalk variant per intersection based on
        // its incident street kinds. Avenue intersections get 6-stripe;
        // alley intersections drop crosswalks; small standard crossings
        // randomly land on 3-stripe / 4-stripe / faded / absent.
        for (let row = 0; row < N - 1; row++) {
            for (let col = 0; col < N - 1; col++) {
                const nw = plotMap[row][col];
                const ne = plotMap[row][col + 1];
                const sw = plotMap[row + 1][col];
                const se = plotMap[row + 1][col + 1];
                if (nw !== -1 && nw === ne && ne === sw && sw === se) continue;
                // v12 — skip intersections where 3+ corners are blocked
                // wasteland so the colony boundary doesn't sprout phantom
                // crossings.
                let blockedCount = 0;
                if (nw === BLOCKED) blockedCount++;
                if (ne === BLOCKED) blockedCount++;
                if (sw === BLOCKED) blockedCount++;
                if (se === BLOCKED) blockedCount++;
                if (blockedCount >= 3) continue;
                const ix = CITY_PAD + (col + 1) * BLOCK_SIZE + col * STREET_W + Math.floor(STREET_W / 2);
                const iy = CITY_PAD + (row + 1) * BLOCK_SIZE + row * STREET_W + Math.floor(STREET_W / 2);
                const xkV = colKinds[col];                     // crossing column kind
                const xkH = rowKinds[row];
                const xwalk = pickCrosswalkVariant(xkV, xkH, col, row);
                drawIntersection(ctx, ix, iy, xkV, xkH, xwalk);
            }
        }

        // Crosswalks at the plaza entries (cardinal directions). The plaza
        // is now a multi-cell patch — span the entire side of its bounding
        // box for the crosswalks.
        const plazaCols = grid.plazaCols || 1;
        const plazaRows = grid.plazaRows || 1;
        const plazaW = plazaCols * BLOCK_SIZE + (plazaCols - 1) * STREET_W;
        const plazaH = plazaRows * BLOCK_SIZE + (plazaRows - 1) * STREET_W;
        const px = CITY_PAD + grid.plazaCol * (BLOCK_SIZE + STREET_W);
        const py = CITY_PAD + grid.plazaRow * (BLOCK_SIZE + STREET_W);
        const pCx = px + plazaW / 2;
        const pCy = py + plazaH / 2;
        drawCrosswalk(ctx, pCx, py - 2, 0, -1);
        drawCrosswalk(ctx, pCx, py + plazaH + 2, 0, 1);
        drawCrosswalk(ctx, px - 2, pCy, -1, 0);
        drawCrosswalk(ctx, px + plazaW + 2, pCy, 1, 0);
    }

    function sameNonEmptyPlot(a, b) {
        return a !== -1 && a === b;
    }

    // Paint one perpendicular cross-section of road at (px, py).
    // Grid v8: 3/4 perspective — horizontal streets have an asymmetric
    // profile, with the SOUTH sidewalk wider than the NORTH one (the
    // south side is "closer" to the camera in top-down 3/4 view).
    // Vertical streets stay symmetric.
    //
    // `progress` advances along the road and drives the dashed-centerline
    // + asphalt-grain rngs. `horizontal=true` triggers the asymmetric profile.
    function paintRoadCrossSection(ctx, px, py, perpDx, perpDy, progress, textureRng, horizontal, kind) {
        const k = kind == null ? STREET_KIND_STANDARD : kind;
        const dash = (Math.floor(progress) % ROAD_DASH_PERIOD) < ROAD_DASH_ON;

        // Alleys are all concrete (no asphalt) — simple pedestrian path.
        if (k === STREET_KIND_ALLEY) {
            for (let p = -ROAD_HALF_W; p <= ROAD_HALF_W; p++) {
                const ex = Math.round(px + perpDx * p);
                const ey = Math.round(py + perpDy * p);
                if (ex < 0 || ex >= state.cityWidth || ey < 0 || ey >= state.cityHeight) continue;
                let color = PALETTE.street.sidewalk;
                if (textureRng && textureRng() < 0.14) color = PALETTE.street.sidewalkHi;
                if (textureRng && textureRng() < 0.06) color = PALETTE.street.sidewalkLo;
                ctx.fillStyle = color;
                ctx.fillRect(ex, ey, 1, 1);
            }
            return;
        }

        // v16 — AVENUE: 4-lane road with a wide median strip. 6 px sidewalk
        // each side leaves 36 px of asphalt for two lanes each direction
        // plus a visible 3-px painted median.
        if (k === STREET_KIND_AVENUE) {
            const northSidewalk = horizontal ? 5 : 6;
            const southSidewalk = horizontal ? 7 : 6;
            const northCurbOffset = ROAD_HALF_W - northSidewalk;
            const southCurbOffset = ROAD_HALF_W - southSidewalk;
            const curbStoneJoint = (Math.floor(progress) % 6) === 0;

            for (let p = -ROAD_HALF_W; p <= ROAD_HALF_W; p++) {
                const ex = Math.round(px + perpDx * p);
                const ey = Math.round(py + perpDy * p);
                if (ex < 0 || ex >= state.cityWidth || ey < 0 || ey >= state.cityHeight) continue;
                const absP = Math.abs(p);
                const isSouth = p > 0;
                const curbOffset = isSouth ? southCurbOffset : northCurbOffset;
                let color;
                if (absP > curbOffset) {
                    color = PALETTE.street.sidewalk;
                    if (textureRng && textureRng() < 0.12) color = PALETTE.street.sidewalkHi;
                } else if (absP === curbOffset || absP === curbOffset - 1) {
                    color = curbStoneJoint && absP === curbOffset - 1
                        ? PALETTE.shadow
                        : PALETTE.street.curb;
                } else if (absP <= 2) {
                    // 5-px wide painted centre median (p = -2..+2).
                    color = PALETTE.street.lane;
                } else if ((absP === 6 || absP === 10) && dash) {
                    // Two lane-separator dashes per direction.
                    color = PALETTE.street.lane;
                } else {
                    color = (textureRng && textureRng() < 0.08)
                        ? PALETTE.street.asphaltLo
                        : PALETTE.street.asphalt;
                }
                ctx.fillStyle = color;
                ctx.fillRect(ex, ey, 1, 1);
            }
            return;
        }

        // v16 — STANDARD profile with 22-px asphalt body, room for proper
        // two-lane detail and crisp paving on the sidewalk.
        //   |sidewalk 10|curb 2|asphalt 10|median 2|asphalt 10|curb 2|sidewalk 10|
        // North sidewalk is 1 px narrower than south to keep the 3/4
        // perspective trick.
        const northSidewalk = horizontal ? 9  : 10;
        const southSidewalk = horizontal ? 11 : 10;
        const northCurbOffset = ROAD_HALF_W - northSidewalk;
        const southCurbOffset = ROAD_HALF_W - southSidewalk;
        const curbStoneJoint = (Math.floor(progress) % 6) === 0;
        // Paving-tile texture seam — 1 line every 8 px across the sidewalk.
        const tileSeam = (Math.floor(progress) % 8) === 0;

        for (let p = -ROAD_HALF_W; p <= ROAD_HALF_W; p++) {
            const ex = Math.round(px + perpDx * p);
            const ey = Math.round(py + perpDy * p);
            if (ex < 0 || ex >= state.cityWidth || ey < 0 || ey >= state.cityHeight) continue;
            const absP = Math.abs(p);
            const isSouth = p > 0;
            const curbOffset = isSouth ? southCurbOffset : northCurbOffset;
            let color;
            if (absP > curbOffset) {
                // SIDEWALK — wider region with paving-tile seams every 8 px
                // and per-pixel speckle for grime + grout.
                color = PALETTE.street.sidewalk;
                if (tileSeam && absP === curbOffset + 1) {
                    color = PALETTE.street.sidewalkLo;     // 1-px seam shadow
                } else if (textureRng && textureRng() < 0.14) {
                    color = PALETTE.street.sidewalkHi;
                }
            } else if (absP === curbOffset || absP === curbOffset - 1) {
                // 2-px tall curb with stone-joint cadence.
                color = curbStoneJoint && absP === curbOffset - 1
                    ? PALETTE.shadow
                    : PALETTE.street.curb;
            } else if (absP === 1) {
                // Solid centre median — 2 px wide (p = -1 and p = +1).
                color = PALETTE.street.lane;
            } else if (absP === 6 && dash) {
                // Dashed lane-separator markings, one per direction
                // (between the slow and fast lane).
                color = PALETTE.street.lane;
            } else {
                // v16 — asphalt with potholes, oil stains, and cracks
                // sprinkled in. Per-pixel deterministic hash keyed off
                // world coords so wear is stable across renders.
                const wear = ((ex * 73856093) ^ (ey * 19349663)) >>> 0;
                const w = (wear & 0xFF) / 255;
                if (w < 0.010) {
                    color = PALETTE.shadow;                       // pothole / deep crack
                } else if (w < 0.030) {
                    color = PALETTE.street.asphaltLo;             // oil patch
                } else if (textureRng && textureRng() < 0.08) {
                    color = PALETTE.street.asphaltLo;             // grain noise
                } else {
                    color = PALETTE.street.asphalt;
                }
            }
            ctx.fillStyle = color;
            ctx.fillRect(ex, ey, 1, 1);
        }
    }

    function drawStraightStreet(ctx, x0, y0, x1, y1, horizontal, kind) {
        const textureRng = mulberry32(((horizontal ? 0xB04D : 0xA12C) ^ ((x0 + y0) | 0)) >>> 0);
        if (horizontal) {
            const xMin = Math.min(x0, x1), xMax = Math.max(x0, x1);
            for (let xx = xMin; xx <= xMax; xx++) {
                paintRoadCrossSection(ctx, xx, y0, 0, 1, xx - xMin, textureRng, true, kind);
            }
        } else {
            const yMin = Math.min(y0, y1), yMax = Math.max(y0, y1);
            for (let yy = yMin; yy <= yMax; yy++) {
                paintRoadCrossSection(ctx, x0, yy, 1, 0, yy - yMin, textureRng, false, kind);
            }
        }
        // v17 — curbside parked cars on standard / avenue roads (alleys
        // have no asphalt so no parking). Cars sit on the asphalt 2 px
        // inside the curb on alternating sides, spaced ~50 px apart.
        if (kind !== STREET_KIND_ALLEY) {
            drawCurbsideParkedCars(ctx, x0, y0, x1, y1, horizontal, textureRng);
        }
    }

    // Place 1-3 parked cars along ONE curb of the road segment between
    // (x0, y0) and (x1, y1). Cars hug the curb, oriented parallel to the
    // road. Side (north/south, east/west) is picked by a per-segment hash
    // so the same segment always gets the same parking layout.
    function drawCurbsideParkedCars(ctx, x0, y0, x1, y1, horizontal, textureRng) {
        const carColors = [PALETTE.neon.amber, PALETTE.neon.cyan, PALETTE.neon.pink, PALETTE.neon.violet];
        const len = horizontal ? Math.abs(x1 - x0) : Math.abs(y1 - y0);
        if (len < 50) return;
        // Pick side: -1 means north/west, +1 means south/east.
        const sideHash = ((x0 * 31 + y0 * 17) ^ 0xA5A5) >>> 0;
        const side = ((sideHash & 1) === 0) ? -1 : 1;
        // 1-3 cars per segment.
        const carCount = 1 + (((sideHash >>> 1) & 3) % 3);
        const carInset = (ROAD_HALF_W - ROAD_SIDEWALK_W - 4); // 10 — just inside the curb
        for (let i = 0; i < carCount; i++) {
            const t = (i + 1) / (carCount + 1);
            const carColorIdx = ((sideHash >>> (3 + i * 2)) & 3);
            const color = carColors[carColorIdx];
            if (horizontal) {
                const cx = Math.min(x0, x1) + Math.floor(t * len) - 4;       // 8 wide car
                const cy = y0 + side * carInset - 2;                          // 4 tall car
                drawCarOnAsphalt(ctx, cx, cy, color, true);
            } else {
                const cx = x0 + side * carInset - 2;                          // 4 wide
                const cy = Math.min(y0, y1) + Math.floor(t * len) - 4;       // 8 tall
                drawCarOnAsphalt(ctx, cx, cy, color, false);
            }
        }
    }

    function drawCarOnAsphalt(ctx, x, y, color, horizontal) {
        // 8×4 horizontal or 4×8 vertical pixel-art car. Drop shadow + body
        // + cabin glass + 2 headlights + 2 taillights.
        if (x < 0 || y < 0) return;
        if (horizontal) {
            if (x + 8 >= state.cityWidth || y + 4 >= state.cityHeight) return;
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(x, y + 3, 8, 1);
            ctx.fillStyle = color;
            ctx.fillRect(x, y, 8, 3);
            ctx.fillStyle = PALETTE.window.dark;
            ctx.fillRect(x + 2, y + 1, 4, 1);
            ctx.fillStyle = PALETTE.neon.cyan;
            ctx.fillRect(x, y + 1, 1, 1);
            ctx.fillRect(x + 7, y + 1, 1, 1);
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(x + 1, y, 1, 1);
            ctx.fillRect(x + 6, y, 1, 1);
        } else {
            if (x + 4 >= state.cityWidth || y + 8 >= state.cityHeight) return;
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(x + 3, y, 1, 8);
            ctx.fillStyle = color;
            ctx.fillRect(x, y, 3, 8);
            ctx.fillStyle = PALETTE.window.dark;
            ctx.fillRect(x + 1, y + 2, 1, 4);
            ctx.fillStyle = PALETTE.neon.cyan;
            ctx.fillRect(x + 1, y, 1, 1);
            ctx.fillRect(x + 1, y + 7, 1, 1);
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(x, y + 1, 1, 1);
            ctx.fillRect(x, y + 6, 1, 1);
        }
    }

    function pickCrosswalkVariant(colKind, rowKind, col, row) {
        // Avenue intersections always paint a busy 6-stripe crosswalk.
        if (colKind === STREET_KIND_AVENUE || rowKind === STREET_KIND_AVENUE) return 6;
        // Alley intersections are pedestrian-only, no zebra paint.
        if (colKind === STREET_KIND_ALLEY || rowKind === STREET_KIND_ALLEY) return 0;
        // Standard intersections pick from {3, 4, faded(-4), absent(0)}.
        const r = ((col * 73856093) ^ (row * 19349663)) & 7;
        if (r < 2) return 0;            // ~25 % absent
        if (r < 3) return -4;           // ~12 % faded
        if (r < 5) return 3;            // ~25 % 3-stripe
        return 4;                       // ~37 % 4-stripe (the default)
    }

    // v16 — intersection rewritten for STREET_W=48. The previous logic
    // used `absX === r-1 / r-2` offsets which assumed thin sidewalks;
    // those don't match the new 10-px sidewalks + 2-px curb profile.
    function drawIntersection(ctx, cxp, cyp, colKind, rowKind, crosswalkVariant) {
        const r = ROAD_HALF_W;
        const sw = ROAD_SIDEWALK_W;
        const curbBand = 2;                              // curb is 2 px tall
        const asphaltEdge = r - sw - curbBand;           // inner edge of curb

        // Alley × alley → sidewalk square.
        if (colKind === STREET_KIND_ALLEY && rowKind === STREET_KIND_ALLEY) {
            ctx.fillStyle = PALETTE.street.sidewalk;
            for (let dy = -r; dy <= r; dy++) {
                for (let dx = -r; dx <= r; dx++) {
                    const x = cxp + dx, y = cyp + dy;
                    if (x < 0 || x >= state.cityWidth || y < 0 || y >= state.cityHeight) continue;
                    ctx.fillRect(x, y, 1, 1);
                }
            }
            return;
        }

        // The intersection is mostly asphalt with sidewalk only in the FOUR
        // CORNERS (where the perpendicular streets' sidewalks meet). The
        // asphalt arms extend straight through the middle.
        for (let dy = -r; dy <= r; dy++) {
            for (let dx = -r; dx <= r; dx++) {
                const x = cxp + dx, y = cyp + dy;
                if (x < 0 || x >= state.cityWidth || y < 0 || y >= state.cityHeight) continue;
                const absX = Math.abs(dx), absY = Math.abs(dy);
                // Both axes inside the asphalt band? It's roadway.
                const onAsphaltX = absX < asphaltEdge;
                const onAsphaltY = absY < asphaltEdge;
                // Corner sidewalk: both axes are in the sidewalk band.
                const inSidewalkX = absX > asphaltEdge + curbBand - 1;
                const inSidewalkY = absY > asphaltEdge + curbBand - 1;
                let color;
                if (inSidewalkX && inSidewalkY) {
                    color = PALETTE.street.sidewalk;
                    // Paving-tile speckle on the corners for richness.
                    const cellSeed = (x * 73856093) ^ (y * 19349663);
                    if (((cellSeed >>> 7) & 7) === 0) color = PALETTE.street.sidewalkHi;
                    else if (((cellSeed >>> 11) & 15) === 0) color = PALETTE.street.sidewalkLo;
                } else if ((absX === asphaltEdge || absX === asphaltEdge + 1) && inSidewalkY) {
                    // Curb running into the corner from the east/west arms.
                    color = PALETTE.street.curb;
                } else if ((absY === asphaltEdge || absY === asphaltEdge + 1) && inSidewalkX) {
                    // Curb running into the corner from the north/south arms.
                    color = PALETTE.street.curb;
                } else {
                    // INSIDE the intersection asphalt area.
                    color = PALETTE.street.asphalt;
                    // v16 — lane-continuation guide marks. At the intersection
                    // centre we add a small 1-px dot pattern showing where the
                    // dashed lane separators would continue.
                    const onLaneSeparator = (absX === 6 || absX === 10 || absY === 6 || absY === 10);
                    const onCentreMedian = (absX <= 1 && onAsphaltY && !onAsphaltX) ||
                                           (absY <= 1 && onAsphaltX && !onAsphaltY);
                    // Centre median continues into the cross-street arms but
                    // NOT across the centre of the intersection (would conflict
                    // with traffic flowing through).
                    if (onCentreMedian) {
                        color = PALETTE.street.lane;
                    } else if (onLaneSeparator && (absX > asphaltEdge - 4 || absY > asphaltEdge - 4)) {
                        // Stop-line zone: dashed marker just inside the asphalt
                        // edge, fading into the intersection centre.
                        const sx = (absX + absY) & 1;
                        if (sx === 0) color = PALETTE.street.lane;
                    }
                }
                ctx.fillStyle = color;
                ctx.fillRect(x, y, 1, 1);
            }
        }

        // STOP LINES — solid 2-px-thick painted line at each intersection
        // entry, just inside the asphalt boundary.
        if (rowKind !== STREET_KIND_ALLEY) {
            ctx.fillStyle = PALETTE.street.lane;
            for (let dx = -(asphaltEdge - 1); dx <= asphaltEdge - 1; dx++) {
                if (Math.abs(dx) <= 1) continue;        // gap over the centre median
                // North entry stop line.
                ctx.fillRect(cxp + dx, cyp - asphaltEdge + 1, 1, 1);
                ctx.fillRect(cxp + dx, cyp - asphaltEdge + 2, 1, 1);
                // South entry stop line.
                ctx.fillRect(cxp + dx, cyp + asphaltEdge - 2, 1, 1);
                ctx.fillRect(cxp + dx, cyp + asphaltEdge - 3, 1, 1);
            }
        }
        if (colKind !== STREET_KIND_ALLEY) {
            ctx.fillStyle = PALETTE.street.lane;
            for (let dy = -(asphaltEdge - 1); dy <= asphaltEdge - 1; dy++) {
                if (Math.abs(dy) <= 1) continue;
                ctx.fillRect(cxp - asphaltEdge + 1, cyp + dy, 1, 1);
                ctx.fillRect(cxp - asphaltEdge + 2, cyp + dy, 1, 1);
                ctx.fillRect(cxp + asphaltEdge - 2, cyp + dy, 1, 1);
                ctx.fillRect(cxp + asphaltEdge - 3, cyp + dy, 1, 1);
            }
        }

        // Crosswalks — drawn JUST OUTSIDE the stop lines so they sit
        // between the stop line and the sidewalk.
        if (crosswalkVariant !== 0) {
            const cwOffset = asphaltEdge - 1;     // crosswalk centre offset
            if (rowKind !== STREET_KIND_ALLEY) {
                drawCrosswalk(ctx, cxp, cyp - cwOffset, 0, -1, crosswalkVariant);
                drawCrosswalk(ctx, cxp, cyp + cwOffset, 0,  1, crosswalkVariant);
            }
            if (colKind !== STREET_KIND_ALLEY) {
                drawCrosswalk(ctx, cxp - cwOffset, cyp, -1, 0, crosswalkVariant);
                drawCrosswalk(ctx, cxp + cwOffset, cyp,  1, 0, crosswalkVariant);
            }
        }

        // v24 — TURN ARROWS painted on the right-hand lane of each
        // approach, ~6 px back from the stop line, pointing INTO the
        // intersection. Reads as proper urban road markings.
        // Each arrow is a 5×7 pixel-art chevron in PALETTE.street.lane.
        if (rowKind !== STREET_KIND_ALLEY) {
            // South-approach arrow on the EAST lane (heading north).
            drawTurnArrow(ctx, cxp + 6, cyp + asphaltEdge - 9, 'up');
            // North-approach arrow on the WEST lane (heading south).
            drawTurnArrow(ctx, cxp - 6, cyp - asphaltEdge + 3, 'down');
        }
        if (colKind !== STREET_KIND_ALLEY) {
            // West-approach arrow on the SOUTH lane (heading east).
            drawTurnArrow(ctx, cxp - asphaltEdge + 3, cyp + 6, 'right');
            // East-approach arrow on the NORTH lane (heading west).
            drawTurnArrow(ctx, cxp + asphaltEdge - 9, cyp - 6, 'left');
        }

        // v17 — TRAFFIC LIGHTS at intersection corners. One per non-alley
        // direction, placed on the sidewalk just inside the corner, so
        // they read as governing the crosswalk to their south or west.
        // Skip if the intersection has no asphalt arms (alley × alley
        // already early-returned above).
        if (rowKind !== STREET_KIND_ALLEY && colKind !== STREET_KIND_ALLEY) {
            // NE corner traffic light — controls the southward crosswalk
            // entry on the east side.
            drawIntersectionTrafficLight(ctx, cxp + asphaltEdge + 3, cyp - asphaltEdge - 16);
            // SW corner traffic light — controls the northward crosswalk
            // entry on the west side.
            drawIntersectionTrafficLight(ctx, cxp - asphaltEdge - 8, cyp + asphaltEdge - 2);
        }
    }

    // v24 — Draw a 5×7 turn arrow at world (x, y) pointing in the given
    // direction ('up' / 'down' / 'left' / 'right'). Painted in
    // PALETTE.street.lane (same colour as lane markings). Each shape is
    // a pixel-art chevron with shaft.
    function drawTurnArrow(ctx, x, y, dir) {
        ctx.fillStyle = PALETTE.street.lane;
        if (dir === 'up') {
            // chevron pointing up + 5-tall shaft below
            ctx.fillRect(x + 2, y,     1, 1);   // tip
            ctx.fillRect(x + 1, y + 1, 3, 1);
            ctx.fillRect(x,     y + 2, 5, 1);
            ctx.fillRect(x + 2, y + 3, 1, 4);   // shaft
        } else if (dir === 'down') {
            ctx.fillRect(x + 2, y,     1, 4);   // shaft
            ctx.fillRect(x,     y + 4, 5, 1);
            ctx.fillRect(x + 1, y + 5, 3, 1);
            ctx.fillRect(x + 2, y + 6, 1, 1);   // tip
        } else if (dir === 'right') {
            // chevron pointing right + 5-wide shaft to the left
            ctx.fillRect(x,     y + 2, 4, 1);   // shaft
            ctx.fillRect(x + 4, y,     1, 5);
            ctx.fillRect(x + 5, y + 1, 1, 3);
            ctx.fillRect(x + 6, y + 2, 1, 1);   // tip
        } else if (dir === 'left') {
            ctx.fillRect(x,     y + 2, 1, 1);   // tip
            ctx.fillRect(x + 1, y + 1, 1, 3);
            ctx.fillRect(x + 2, y,     1, 5);
            ctx.fillRect(x + 3, y + 2, 4, 1);   // shaft
        }
    }

    // Draw a single 3-lamp traffic light at world (x, y). Lifted from the
    // legacy per-plot trafficLight() prop so intersection placement uses
    // the same visual.
    function drawIntersectionTrafficLight(ctx, x, y) {
        if (x < 0 || y < 0 || x + 6 >= state.cityWidth || y + 18 >= state.cityHeight) return;
        ctx.fillStyle = PALETTE.wall.hi;
        ctx.fillRect(x + 2, y + 11, 2, 7);
        ctx.fillStyle = PALETTE.wall.shade;
        ctx.fillRect(x + 1, y + 17, 4, 1);
        ctx.fillRect(x, y, 6, 11);
        ctx.fillStyle = PALETTE.shadow;
        ctx.fillRect(x + 5, y, 1, 11);
        ctx.fillRect(x, y + 10, 6, 1);
        ctx.fillStyle = PALETTE.wall.hi;
        ctx.fillRect(x, y, 6, 1);
        ctx.fillRect(x, y, 1, 11);
        const lamps = [
            { dy: 1, color: PALETTE.neon.pink },
            { dy: 4, color: PALETTE.neon.amber },
            { dy: 7, color: PALETTE.neon.cyan },
        ];
        for (const L of lamps) {
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(x + 1, y + L.dy, 4, 3);
            ctx.fillStyle = L.color;
            ctx.fillRect(x + 2, y + L.dy + 1, 2, 2);
            ctx.globalAlpha = 0.45;
            ctx.fillRect(x + 1, y + L.dy + 2, 1, 1);
            ctx.fillRect(x + 4, y + L.dy + 2, 1, 1);
            ctx.globalAlpha = 1.0;
        }
    }

    // v16 — crosswalk rewritten for STREET_W=48. Stripes now fit within
    // the 22-px asphalt body (was using ROAD_HALF_W*2 which overflowed
    // onto the sidewalk). Each stripe is 2 px wide and 6 px deep (a
    // proper white zebra band), spaced to fill the asphalt edge-to-edge.
    function drawCrosswalk(ctx, px, py, dirDx, dirDy, variant) {
        const v = (variant == null) ? 4 : variant;
        if (v === 0) return;
        const count = Math.abs(v);
        const faded = v < 0;
        const perpDx = -dirDy;
        const perpDy =  dirDx;
        const depth = 6;
        const stripeW = 2;            // each painted stripe is 2 px wide
        // Asphalt width = 2*(ROAD_HALF_W - ROAD_SIDEWALK_W - 2). Crosswalk
        // sits ENTIRELY inside this band, with a 2-px gutter on each side
        // (1 px to avoid the centre median + 1 px to clear the lane edge).
        const asphaltHalf = ROAD_HALF_W - ROAD_SIDEWALK_W - 2;     // 12
        const fillSpan = (asphaltHalf - 1) * 2;                    // 22
        const totalStripeWidth = count * stripeW;
        const gapCount = count + 1;
        const stripeStride = Math.floor((fillSpan - totalStripeWidth) / gapCount) + stripeW;
        const startS = -Math.floor(count * stripeStride / 2) + Math.floor(stripeStride / 2);
        ctx.fillStyle = PALETTE.street.cross;
        for (let i = 0; i < count; i++) {
            const sCentre = startS + i * stripeStride;
            if (faded && (i & 1) === 1) {
                ctx.globalAlpha = 0.35;
            } else if (faded) {
                ctx.globalAlpha = 0.65;
            } else {
                ctx.globalAlpha = 1.0;
            }
            for (let sw = -Math.floor(stripeW / 2); sw < Math.ceil(stripeW / 2); sw++) {
                const s = sCentre + sw;
                for (let d = 0; d < depth; d++) {
                    const ex = Math.round(px + perpDx * s + dirDx * d);
                    const ey = Math.round(py + perpDy * s + dirDy * d);
                    if (ex >= 0 && ex < state.cityWidth && ey >= 0 && ey < state.cityHeight) {
                        ctx.fillRect(ex, ey, 1, 1);
                    }
                }
            }
        }
        ctx.globalAlpha = 1.0;
    }

    function drawRoundabout(ctx, cxp, cyp) {
        // Outer sidewalk ring → curb → asphalt fill → central planted island.
        const R_OUTER     = 11;                          // includes neon trim + sidewalk
        const R_SIDEWALK  = R_OUTER - 1;
        const R_CURB      = R_SIDEWALK - ROAD_SIDEWALK_W;
        const R_ASPHALT   = R_CURB - 1;
        const R_ISLAND    = 2;

        for (let dy = -R_OUTER; dy <= R_OUTER; dy++) {
            for (let dx = -R_OUTER; dx <= R_OUTER; dx++) {
                const d2 = dx * dx + dy * dy;
                if (d2 > R_OUTER * R_OUTER) continue;
                const x = cxp + dx, y = cyp + dy;
                if (x < 0 || x >= state.cityWidth || y < 0 || y >= state.cityHeight) continue;

                let color;
                if (d2 > R_SIDEWALK * R_SIDEWALK) {
                    color = PALETTE.street.edge;             // neon trim
                } else if (d2 > R_CURB * R_CURB) {
                    color = PALETTE.street.sidewalk;
                } else if (d2 > R_ASPHALT * R_ASPHALT) {
                    color = PALETTE.street.curb;
                } else if (d2 > R_ISLAND * R_ISLAND) {
                    color = PALETTE.street.asphalt;
                } else {
                    color = PALETTE.plaza.floorHi;           // central planted island
                }
                ctx.fillStyle = color;
                ctx.fillRect(x, y, 1, 1);
            }
        }
        // Central plaza-marker dot keeps the roundabout reading as a
        // deliberate landmark from far zoom.
        ctx.fillStyle = PALETTE.plaza.ring;
        ctx.fillRect(cxp - 1, cyp - 1, 2, 2);
    }

    function renderFootprintEdge(ctx, b) {
        // 1-px hot magenta outline 2 px outside building's AABB.
        ctx.fillStyle = PALETTE.street.edge;
        ctx.fillRect(b.x - 2, b.y - 2, b.w + 4, 1);            // top
        ctx.fillRect(b.x - 2, b.y + b.h + 1, b.w + 4, 1);      // bottom
        ctx.fillRect(b.x - 2, b.y - 2, 1, b.h + 4);            // left
        ctx.fillRect(b.x + b.w + 1, b.y - 2, 1, b.h + 4);      // right
        // Softer 1-px glow further out.
        ctx.globalAlpha = 0.40;
        ctx.fillRect(b.x - 3, b.y - 3, b.w + 6, 1);
        ctx.fillRect(b.x - 3, b.y + b.h + 2, b.w + 6, 1);
        ctx.fillRect(b.x - 3, b.y - 3, 1, b.h + 6);
        ctx.fillRect(b.x + b.w + 2, b.y - 3, 1, b.h + 6);
        ctx.globalAlpha = 1.0;
    }

    // =========================================================
    //   STREET-LEVEL DECORATIONS — organic placement (#21)
    //   Lampposts ring the plaza + a sparse halo at the outer
    //   ring radius. NPCs and vehicles scatter in void areas
    //   (cells not occupied by any building's AABB).
    //   All static + deterministic; baked into the ground canvas.
    // =========================================================
    function drawLamppost(ctx, x, y) {
        ctx.fillStyle = PALETTE.shadow;
        ctx.globalAlpha = 0.5;
        ctx.fillRect(x + 1, y + 2, 3, 2);
        ctx.globalAlpha = 1.0;
        ctx.fillStyle = PALETTE.wall.hi;
        ctx.fillRect(x + 1, y, 1, 3);
        ctx.fillStyle = PALETTE.neon.cyan;
        ctx.fillRect(x, y - 1, 3, 1);
        ctx.fillRect(x + 1, y, 1, 1);
        ctx.globalAlpha = 0.45;
        ctx.fillRect(x - 1, y - 1, 5, 1);
        ctx.fillRect(x, y - 2, 3, 1);
        ctx.globalAlpha = 1.0;
    }

    // Lamppost layout — one lamp at every road intersection of the grid.
    // Drives both the cached ground draw and the lens-flare overlay (#25),
    // so flares stay aligned with the actual street network (was based on
    // legacy RING_RADII before — flagged by Hermes review on PR #39).
    function generateLamppostPositions(layout) {
        if (!layout || !layout.grid) return [];
        const grid = layout.grid;
        const out = [];
        // v9a — place a lamp only on a checkerboard of intersections,
        // not at every one. Previous behaviour put a cyan dot at every
        // road crossing and made the grid read as a PCB; this halves
        // the count + plus we now drop them at intersections that fall
        // entirely inside a multi-cell plot (no road to light).
        const plotMap = grid.plotMap;
        const BLOCKED = grid.blockedOwner ?? -3;     // v12 — radial wasteland
        for (let row = 0; row < grid.n - 1; row++) {
            for (let col = 0; col < grid.n - 1; col++) {
                if (((col + row) & 1) !== 0) continue;          // checkerboard
                if (plotMap) {
                    const nw = plotMap[row][col];
                    const ne = plotMap[row][col + 1];
                    const sw = plotMap[row + 1][col];
                    const se = plotMap[row + 1][col + 1];
                    // Skip intersections that lie entirely inside one plot.
                    if (nw !== -1 && nw === ne && ne === sw && sw === se) continue;
                    // v12 — skip intersections in wasteland (3+ blocked
                    // corners), matching the road/intersection cull in
                    // renderGround so no phantom lamppost / lens flare
                    // hovers in the void. Hermes review #203.
                    let blocked = 0;
                    if (nw === BLOCKED) blocked++;
                    if (ne === BLOCKED) blocked++;
                    if (sw === BLOCKED) blocked++;
                    if (se === BLOCKED) blocked++;
                    if (blocked >= 3) continue;
                }
                const x = grid.pad + (col + 1) * grid.blockSize + col * grid.streetW
                          + Math.floor(grid.streetW / 2);
                const y = grid.pad + (row + 1) * grid.blockSize + row * grid.streetW
                          + Math.floor(grid.streetW / 2);
                out.push({ x, y });
            }
        }
        return out;
    }

    function renderLamppostsOrganic(ctx, lampposts) {
        if (!lampposts) return;
        for (let i = 0; i < lampposts.length; i++) {
            drawLamppost(ctx, lampposts[i].x, lampposts[i].y);
        }
    }

    // v13 — skywalks between adjacent tier-3+ buildings. The reference's
    // signature "cyberpunk megacity" feature is the cyan-lit pedestrian
    // bridges spanning streets between tower roofs. We detect pairs of
    // high-tier buildings whose plots are within 1 cell of each other and
    // draw a 4-px-wide cyan bridge connecting their roofs.
    function generateSkywalks(buildings) {
        if (!buildings || !buildings.length) return [];
        const out = [];

        // Build a quick (blockCol, blockRow) → building index for the
        // closest pairings. We connect a building to one neighbour east
        // and one neighbour south at most (no diagonals) so we avoid
        // tangled X-shaped bridge networks.
        function plotEnd(b, axis) {
            return axis === 'x' ? (b.blockCol + b.plotCols - 1)
                                : (b.blockRow + b.plotRows - 1);
        }
        function findEast(b) {
            for (const o of buildings) {
                if (o === b) continue;
                if (o.tier < 3) continue;
                if (o.blockRow !== b.blockRow) continue;
                const gap = o.blockCol - plotEnd(b, 'x') - 1;
                if (gap === 1 || gap === 0) return o;
            }
            return null;
        }
        function findSouth(b) {
            for (const o of buildings) {
                if (o === b) continue;
                if (o.tier < 3) continue;
                if (o.blockCol !== b.blockCol) continue;
                const gap = o.blockRow - plotEnd(b, 'y') - 1;
                if (gap === 1 || gap === 0) return o;
            }
            return null;
        }

        const seen = new Set();
        const keyFor = (a, b) => a.id < b.id ? a.id + '-' + b.id : b.id + '-' + a.id;

        for (const b of buildings) {
            if (b.tier < 3) continue;
            const e = findEast(b);
            if (e) {
                const k = keyFor(b, e);
                if (!seen.has(k)) {
                    seen.add(k);
                    out.push({ from: b, to: e, horizontal: true });
                }
            }
            const s = findSouth(b);
            if (s) {
                const k = keyFor(b, s);
                if (!seen.has(k)) {
                    seen.add(k);
                    out.push({ from: b, to: s, horizontal: false });
                }
            }
        }
        return out;
    }

    function renderSkywalks(ctx, skywalks, camX, camY, W, H, animFrame) {
        if (!skywalks || !skywalks.length) return;
        const bri = isReducedMotion() ? 0.85
                  : (0.65 + 0.20 * Math.abs(Math.sin((animFrame | 0) / 5)));
        const cyan = dimColor(PALETTE.neon.cyan, bri);
        for (const w of skywalks) {
            const A = w.from, B = w.to;
            // v13 — validate both endpoints are still tier-3+ at render
            // time. updateBuilding() can drop a building's tier after
            // setBuildings() generated the skywalk list (Hermes review
            // #205); without this check the bridge stays drawn even after
            // its endpoints stopped being towers.
            if (A.tier < 3 || B.tier < 3) continue;
            // Roof centre points of each building.
            const ax = A.x + Math.floor(A.w / 2);
            const ay = A.y + Math.floor(A.h / 2);
            const bx = B.x + Math.floor(B.w / 2);
            const by = B.y + Math.floor(B.h / 2);
            if (w.horizontal) {
                // Horizontal bridge across the street between two columns.
                // Y is centred vertically between the two roofs so the
                // bridge meets the buildings near their roof mid-points.
                const cy = Math.floor((A.y + A.h / 2 + B.y + B.h / 2) / 2) - 2;
                // X span: from the right edge of the left building (with
                // 2 px overlap) to the left edge of the right building.
                const leftBldg = (A.x < B.x) ? A : B;
                const rightBldg = (A.x < B.x) ? B : A;
                const left = leftBldg.x + leftBldg.w - 3;
                const right = rightBldg.x + 3;
                if (right <= left) continue;
                const sx = left - camX;
                const sy = cy - camY;
                const len = right - left;
                if (sx + len < 0 || sx > W || sy < -8 || sy > H) continue;
                // Dark base.
                ctx.fillStyle = PALETTE.shadow;
                ctx.fillRect(sx, sy, len, 4);
                // Cyan-lit inner glass.
                ctx.fillStyle = cyan;
                ctx.fillRect(sx, sy + 1, len, 2);
                // Bright top edge.
                ctx.fillStyle = PALETTE.neon.cyan;
                ctx.fillRect(sx, sy, len, 1);
                // Cross-struts every 4 px — implies "glass-floor walkway".
                ctx.fillStyle = PALETTE.shadow;
                for (let g = 2; g < len - 2; g += 4) {
                    ctx.fillRect(sx + g, sy + 1, 1, 2);
                }
            } else {
                // Vertical bridge between two rows. X centred horizontally
                // between the two roofs.
                const cx = Math.floor((A.x + A.w / 2 + B.x + B.w / 2) / 2) - 2;
                const topBldg = (A.y < B.y) ? A : B;
                const botBldg = (A.y < B.y) ? B : A;
                const top = topBldg.y + topBldg.h - 3;
                const bot = botBldg.y + 3;
                if (bot <= top) continue;
                const sy = top - camY;
                const sx = cx - camX;
                const len = bot - top;
                if (sx < -8 || sx > W || sy + len < 0 || sy > H) continue;
                ctx.fillStyle = PALETTE.shadow;
                ctx.fillRect(sx, sy, 4, len);
                ctx.fillStyle = cyan;
                ctx.fillRect(sx + 1, sy, 2, len);
                ctx.fillStyle = PALETTE.neon.cyan;
                ctx.fillRect(sx, sy, 1, len);
                ctx.fillStyle = PALETTE.shadow;
                for (let g = 2; g < len - 2; g += 4) {
                    ctx.fillRect(sx + 1, sy + g, 2, 1);
                }
            }
        }
    }

    // ---------------------------------------------------------
    //   v21 — Moving vehicles on roads
    //   One car per non-alley road segment, driving between the
    //   two endpoints. Advanced per animTick (8 fps); drawn fresh
    //   each RAF on top of the parked-car layer.
    // ---------------------------------------------------------
    function generateVehicles(layout) {
        if (!layout || !layout.grid) return [];
        const grid = layout.grid;
        const N = grid.n;
        const plotMap = grid.plotMap;
        const BLOCKED = grid.blockedOwner ?? -3;
        const colKinds = grid.colKinds || new Array(N - 1).fill(0);
        const rowKinds = grid.rowKinds || new Array(N - 1).fill(0);
        const carColors = [
            PALETTE.neon.amber, PALETTE.neon.cyan,
            PALETTE.neon.pink,  PALETTE.neon.violet,
        ];
        const out = [];

        function spawnSegment(x0, y0, x1, y1, horizontal, kind, segHash) {
            // v21 — Only spawn on 1 in 3 segments to keep total vehicle
            // count reasonable for a city with hundreds of road segments.
            if (((segHash >>> 1) & 3) !== 0) return;
            const carCount = 1;
            // Inner lane offset — cars drive on the right-hand lane only
            // (inside the centre median), 4 px in from the asphalt edge
            // (so they're past the median but not on the curb).
            const laneOffset = 4;
            for (let i = 0; i < carCount; i++) {
                const dir = (((segHash >>> (5 + i * 3)) & 1) === 0) ? 1 : -1;
                // Lane sides: even index uses south/east lane, odd uses
                // north/west — so cars going opposite directions stay on
                // opposite sides of the median.
                const side = (i % 2 === 0) ? 1 : -1;
                const colorIdx = (segHash >>> (8 + i * 2)) & 3;
                const colour = carColors[colorIdx];
                const speed = 0.4 + ((segHash >>> (12 + i * 2)) & 3) * 0.15;
                const prog0 = (i / carCount) + 0.1 * ((segHash >>> (16 + i * 4)) & 7) / 8;
                out.push({
                    x0, y0, x1, y1,
                    horizontal,
                    laneOffset, side,
                    prog: prog0,
                    speed: speed * dir,
                    colour,
                });
            }
        }

        for (let i = 0; i < N - 1; i++) {
            const colSpacingX = CITY_PAD + (i + 1) * BLOCK_SIZE + i * STREET_W + Math.floor(STREET_W / 2);
            const kind = colKinds[i];
            if (kind === STREET_KIND_ALLEY) continue;       // no asphalt → no vehicles
            for (let row = 0; row < N; row++) {
                if (sameNonEmptyPlot(plotMap[row][i], plotMap[row][i + 1])) continue;
                if (plotMap[row][i] === BLOCKED && plotMap[row][i + 1] === BLOCKED) continue;
                const sy0 = CITY_PAD + row * (BLOCK_SIZE + STREET_W);
                const sy1 = sy0 + BLOCK_SIZE;
                const segHash = ((colSpacingX * 73856093) ^ (sy0 * 19349663)) >>> 0;
                spawnSegment(colSpacingX, sy0, colSpacingX, sy1, false, kind, segHash);
            }
        }
        for (let i = 0; i < N - 1; i++) {
            const rowSpacingY = CITY_PAD + (i + 1) * BLOCK_SIZE + i * STREET_W + Math.floor(STREET_W / 2);
            const kind = rowKinds[i];
            if (kind === STREET_KIND_ALLEY) continue;       // no asphalt → no vehicles
            for (let col = 0; col < N; col++) {
                if (sameNonEmptyPlot(plotMap[i][col], plotMap[i + 1][col])) continue;
                if (plotMap[i][col] === BLOCKED && plotMap[i + 1][col] === BLOCKED) continue;
                const sx0 = CITY_PAD + col * (BLOCK_SIZE + STREET_W);
                const sx1 = sx0 + BLOCK_SIZE;
                const segHash = ((sx0 * 73856093) ^ (rowSpacingY * 19349663)) >>> 0;
                spawnSegment(sx0, rowSpacingY, sx1, rowSpacingY, true, kind, segHash);
            }
        }
        return out;
    }

    function tickVehicles() {
        if (!state.vehicles) return;
        for (const v of state.vehicles) {
            v.prog += v.speed * 0.012;          // 8 fps × 0.012 ≈ 1 % progress per tick
            if (v.prog > 1) v.prog -= 1;
            if (v.prog < 0) v.prog += 1;
        }
    }

    function renderVehicles(ctx, camX, camY, W, H) {
        if (!state.vehicles) return;
        for (const v of state.vehicles) {
            // Linear interpolation between endpoints.
            const len = v.horizontal ? (v.x1 - v.x0) : (v.y1 - v.y0);
            if (Math.abs(len) < 30) continue;
            const t = v.prog;
            let cx, cy;
            if (v.horizontal) {
                cx = v.x0 + t * (v.x1 - v.x0);
                cy = v.y0 + v.side * v.laneOffset;
            } else {
                cx = v.x0 + v.side * v.laneOffset;
                cy = v.y0 + t * (v.y1 - v.y0);
            }
            const sx = Math.round(cx - camX);
            const sy = Math.round(cy - camY);
            if (sx < -10 || sx > W || sy < -10 || sy > H) continue;
            drawMovingVehicle(ctx, sx, sy, v.colour, v.horizontal, v.speed > 0);
        }
    }

    function drawMovingVehicle(ctx, x, y, colour, horizontal, forward) {
        // 8×4 horizontal or 4×8 vertical pixel-art car with directional
        // headlights / taillights so the eye can tell which way it's
        // driving. v23 — adds a 30 %-alpha cyan headlight cone ahead of
        // the car so motion direction reads instantly.
        if (horizontal) {
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(x - 4, y + 1, 8, 1);                       // ground shadow
            ctx.fillStyle = colour;
            ctx.fillRect(x - 4, y - 2, 8, 3);                       // body
            ctx.fillStyle = PALETTE.window.dark;
            ctx.fillRect(x - 2, y - 1, 4, 1);                       // windscreen
            // v23 — headlight cone (30% alpha cyan trapezoid ahead of car).
            ctx.fillStyle = PALETTE.neon.cyan;
            ctx.globalAlpha = 0.30;
            if (forward) {
                ctx.fillRect(x + 4, y - 1, 3, 1);                   // cone east
                ctx.fillRect(x + 5, y - 2, 2, 1);
                ctx.fillRect(x + 5, y,     2, 1);
            } else {
                ctx.fillRect(x - 7, y - 1, 3, 1);                   // cone west
                ctx.fillRect(x - 7, y - 2, 2, 1);
                ctx.fillRect(x - 7, y,     2, 1);
            }
            ctx.globalAlpha = 1.0;
            // Headlights + taillights (bright 1-px dots).
            if (forward) {
                ctx.fillStyle = PALETTE.neon.cyan;
                ctx.fillRect(x + 3, y - 1, 1, 1);
                ctx.fillStyle = PALETTE.neon.pink;
                ctx.fillRect(x - 4, y - 1, 1, 1);
            } else {
                ctx.fillStyle = PALETTE.neon.cyan;
                ctx.fillRect(x - 4, y - 1, 1, 1);
                ctx.fillStyle = PALETTE.neon.pink;
                ctx.fillRect(x + 3, y - 1, 1, 1);
            }
        } else {
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(x + 1, y - 4, 1, 8);
            ctx.fillStyle = colour;
            ctx.fillRect(x - 2, y - 4, 3, 8);
            ctx.fillStyle = PALETTE.window.dark;
            ctx.fillRect(x - 1, y - 2, 1, 4);
            // v23 — headlight cone vertical orientation.
            ctx.fillStyle = PALETTE.neon.cyan;
            ctx.globalAlpha = 0.30;
            if (forward) {
                ctx.fillRect(x - 1, y + 4, 1, 3);                   // cone south
                ctx.fillRect(x - 2, y + 5, 1, 2);
                ctx.fillRect(x,     y + 5, 1, 2);
            } else {
                ctx.fillRect(x - 1, y - 7, 1, 3);                   // cone north
                ctx.fillRect(x - 2, y - 7, 1, 2);
                ctx.fillRect(x,     y - 7, 1, 2);
            }
            ctx.globalAlpha = 1.0;
            if (forward) {
                ctx.fillStyle = PALETTE.neon.cyan;
                ctx.fillRect(x - 1, y + 3, 1, 1);
                ctx.fillStyle = PALETTE.neon.pink;
                ctx.fillRect(x - 1, y - 4, 1, 1);
            } else {
                ctx.fillStyle = PALETTE.neon.cyan;
                ctx.fillRect(x - 1, y - 4, 1, 1);
                ctx.fillStyle = PALETTE.neon.pink;
                ctx.fillRect(x - 1, y + 3, 1, 1);
            }
        }
    }

    // ---------------------------------------------------------
    //   Walking NPC overlay (issue #24)
    //   Each NPC owns a 4-corner rectangular loop and advances
    //   along it every animTick. Sprites are drawn fresh on the
    //   main RAF loop (NOT into the cached ground canvas) so they
    //   actually move. Reduced-motion freezes them in place.
    // ---------------------------------------------------------
    const NPC_HAIR_COLOURS = [
        PALETTE.neon.pink, PALETTE.neon.cyan, PALETTE.neon.violet, PALETTE.neon.amber,
    ];
    const NPC_BODY_COLOURS = ['#2a2444', '#3a2138', '#1a2238', '#2c3a5c'];

    function generateNpcs(layout) {
        // v9d — NPCs now patrol the sidewalk perimeter of their plot (or
        // the plaza), not random void rectangles. Same plot can hold 1-3
        // NPCs sharing one loop with staggered progress so they don't
        // bunch up. Addresses the "pedestrians on the asphalt" reading.
        const out = [];
        if (!layout || !layout.positioned || state.cityWidth <= 0) return out;
        const rng = mulberry32(0xA17C);
        const target = Math.max(20, Math.floor(layout.positioned.length * 2.0));
        let placed = 0;

        function buildLoop(originX, originY, w, h, inset) {
            return [
                { x: originX + inset,         y: originY + inset },
                { x: originX + w - inset,     y: originY + inset },
                { x: originX + w - inset,     y: originY + h - inset },
                { x: originX + inset,         y: originY + h - inset },
            ];
        }

        function pushOnLoop(path, count) {
            for (let i = 0; i < count && placed < target; i++) {
                out.push({
                    path,
                    pi: i % path.length,
                    prog: ((i + 1) / (count + 1)),
                    speed: 0.30 + rng() * 0.30,
                    hair: NPC_HAIR_COLOURS[Math.floor(rng() * NPC_HAIR_COLOURS.length)],
                    body: NPC_BODY_COLOURS[Math.floor(rng() * NPC_BODY_COLOURS.length)],
                    bubble: rng() < 0.10,
                    bubblePhase: Math.floor(rng() * 24),
                });
                placed++;
            }
        }

        for (let i = 0; i < layout.positioned.length && placed < target; i++) {
            const b = layout.positioned[i];
            const plotOrig = blockOrigin(b.blockCol, b.blockRow);
            const span = plotSpan(b.plotCols, b.plotRows);
            if (span.w < 24 || span.h < 24) continue;
            const path = buildLoop(plotOrig.x, plotOrig.y, span.w, span.h, 4);
            // More NPCs on bigger plots — 1 on 1×1, 2 on 1×2/2×1, 3 on 2×2+.
            const area = b.plotCols * b.plotRows;
            const npcCount = Math.min(3, Math.max(1, Math.floor((area + 1) / 2)));
            pushOnLoop(path, npcCount);
        }
        // Six NPCs walking around the plaza's inside perimeter.
        if (layout.plaza && placed < target) {
            const path = buildLoop(layout.plaza.x, layout.plaza.y,
                                   layout.plaza.w, layout.plaza.h, 5);
            pushOnLoop(path, 6);
        }

        // v25 — CROSSWALK NPCs. Add 1-2 pedestrians at each non-alley
        // intersection walking back-and-forth across one of the
        // crosswalks. Path is 2 waypoints (start of crosswalk + end of
        // crosswalk); NPCs ping-pong by reaching the end and looping.
        const grid = layout.grid;
        if (grid && grid.plotMap) {
            const N = grid.n;
            const plotMap = grid.plotMap;
            const BLOCKED = grid.blockedOwner ?? -3;
            const colKinds = grid.colKinds || [];
            const rowKinds = grid.rowKinds || [];
            const STREET_KIND_ALLEY_LOCAL = 2;        // matches constant
            for (let row = 0; row < N - 1; row++) {
                for (let col = 0; col < N - 1; col++) {
                    const xkH = rowKinds[row];
                    const xkV = colKinds[col];
                    if (xkH === STREET_KIND_ALLEY_LOCAL || xkV === STREET_KIND_ALLEY_LOCAL) continue;
                    const nw = plotMap[row][col];
                    const ne = plotMap[row][col + 1];
                    const sw = plotMap[row + 1][col];
                    const se = plotMap[row + 1][col + 1];
                    let blockedCount = 0;
                    if (nw === BLOCKED) blockedCount++;
                    if (ne === BLOCKED) blockedCount++;
                    if (sw === BLOCKED) blockedCount++;
                    if (se === BLOCKED) blockedCount++;
                    if (blockedCount >= 3) continue;
                    const ix = CITY_PAD + (col + 1) * BLOCK_SIZE + col * STREET_W + Math.floor(STREET_W / 2);
                    const iy = CITY_PAD + (row + 1) * BLOCK_SIZE + row * STREET_W + Math.floor(STREET_W / 2);
                    // Two-waypoint crosswalk path. Picks N or W crosswalk
                    // per intersection by deterministic hash.
                    const ph = ((col * 73856093) ^ (row * 19349663)) >>> 0;
                    const useNorth = (ph & 1) === 0;
                    const crosswalkSpan = ROAD_HALF_W - ROAD_SIDEWALK_W - 1;
                    let path;
                    if (useNorth) {
                        // Crosswalk north of the intersection — walks east/west.
                        const cy = iy - (ROAD_HALF_W - 2);
                        path = [
                            { x: ix - crosswalkSpan, y: cy },
                            { x: ix + crosswalkSpan, y: cy },
                        ];
                    } else {
                        // Crosswalk west of the intersection — walks north/south.
                        const cx = ix - (ROAD_HALF_W - 2);
                        path = [
                            { x: cx, y: iy - crosswalkSpan },
                            { x: cx, y: iy + crosswalkSpan },
                        ];
                    }
                    // 1 NPC per crosswalk, progress staggered so multiple
                    // intersections don't appear synchronised.
                    out.push({
                        path,
                        pi: 0,
                        prog: ((ph >>> 4) & 0xFF) / 256,
                        speed: 0.20 + ((ph >>> 12) & 0xFF) / 1024,
                        hair: NPC_HAIR_COLOURS[(ph >>> 8) % NPC_HAIR_COLOURS.length],
                        body: NPC_BODY_COLOURS[(ph >>> 16) % NPC_BODY_COLOURS.length],
                        bubble: false,
                        bubblePhase: 0,
                    });
                    placed++;
                }
            }
        }

        return out;
    }

    function tickNpcs() {
        if (!state.npcs || !state.npcs.length) return;
        for (let i = 0; i < state.npcs.length; i++) {
            const n = state.npcs[i];
            const next = (n.pi + 1) % n.path.length;
            const a = n.path[n.pi];
            const b = n.path[next];
            const dx = b.x - a.x;
            const dy = b.y - a.y;
            const segLen = Math.hypot(dx, dy);
            if (segLen < 0.5) { n.pi = next; n.prog = 0; continue; }
            n.prog += n.speed / segLen;
            if (n.prog >= 1) { n.pi = next; n.prog -= 1; }
        }
    }

    function renderNpcs(ctx, animFrame, camX, camY, W, H) {
        if (!state.npcs || !state.npcs.length) return;
        const reduced = isReducedMotion();
        for (let i = 0; i < state.npcs.length; i++) {
            const n = state.npcs[i];
            const next = (n.pi + 1) % n.path.length;
            const a = n.path[n.pi];
            const b = n.path[next];
            const gx = Math.floor(a.x + (b.x - a.x) * n.prog);
            const gy = Math.floor(a.y + (b.y - a.y) * n.prog);
            const sx = gx - camX;
            const sy = gy - camY;
            // Viewport-cull with margin for the 5×11 sprite + bubble.
            if (sx < -24 || sx > W + 6 || sy < -16 || sy > H + 12) continue;
            drawNpcSprite(ctx, sx, sy, n, animFrame, reduced);
            if (n.bubble && !reduced) {
                // Cycle bubble on for ~16 of every 24 ticks (offset per NPC).
                const phase = (animFrame + n.bubblePhase) % 24;
                if (phase < 16) drawNpcBubble(ctx, sx, sy);
            }
        }
    }

    function drawNpcSprite(ctx, x, y, n, animFrame, reduced) {
        // 5×11 sprite (was 3×6 — Grid v7 scale-up so people aren't tiny
        // relative to the new bigger blocks/streets). Layout from top:
        //   y+0     hair tuft
        //   y+1..2  head 3×2 with a 1-px hair fringe
        //   y+3..4  shoulders + arms (5 wide)
        //   y+5..7  torso (3 wide, centred)
        //   y+8..9  legs (animated)
        //   y+10    drop shadow
        const step = reduced ? 0 : (animFrame & 1);

        // Hair tuft.
        ctx.fillStyle = n.hair;
        ctx.fillRect(x + 2, y, 1, 1);
        // Head — 3×2 (face) with hair fringe on top row + side.
        ctx.fillRect(x + 1, y + 1, 3, 1);
        ctx.fillStyle = '#cba38f';      // skin tone — fixed; not status-relevant
        ctx.fillRect(x + 2, y + 2, 1, 1);
        ctx.fillStyle = n.hair;
        ctx.fillRect(x + 1, y + 2, 1, 1);
        ctx.fillRect(x + 3, y + 2, 1, 1);
        // Shoulders + arms (5-wide row).
        ctx.fillStyle = n.body;
        ctx.fillRect(x, y + 3, 5, 1);
        // Torso (3-wide rows).
        ctx.fillRect(x + 1, y + 4, 3, 1);
        ctx.fillRect(x + 1, y + 5, 3, 1);
        ctx.fillRect(x + 1, y + 6, 3, 1);
        ctx.fillRect(x + 1, y + 7, 3, 1);
        // Legs — alternating stride.
        ctx.fillStyle = PALETTE.wall.shade;
        if (step === 0) {
            ctx.fillRect(x + 1, y + 8, 1, 2);
            ctx.fillRect(x + 3, y + 8, 1, 2);
        } else {
            ctx.fillRect(x + 1, y + 8, 1, 1);
            ctx.fillRect(x + 2, y + 9, 1, 1);
            ctx.fillRect(x + 3, y + 8, 1, 1);
            ctx.fillRect(x, y + 9, 1, 1);
        }
        // Shadow.
        ctx.globalAlpha = 0.5;
        ctx.fillStyle = PALETTE.shadow;
        ctx.fillRect(x, y + 10, 5, 1);
        ctx.globalAlpha = 1.0;
    }

    function drawNpcBubble(ctx, x, y) {
        // Speech bubble drawn above the NPC. Uses the bitmap font from #23
        // and the reserved decorative cyan — never status.*.
        const tw = textWidth('TALK?');
        const bw = tw + 4;
        const bh = 9;
        const bx = x - Math.floor(bw / 2) + 1;
        const by = y - bh - 3;
        // Backboard.
        ctx.fillStyle = PALETTE.wall.shade;
        ctx.fillRect(bx, by, bw, bh);
        // Cyan outline.
        ctx.fillStyle = PALETTE.neon.cyan;
        ctx.fillRect(bx, by, bw, 1);
        ctx.fillRect(bx, by + bh - 1, bw, 1);
        ctx.fillRect(bx, by, 1, bh);
        ctx.fillRect(bx + bw - 1, by, 1, bh);
        // Tail pointing down at the NPC head.
        const tailX = bx + Math.floor(bw / 2);
        ctx.fillRect(tailX, by + bh, 1, 1);
        ctx.fillRect(tailX - 1, by + bh + 1, 3, 1);
        // Text.
        drawText(ctx, 'TALK?', bx + 2, by + 1, PALETTE.neon.cyan);
    }

    // ---------------------------------------------------------
    //   Ambient particle systems (issue #25)
    //   Steam vents puff above street tiles, dust motes drift up
    //   through the air, lamppost lens flares pulse at ~0.5 Hz.
    //   All three suppressed under prefers-reduced-motion.
    // ---------------------------------------------------------
    function generateParticles(layout) {
        const out = { steamVents: [], motes: [] };
        if (!layout || !layout.positioned || state.cityWidth <= 0) return out;
        const rng = mulberry32(0xD08C);

        // Steam vents — sparse scatter in void areas (NOT on building roofs).
        // v12 — also skip blocked wasteland cells so no steam puff hovers
        // in the void surrounding the colony.
        const ventTarget = Math.max(6, Math.floor((state.cityWidth * state.cityHeight) / 9000));
        const grid = layout.grid;
        const BLOCKED = grid ? (grid.blockedOwner ?? -3) : -3;
        const inWasteland = (x, y) => {
            if (!grid || !grid.plotMap) return false;
            const stride = grid.blockSize + grid.streetW;
            const col = Math.floor((x - grid.pad) / stride);
            const row = Math.floor((y - grid.pad) / stride);
            if (row < 0 || row >= grid.n || col < 0 || col >= grid.n) return true;
            return grid.plotMap[row][col] === BLOCKED;
        };
        let attempts = 0;
        while (out.steamVents.length < ventTarget && attempts < ventTarget * 16) {
            attempts++;
            const x = Math.floor(rng() * (state.cityWidth - 6)) + 3;
            const y = Math.floor(rng() * (state.cityHeight - 6)) + 3;
            if (pointInsideAnyBuilding(layout.positioned, x, y, 2)) continue;
            if (layout.plaza && pointInsidePlaza(layout.plaza, x, y, -6)) continue;
            if (inWasteland(x, y)) continue;
            out.steamVents.push({ x, y, phase: Math.floor(rng() * 8) });
        }

        // Dust motes — spread over the whole city. Animate by ticking
        // their y upward; on wrap, jitter x slightly so they don't
        // form vertical columns.
        const moteCount = 75;
        for (let i = 0; i < moteCount; i++) {
            out.motes.push({
                x: Math.floor(rng() * Math.max(1, state.cityWidth)),
                y: Math.floor(rng() * Math.max(1, state.cityHeight)),
                seed: Math.floor(rng() * 1000),
            });
        }

        return out;
    }

    function tickParticles() {
        if (!state.particles) return;
        const motes = state.particles.motes;
        const W = state.cityWidth;
        const H = state.cityHeight;
        for (let i = 0; i < motes.length; i++) {
            motes[i].y -= 1;
            if (motes[i].y < 0) {
                motes[i].y = H - 1;
                motes[i].x = (motes[i].x + 7 + (motes[i].seed & 7)) % W;
            }
        }
    }

    function renderParticles(ctx, animFrame, camX, camY, W, H) {
        if (!state.particles) return;
        const reduced = isReducedMotion();

        // Steam vents — 8-tick cycle, visible on frames 0–2 (the 3-frame puff).
        const vents = state.particles.steamVents;
        if (vents) {
            for (let i = 0; i < vents.length; i++) {
                const v = vents[i];
                const sx = v.x - camX;
                const sy = v.y - camY;
                if (sx < -6 || sx > W + 6 || sy < -8 || sy > H + 4) continue;
                if (reduced) {
                    drawSteamPuff(ctx, sx, sy, 0);
                    continue;
                }
                const frame = (animFrame + v.phase) % 8;
                if (frame >= 3) continue;
                drawSteamPuff(ctx, sx, sy, frame);
            }
        }

        // Lens flares on lampposts — pulse 4 of every 16 ticks (~0.5 Hz at 8 fps).
        if (state.lampposts && !reduced) {
            const bright = (animFrame % 16) < 4;
            if (bright) {
                for (let i = 0; i < state.lampposts.length; i++) {
                    const lp = state.lampposts[i];
                    const sx = lp.x - camX + 1;
                    const sy = lp.y - camY - 1;
                    if (sx < -6 || sx > W + 6 || sy < -6 || sy > H + 6) continue;
                    drawLensFlare(ctx, sx, sy);
                }
            }
        }

        // Dust motes — drifting cyan/grey 1-px dots. Rendered even under
        // reduced-motion (as static dots) so the air doesn't read as empty.
        const motes = state.particles.motes;
        for (let i = 0; i < motes.length; i++) {
            const m = motes[i];
            const sx = m.x - camX;
            const sy = m.y - camY;
            if (sx < 0 || sx >= W || sy < 0 || sy >= H) continue;
            const alpha = 0.20 + ((m.seed % 5) / 25);
            ctx.globalAlpha = alpha;
            ctx.fillStyle = (m.seed % 3 === 0) ? PALETTE.neon.cyan : '#d9d4e6';
            ctx.fillRect(sx, sy, 1, 1);
        }
        ctx.globalAlpha = 1.0;
    }

    function drawSteamPuff(ctx, x, y, frame) {
        // 3-frame upward-rising puff. Cool grey alpha-blended.
        ctx.fillStyle = '#d9d4e6';
        if (frame === 0) {
            ctx.globalAlpha = 0.55;
            ctx.fillRect(x, y, 2, 1);
            ctx.fillRect(x + 1, y - 1, 1, 1);
        } else if (frame === 1) {
            ctx.globalAlpha = 0.45;
            ctx.fillRect(x - 1, y - 1, 4, 1);
            ctx.fillRect(x, y - 2, 2, 1);
            ctx.fillRect(x + 1, y - 3, 1, 1);
        } else {
            ctx.globalAlpha = 0.30;
            ctx.fillRect(x - 1, y - 3, 1, 1);
            ctx.fillRect(x + 2, y - 3, 1, 1);
            ctx.fillRect(x, y - 4, 2, 1);
            ctx.fillRect(x + 1, y - 5, 1, 1);
        }
        ctx.globalAlpha = 1.0;
    }

    function drawLensFlare(ctx, x, y) {
        // Soft pink halo + faint cross of light over the lamp head.
        ctx.fillStyle = PALETTE.neon.pink;
        ctx.globalAlpha = 0.42;
        ctx.fillRect(x - 1, y - 1, 5, 1);
        ctx.fillRect(x - 1, y + 1, 5, 1);
        ctx.fillRect(x - 2, y, 1, 1);
        ctx.fillRect(x + 4, y, 1, 1);
        ctx.globalAlpha = 0.22;
        ctx.fillRect(x - 2, y - 2, 7, 1);
        ctx.fillRect(x - 2, y + 2, 7, 1);
        ctx.fillRect(x - 3, y - 1, 1, 3);
        ctx.fillRect(x + 5, y - 1, 1, 3);
        ctx.globalAlpha = 1.0;
    }

    function pointInsideAnyBuilding(buildings, x, y, margin) {
        for (const b of buildings) {
            if (x >= b.x - margin && x < b.x + b.w + margin
                && y >= b.y - margin && y < b.y + b.h + margin) {
                return true;
            }
        }
        return false;
    }

    function pointInsidePlaza(plaza, x, y, margin) {
        return x >= plaza.x - margin && x < plaza.x + plaza.w + margin
            && y >= plaza.y - margin && y < plaza.y + plaza.h + margin;
    }

    // ---------------------------------------------------------
    //   Tier-4 holographic projections (issue #26)
    //   Every tier-4 landmark gets a small 8×12 cyan glyph
    //   floating 18 px above its roof. ±2-px sine bob keyed
    //   off the anim-tick. Triple-layer glow: outer 3×3 stamp
    //   at α 0.18, middle pass at α 0.35, crisp core at α 1.0.
    //   When the underlying check is in err/fail, the glyph
    //   picks up the status tint and strobes via
    //   signBrightnessForAnim. Reduced-motion freezes the float
    //   and skips the strobe.
    // ---------------------------------------------------------
    const HOLOGRAM_GLYPHS = [
        // YEN ¥
        [
            '##....##','.#....#.','..#..#..','...##...',
            '########','########','...##...','########',
            '########','...##...','...##...','...##...',
        ],
        // HEART
        [
            '........','.##..##.','########','########',
            '########','########','.######.','.######.',
            '..####..','..####..','...##...','........',
        ],
        // SKULL
        [
            '.######.','########','##.##.##','########',
            '########','.######.','.#.##.#.','.######.',
            '.######.','..####..','.##..##.','##....##',
        ],
        // CODE BRACKETS </>
        [
            '..#..#..','.#....#.','#......#','##....##',
            '###..###','.##.##..','..##.##.','###..###',
            '##....##','#......#','.#....#.','..#..#..',
        ],
        // FISH
        [
            '........','...##...','..####..','.######.',
            '#######.','########','########','#######.',
            '.######.','..####..','...##...','........',
        ],
        // RAMEN BOWL
        [
            '.######.','##....##','#......#','#......#',
            '#......#','#......#','##....##','########',
            '########','########','...##...','...##...',
        ],
        // INFINITY ∞
        [
            '........','........','........','........',
            '##.##.##','###.####','###.####','###.####',
            '##.##.##','........','........','........',
        ],
        // v20 — SATURN (planet with ring)
        [
            '........','........','...##...','..####..',
            '.######.','#######.','########','########',
            '#######.','#######.','.######.','..####..',
        ],
        // v20 — ATOM (nucleus + electron orbits)
        [
            '........','...##...','..####..','.######.',
            '##.##.##','##.##.##','########','##.##.##',
            '##.##.##','.######.','..####..','...##...',
        ],
        // v20 — CROSS-PLUS
        [
            '........','...##...','...##...','...##...',
            '########','########','########','########',
            '...##...','...##...','...##...','........',
        ],
        // v20 — TARGET / CONCENTRIC RINGS
        [
            '........','..####..','.######.','##....##',
            '#.####.#','#.#..#.#','#.#..#.#','#.####.#',
            '##....##','.######.','..####..','........',
        ],
    ];

    function renderHolograms(ctx, animFrame, camX, camY, W, H) {
        const buildings = state.buildings;
        if (!buildings || !buildings.length) return;
        const reduced = isReducedMotion();
        const float = reduced ? 0 : Math.round(Math.sin(animFrame * 0.40) * 3);
        // v20 — per-building accent palette so saturn/atom holograms come
        // in pink / cyan / amber / violet / magenta as in the reference
        // (not all cyan).
        const accent8 = [
            PALETTE.neon.cyan,    PALETTE.neon.pink,    PALETTE.neon.amber,
            PALETTE.neon.violet,  PALETTE.neon.magenta, PALETTE.neon.cyan,
            PALETTE.neon.pink,    PALETTE.neon.amber,
        ];

        for (let i = 0; i < buildings.length; i++) {
            const b = buildings[i];
            // v20 — extended from "tier 4 only" to tier ≥ 3 megacorp-tower
            // and tier ≥ 3 office-spire. The reference shows holograms
            // above many large buildings, not just the absolute biggest.
            const eligible = (b.tier === 4)
                          || (b.tier >= 3 && (b.archetype === 'megacorp-tower'
                                           || b.archetype === 'office-spire'));
            if (!eligible) continue;

            const glyphIdx = (b.seed >>> 16) % HOLOGRAM_GLYPHS.length;
            const glyph = HOLOGRAM_GLYPHS[glyphIdx];
            const glyphW = glyph[0].length;
            const glyphH = glyph.length;
            // v20 — render at 2× pixel-art scale so holograms read clearly
            // at the new resolution. Each glyph pixel becomes a 2×2 block.
            const SCALE = 2;
            const drawW = glyphW * SCALE;
            const drawH = glyphH * SCALE;
            const baseX = (b.x - camX) + Math.floor((b.w - drawW) / 2);
            const baseY = (b.y - camY) - drawH - 6 + float;

            if (baseX + drawW + 4 < 0 || baseX - 4 > W) continue;
            if (baseY + drawH + 4 < 0 || baseY - 4 > H) continue;

            const failType = b.currentFailType;
            const isFailing = (failType === 0 || failType === 3);
            const accent = isFailing ? statusGlow(failType) : accent8[(b.seed >>> 13) & 7];
            const bri = (isFailing && !reduced)
                ? signBrightnessForAnim(failType, animFrame)
                : (0.75 + 0.20 * Math.abs(Math.sin(animFrame * 0.15)));

            drawHologramScaled(ctx, baseX, baseY, glyph, accent, bri, SCALE);
        }
    }

    function drawHologramScaled(ctx, x, y, glyph, color, brightness, scale) {
        if (brightness < 0.05) return;
        const rows = glyph.length;
        const cols = glyph[0].length;
        ctx.fillStyle = color;
        ctx.globalAlpha = brightness;
        for (let r = 0; r < rows; r++) {
            const line = glyph[r];
            for (let c = 0; c < cols; c++) {
                if (line[c] === '#') {
                    ctx.fillRect(x + c * scale, y + r * scale, scale, scale);
                }
            }
        }
        // Soft halo — 1-extra-px outline at half alpha.
        ctx.globalAlpha = brightness * 0.30;
        for (let r = 0; r < rows; r++) {
            const line = glyph[r];
            for (let c = 0; c < cols; c++) {
                if (line[c] !== '#') continue;
                // Check 4-neighbours: if a neighbour is empty, paint a
                // halo pixel there.
                if (c > 0 && line[c - 1] !== '#') {
                    ctx.fillRect(x + (c - 1) * scale, y + r * scale, scale, scale);
                }
                if (c < cols - 1 && line[c + 1] !== '#') {
                    ctx.fillRect(x + (c + 1) * scale, y + r * scale, scale, scale);
                }
                if (r > 0 && glyph[r - 1][c] !== '#') {
                    ctx.fillRect(x + c * scale, y + (r - 1) * scale, scale, scale);
                }
                if (r < rows - 1 && glyph[r + 1][c] !== '#') {
                    ctx.fillRect(x + c * scale, y + (r + 1) * scale, scale, scale);
                }
            }
        }
        ctx.globalAlpha = 1.0;
    }

    function drawHologram(ctx, x, y, glyph, color, brightness) {
        if (brightness < 0.05) return;
        const rows = glyph.length;
        const cols = glyph[0].length;

        ctx.fillStyle = color;

        // Pass 1 — outer halo: 3×3 stamp around each lit pixel.
        ctx.globalAlpha = 0.18 * brightness;
        for (let row = 0; row < rows; row++) {
            const r = glyph[row];
            for (let col = 0; col < cols; col++) {
                if (r.charCodeAt(col) === 35) {
                    ctx.fillRect(x + col - 1, y + row - 1, 3, 3);
                }
            }
        }

        // Pass 2 — middle band: same pixels stronger.
        ctx.globalAlpha = 0.35 * brightness;
        for (let row = 0; row < rows; row++) {
            const r = glyph[row];
            for (let col = 0; col < cols; col++) {
                if (r.charCodeAt(col) === 35) {
                    ctx.fillRect(x + col, y + row, 1, 1);
                }
            }
        }

        // Pass 3 — crisp core.
        ctx.globalAlpha = brightness;
        for (let row = 0; row < rows; row++) {
            const r = glyph[row];
            for (let col = 0; col < cols; col++) {
                if (r.charCodeAt(col) === 35) {
                    ctx.fillRect(x + col, y + row, 1, 1);
                }
            }
        }

        ctx.globalAlpha = 1.0;
    }

    // ---------------------------------------------------------
    //   Construction-site frame for new checks (issue #27)
    //   Every building with ageDays < 7 wears a top-down
    //   construction overlay that disassembles over the first
    //   week. Tarp + corner scaffold poles + welder sparks +
    //   "NEW" banner. Determinism via mulberry32(seed ^ 0xDEAD).
    //   Reduced-motion stops the spark animation but keeps the
    //   static elements so brand-new checks still read as such.
    // ---------------------------------------------------------
    function renderConstructionFrames(ctx, animFrame, camX, camY, W, H) {
        const buildings = state.buildings;
        if (!buildings || !buildings.length) return;
        const reduced = isReducedMotion();

        for (let i = 0; i < buildings.length; i++) {
            const b = buildings[i];
            if (typeof b.ageDays !== 'number' || b.ageDays >= 7) continue;

            const screenX = b.x - camX;
            const screenY = b.y - camY;
            // Viewport cull (banner + sparks add a few px of margin).
            if (screenX + b.w + 6 < 0 || screenX - 6 > W) continue;
            if (screenY + b.h + 6 < 0 || screenY - 14 > H) continue;

            drawConstructionFrame(ctx, b, screenX, screenY, animFrame, reduced);
        }
    }

    function drawConstructionFrame(ctx, b, x, y, animFrame, reduced) {
        const age = Math.max(0, b.ageDays);

        // 1. Tarp — semi-transparent dark cover over the roof. Fades to 0 at day 7.
        const tarpAlpha = Math.max(0, (7 - age) / 7) * 0.45;
        if (tarpAlpha > 0.01) {
            ctx.globalAlpha = tarpAlpha;
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(x, y, b.w, b.h);
            ctx.globalAlpha = 1.0;
        }

        // 2. Four scaffold poles at the roof corners. Each disassembles on a
        //    specific day so the overlay visibly shrinks day by day.
        ctx.fillStyle = PALETTE.wall.hi;
        const POLE_H = 6;
        if (age < 2) ctx.fillRect(x,             y,             1, POLE_H); // NW
        if (age < 3) ctx.fillRect(x + b.w - 1,   y,             1, POLE_H); // NE
        if (age < 4) ctx.fillRect(x + b.w - 1,   y + b.h - POLE_H, 1, POLE_H); // SE
        if (age < 5) ctx.fillRect(x,             y + b.h - POLE_H, 1, POLE_H); // SW

        // 3. Welder sparks along the south edge — only on early days, only on
        //    "on" frames, only when motion is allowed. Sparks use the
        //    decorative neon.amber + pale yellow #ffffaa so a healthy
        //    brand-new check is NOT mis-read as warning (Hermes review #163).
        if (!reduced && age < 4) {
            const sparkOn = ((animFrame + (b.seed | 0)) & 7) < 2;
            if (sparkOn) {
                const rng = mulberry32((b.seed ^ 0xDEAD) >>> 0);
                const sparkCount = 2 + Math.floor(rng() * 2);    // 2 or 3
                for (let s = 0; s < sparkCount; s++) {
                    const sx = x + 2 + Math.floor(rng() * Math.max(1, b.w - 4));
                    const sy = y + b.h - 1 - Math.floor(rng() * 3);
                    ctx.fillStyle = (s & 1) ? PALETTE.neon.amber : '#ffffaa';
                    ctx.fillRect(sx, sy, 1, 1);
                }
            }
        }

        // 4. "NEW" banner above the roof. Fades out by day 5.
        const bannerAlpha = Math.max(0, (5 - age) / 5);
        if (bannerAlpha > 0.05) {
            const labelW = textWidth('NEW');
            const bw = labelW + 4;
            const bh = 9;
            const bx = x + Math.floor((b.w - bw) / 2);
            const by = y - bh - 4;
            ctx.globalAlpha = bannerAlpha;
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(bx, by, bw, bh);
            ctx.fillStyle = PALETTE.neon.amber;
            ctx.fillRect(bx, by, bw, 1);
            ctx.fillRect(bx, by + bh - 1, bw, 1);
            ctx.fillRect(bx, by, 1, bh);
            ctx.fillRect(bx + bw - 1, by, 1, bh);
            drawText(ctx, 'NEW', bx + 2, by + 1, PALETTE.neon.amber);
            ctx.globalAlpha = 1.0;
        }
    }

    // Top-down 6×4 vehicle on the street.
    function drawVehicle(ctx, x, y, horizontal, rng) {
        // Same rule as NPC hair — use decorative amber, never status.warn.
        const colours = [PALETTE.neon.cyan, PALETTE.neon.amber, PALETTE.neon.pink, '#aac', '#5e3a6a'];
        const c = colours[Math.floor(rng() * colours.length)];
        if (horizontal) {
            // shadow
            ctx.fillStyle = PALETTE.shadow;
            ctx.globalAlpha = 0.55;
            ctx.fillRect(x + 1, y + 4, 7, 1);
            ctx.globalAlpha = 1.0;
            // body
            ctx.fillStyle = '#1a1432';
            ctx.fillRect(x, y, 7, 4);
            // hood / roof colour band
            ctx.fillStyle = c;
            ctx.fillRect(x + 1, y + 1, 5, 2);
            // windshield strip
            ctx.fillStyle = PALETTE.window.lit;
            ctx.fillRect(x + 2, y + 1, 3, 1);
            // wheels
            ctx.fillStyle = PALETTE.window.frame;
            ctx.fillRect(x + 1, y, 1, 1);
            ctx.fillRect(x + 5, y, 1, 1);
            ctx.fillRect(x + 1, y + 3, 1, 1);
            ctx.fillRect(x + 5, y + 3, 1, 1);
        } else {
            // vertical orientation — body 4 wide × 7 tall
            ctx.fillStyle = PALETTE.shadow;
            ctx.globalAlpha = 0.55;
            ctx.fillRect(x + 4, y + 1, 1, 7);
            ctx.globalAlpha = 1.0;
            ctx.fillStyle = '#1a1432';
            ctx.fillRect(x, y, 4, 7);
            ctx.fillStyle = c;
            ctx.fillRect(x + 1, y + 1, 2, 5);
            ctx.fillStyle = PALETTE.window.lit;
            ctx.fillRect(x + 1, y + 2, 2, 1);
            ctx.fillStyle = PALETTE.window.frame;
            ctx.fillRect(x, y + 1, 1, 1);
            ctx.fillRect(x + 3, y + 1, 1, 1);
            ctx.fillRect(x, y + 5, 1, 1);
            ctx.fillRect(x + 3, y + 5, 1, 1);
        }
    }

    function renderVehiclesOrganic(ctx, layout) {
        // Scatter parked vehicles in void areas. Count scales with city size
        // but stays sparse so they read as "parked" not "parking lot".
        const rng = mulberry32(0xCAF1);
        const target = Math.max(6, Math.floor(layout.positioned.length * 0.6));
        let placed = 0;
        let attempts = 0;
        while (placed < target && attempts < target * 8) {
            attempts++;
            const horizontal = rng() < 0.5;
            const vw = horizontal ? 7 : 4;
            const vh = horizontal ? 4 : 7;
            const vx = Math.floor(rng() * (state.cityWidth - vw - 4)) + 2;
            const vy = Math.floor(rng() * (state.cityHeight - vh - 4)) + 2;
            // Vehicle must clear every building footprint by 4 px.
            if (rectOverlapsAnyBuilding(layout.positioned, vx, vy, vw, vh, 4)) continue;
            if (layout.plaza && rectOverlapsPlaza(layout.plaza, vx, vy, vw, vh, -2)) continue;
            drawVehicle(ctx, vx, vy, horizontal, rng);
            placed++;
        }
    }

    function rectOverlapsAnyBuilding(buildings, x, y, w, h, margin) {
        for (const b of buildings) {
            if (x + w + margin > b.x && b.x + b.w + margin > x
                && y + h + margin > b.y && b.y + b.h + margin > y) {
                return true;
            }
        }
        return false;
    }

    function rectOverlapsPlaza(plaza, x, y, w, h, margin) {
        return x + w + margin > plaza.x && plaza.x + plaza.w + margin > x
            && y + h + margin > plaza.y && plaza.y + plaza.h + margin > y;
    }

    function renderPlazaInto(ctx, plaza) {
        const { x, y, w, h } = plaza;

        // 1. Plaza floor.
        ctx.fillStyle = PALETTE.plaza.floor;
        ctx.fillRect(x, y, w, h);

        // 2. Floor tile grid — 16-px subdivisions with darker outlines.
        ctx.fillStyle = PALETTE.plaza.tileLine;
        for (let xx = 0; xx <= w; xx += 16) {
            ctx.fillRect(x + xx, y, 1, h);
        }
        for (let yy = 0; yy <= h; yy += 16) {
            ctx.fillRect(x, y + yy, w, 1);
        }

        // 3. Four planters in the corners (8×8 px each).
        const planterColor = '#3a5f6a';
        const planterLeaf = PALETTE.neon.cyan;
        const planterPositions = [
            { px: x + 6,          py: y + 6 },
            { px: x + w - 14,     py: y + 6 },
            { px: x + 6,          py: y + h - 14 },
            { px: x + w - 14,     py: y + h - 14 },
        ];
        for (const p of planterPositions) {
            ctx.fillStyle = planterColor;
            ctx.fillRect(p.px, p.py, 8, 8);
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(p.px, p.py + 7, 8, 1);
            // Leaves — 2-px clusters in cyan.
            ctx.fillStyle = planterLeaf;
            ctx.fillRect(p.px + 1, p.py + 1, 2, 2);
            ctx.fillRect(p.px + 5, p.py + 2, 2, 2);
            ctx.fillRect(p.px + 3, p.py + 4, 2, 2);
        }

        // 4. Bench rows (2 horizontal benches at top + bottom).
        ctx.fillStyle = '#3a3050';
        ctx.fillRect(x + 24, y + 18, 24, 3);
        ctx.fillRect(x + w - 48, y + 18, 24, 3);
        ctx.fillRect(x + 24, y + h - 21, 24, 3);
        ctx.fillRect(x + w - 48, y + h - 21, 24, 3);
        ctx.fillStyle = PALETTE.wall.shade;
        ctx.fillRect(x + 24, y + 21, 24, 1);
        ctx.fillRect(x + w - 48, y + 21, 24, 1);

        // 5. Magenta ring border — 1-px outer + softer glow.
        ctx.fillStyle = PALETTE.plaza.ring;
        ctx.fillRect(x, y, w, 1);
        ctx.fillRect(x, y + h - 1, w, 1);
        ctx.fillRect(x, y, 1, h);
        ctx.fillRect(x + w - 1, y, 1, h);
        ctx.globalAlpha = 0.5;
        ctx.fillRect(x + 1, y + 1, w - 2, 1);
        ctx.fillRect(x + 1, y + h - 2, w - 2, 1);
        ctx.fillRect(x + 1, y + 1, 1, h - 2);
        ctx.fillRect(x + w - 2, y + 1, 1, h - 2);
        ctx.globalAlpha = 1.0;

        // 6. Central hologram pillar — cyan obelisk with glow halo.
        const cx = x + Math.floor(w / 2);
        const cy = y + Math.floor(h / 2);
        // Base pedestal.
        ctx.fillStyle = '#3a3050';
        ctx.fillRect(cx - 6, cy + 4, 12, 4);
        ctx.fillStyle = PALETTE.plaza.tileLine;
        ctx.fillRect(cx - 6, cy + 4, 12, 1);
        // Obelisk core.
        ctx.fillStyle = PALETTE.neon.cyan;
        ctx.fillRect(cx - 1, cy - 10, 2, 14);
        ctx.fillRect(cx - 2, cy - 8, 4, 2);
        ctx.fillRect(cx - 2, cy, 4, 2);
        // Glow halo (concentric).
        ctx.globalAlpha = 0.55;
        ctx.fillRect(cx - 3, cy - 6, 6, 6);
        ctx.globalAlpha = 0.30;
        ctx.fillRect(cx - 5, cy - 4, 10, 4);
        ctx.globalAlpha = 0.15;
        ctx.fillRect(cx - 8, cy - 2, 16, 2);
        ctx.globalAlpha = 1.0;
    }

    // =========================================================
    //               BUILDING — TOP-DOWN RENDERER
    //  Drawn from primitives in the style of the reference pack.
    //  Layers (back to front):
    //    1. Ground shadow (2-px SE offset, darkens beneath)
    //    2. South-facing wall band (4-px tall strip at bottom)
    //    3. Roof main fill
    //    4. Roof corner highlights / shadows for 3/4 perspective
    //    5. Roof texture (AC units, antennas, hologram pads)
    //    6. Sign (small neon billboard at the south edge)
    //    7. Window dots on the wall band
    //    8. Status halo — neon outline glow around the roof
    // =========================================================

    // v22 — per-archetype wall texture.  Picks one of 5 materials by
    // archetype so the building's wall band reads as glass / panel /
    // brick / stucco / concrete from above. Subtle but multiplies the
    // visual variety between adjacent same-tone buildings.
    function applyArchetypeWallTexture(ctx, b, wallX, wallY, WALL_H, rng) {
        const arch = b.archetype;
        if (arch === 'megacorp-tower') {
            // GLASS — no horizontal seams, very fine speckle in window.dark
            // (the gaps between panes), bright top-edge highlight already
            // drawn so we just add subtle pane suggestions.
            ctx.fillStyle = PALETTE.window.dark;
            ctx.globalAlpha = 0.45;
            for (let xx = wallX + 1; xx < wallX + b.w - 1; xx += 3) {
                ctx.fillRect(xx, wallY + 1, 1, WALL_H - 2);
            }
            ctx.globalAlpha = 1.0;
        } else if (arch === 'office-spire') {
            // PANEL — vertical 1-px lines every 8 px (panel-wall joints).
            ctx.fillStyle = PALETTE.shadow;
            ctx.globalAlpha = 0.40;
            for (let xx = wallX + 4; xx < wallX + b.w - 1; xx += 8) {
                ctx.fillRect(xx, wallY + 1, 1, WALL_H - 2);
            }
            ctx.globalAlpha = 1.0;
        } else if (arch === 'tenement') {
            // BRICK — dense horizontal seams every 4 px + per-pixel grime.
            ctx.fillStyle = PALETTE.shadow;
            ctx.globalAlpha = 0.45;
            for (let sy = wallY + 2; sy < wallY + WALL_H - 1; sy += 4) {
                ctx.fillRect(wallX + 1, sy, b.w - 2, 1);
            }
            ctx.globalAlpha = 1.0;
            // Mortar speckle.
            ctx.fillStyle = PALETTE.wall.mid;
            for (let sy = wallY + 1; sy < wallY + WALL_H - 1; sy += 2) {
                for (let xx = wallX + 1; xx < wallX + b.w - 1; xx += 2) {
                    if (rng() < 0.20) ctx.fillRect(xx, sy, 1, 1);
                }
            }
        } else if (arch === 'hostel') {
            // STUCCO — sparse horizontal seam every 8 px + heavy speckle.
            ctx.fillStyle = PALETTE.shadow;
            ctx.globalAlpha = 0.30;
            for (let sy = wallY + 5; sy < wallY + WALL_H - 2; sy += 8) {
                ctx.fillRect(wallX + 1, sy, b.w - 2, 1);
            }
            ctx.globalAlpha = 1.0;
            ctx.fillStyle = PALETTE.wall.mid;
            for (let sy = wallY + 2; sy < wallY + WALL_H - 1; sy += 2) {
                for (let xx = wallX + 1; xx < wallX + b.w - 1; xx += 2) {
                    if (rng() < 0.25) ctx.fillRect(xx, sy, 1, 1);
                }
            }
        } else {
            // CONCRETE (ramen / arcade / karaoke / noodle) — default v18
            // brick pattern: 6-px seam stride + medium speckle.
            ctx.fillStyle = PALETTE.shadow;
            ctx.globalAlpha = 0.40;
            for (let sy = wallY + 3; sy < wallY + WALL_H - 2; sy += 6) {
                ctx.fillRect(wallX + 1, sy, b.w - 2, 1);
            }
            ctx.globalAlpha = 1.0;
            ctx.fillStyle = PALETTE.wall.mid;
            for (let sy = wallY + 2; sy < wallY + WALL_H - 1; sy += 3) {
                for (let xx = wallX + 1; xx < wallX + b.w - 1; xx += 2) {
                    if (rng() < 0.18) ctx.fillRect(xx, sy, 1, 1);
                }
            }
        }
    }

    // v23 — Draw a 4×4 window with a 1-px dark frame around it. When lit,
    // a 30 %-alpha glow halo extends 1 px around the lit core in the
    // window's lit colour so the window reads as a real light source,
    // not just a coloured rectangle.
    function drawLitWindow(ctx, xx, yy, isLit, litColor) {
        // Dark frame.
        ctx.fillStyle = PALETTE.window.frame;
        ctx.fillRect(xx - 1, yy - 1, 6, 6);
        // Lit / dark core.
        ctx.fillStyle = isLit ? litColor : PALETTE.window.dark;
        ctx.fillRect(xx, yy, 4, 4);
        // Glow halo for lit windows only.
        if (isLit) {
            ctx.fillStyle = litColor;
            ctx.globalAlpha = 0.30;
            ctx.fillRect(xx - 1, yy,     1, 4);              // west
            ctx.fillRect(xx + 4, yy,     1, 4);              // east
            ctx.fillRect(xx,     yy - 1, 4, 1);              // north
            ctx.fillRect(xx,     yy + 4, 4, 1);              // south
            ctx.globalAlpha = 1.0;
        }
    }

    function renderBuilding(b, animFrame) {
        animFrame = isReducedMotion() ? 0 : (animFrame | 0);
        const t0 = performance.now();

        // Per-building wall height (Grid v7 — visual diversity, was a fixed
        // 7 px). Stored on the building during layoutCity; fall back to a
        // moderate default if a caller hasn't populated it yet.
        const wallH = (typeof b.wallH === 'number') ? b.wallH : 10;

        // Pad the canvas for shadow + wall band + halo overflow. v11 —
        // bumped to 12 px to accommodate the south-extruded hanging sign
        // (drawn 1 px below the wall band, 9 px tall) and the roof-billboard
        // (drawn 10 px above the roof when bits 3-5 select that placement).
        const PAD = 12;
        const W = b.w + PAD * 2;
        const H = b.h + PAD + wallH + PAD;
        const c = createCanvas(W, H);
        const ctx = c.getContext('2d');
        ctx.imageSmoothingEnabled = false;
        const rng = mulberry32(b.seed);

        // Issue #37 — per-instance variation. A separate rng drives the
        // visual differences between two same-archetype buildings; the
        // primary `rng` keeps driving everything else exactly as before
        // so window-lit patterns and AC layout stay deterministic.
        const vRng = mulberry32((b.seed ^ 0xC1A0) >>> 0);
        const roofBase = jitterColor(PALETTE.roof[archetypeKey(b.archetype, 'base')], vRng);
        const roofHi   = jitterColor(PALETTE.roof[archetypeKey(b.archetype, 'Hi')],   vRng);
        const roofLo   = jitterColor(PALETTE.roof[archetypeKey(b.archetype, 'Lo')],   vRng);
        const windowPattern = (b.seed >>> 24) & 3;            // 0..3
        const wallVariant   = (b.seed >>> 20) & 3;            // 0..3 — paint/grime/decal toggle
        // v9b — silhouette + ground-floor variants. Adds visual variety
        // beyond colour/window jitter so two same-archetype buildings
        // can have genuinely different shapes.
        const silhouetteVariant = (b.seed >>> 14) & 7;        // 0..7
        const groundFloorVariant = (b.seed >>> 9) & 3;        // 0..3
        const WALL_H = wallH;

        // 1. Ground shadow under the building — south-east drop, soft.
        ctx.fillStyle = PALETTE.shadow;
        ctx.globalAlpha = 0.50;
        ctx.fillRect(PAD + 3, PAD + 5, b.w, b.h + WALL_H);
        ctx.globalAlpha = 1.0;

        // 2. South-facing wall band.  v18 — proper brick/panel texture
        //    with horizontal seams every 6 px + per-pixel speckle.  Looks
        //    like a real concrete/brick facade instead of flat fill.
        const wallX = PAD;
        const wallY = PAD + b.h;
        ctx.fillStyle = PALETTE.wall.base;
        ctx.fillRect(wallX, wallY, b.w, WALL_H);
        // 1-px highlight along the eave (top of wall band).
        ctx.fillStyle = PALETTE.wall.hi;
        ctx.fillRect(wallX, wallY, b.w, 1);
        // 1-px shadow along the base where wall meets ground.
        ctx.fillStyle = PALETTE.shadow;
        ctx.fillRect(wallX, wallY + WALL_H - 1, b.w, 1);
        // v22 — ARCHETYPE-SPECIFIC WALL TEXTURE. Each archetype family
        // gets a distinct facade material so buildings read as different
        // construction types even when same-tone:
        //   megacorp-tower → GLASS (no seams, lighter speckle)
        //   office-spire   → PANEL (vertical 1-px lines every 8 px)
        //   tenement       → BRICK (horizontal seam every 4 px, denser)
        //   hostel         → STUCCO (horizontal seam every 8 px, sparse)
        //   ramen/arcade/karaoke/noodle → CONCRETE (default v18 brick pattern)
        applyArchetypeWallTexture(ctx, b, wallX, wallY, WALL_H, rng);

        // v18 — BALCONIES on tier-2+ buildings with tall enough wall bands.
        // Drawn as a 2-px-tall ledge extruded 2 px south of the wall band,
        // with a 1-px cyan rail running along the top. ~30 % of eligible
        // buildings get balconies, position picked by seed bits 6-7.
        if (b.tier >= 2 && WALL_H >= 30 && ((b.seed >>> 6) & 7) < 3) {
            const balconyCount = 1 + ((b.seed >>> 8) & 1);   // 1 or 2 balconies
            for (let i = 0; i < balconyCount; i++) {
                const bwidth = 10 + ((b.seed >>> (10 + i * 3)) & 7);
                const bxStart = wallX + 4 + ((b.seed >>> (12 + i * 4)) % Math.max(1, b.w - bwidth - 8));
                // Y position: 1/3 or 2/3 down the wall band.
                const balY = wallY + Math.floor(WALL_H * (0.30 + i * 0.40));
                if (balY + 4 > wallY + WALL_H - 1) continue;
                // Floor slab — dark grey strip.
                ctx.fillStyle = PALETTE.shadow;
                ctx.fillRect(bxStart, balY + 2, bwidth, 1);             // floor shadow
                ctx.fillStyle = PALETTE.wall.mid;
                ctx.fillRect(bxStart, balY + 1, bwidth, 1);             // floor slab
                // Rail — bright cyan dotted line along the top.
                ctx.fillStyle = PALETTE.neon.cyan;
                for (let rx = bxStart; rx < bxStart + bwidth; rx += 2) {
                    ctx.fillRect(rx, balY, 1, 1);
                }
            }
        }

        // 3. Front windows — pattern chosen per building (issue #37) + as
        //    many stacked rows as the wall band has space for (Grid v7).
        //    A tier-4 tower with an 18-px wall band shows 3 stories.
        const litFracByHealth = [0.0, 0.30, 0.60, 0.85, 1.0];
        const litFrac = litFracByHealth[b.health];
        const litColor = (b.currentFailType === 2) ? PALETTE.window.lit
                       : (b.currentFailType === 1) ? PALETTE.status.warn
                       : (b.currentFailType === 0) ? PALETTE.status.err
                       :                              PALETTE.status.fail;
        // v14 — story stride + window sizes doubled so individual windows
        // are clearly visible at the new pixel resolution. Each window
        // now has a 1-px dark frame around the lit core (visible at zoom 3+).
        const storyStride = 8;
        const usableWallH = WALL_H - 6;     // skip eave + base shadow
        const numStories = Math.max(1, Math.floor(usableWallH / storyStride));
        for (let story = 0; story < numStories; story++) {
            const winY = wallY + 4 + story * storyStride;
            if (windowPattern === 2) {
                // Tall slot windows — 1×5 px, every 5 px.
                for (let xx = wallX + 4; xx < wallX + b.w - 4; xx += 5) {
                    ctx.fillStyle = (rng() < litFrac) ? litColor : PALETTE.window.dark;
                    ctx.fillRect(xx, winY, 1, 5);
                }
            } else if (windowPattern === 3) {
                // Random-placement big windows — 4×4 with frame + v23 glow.
                const winCount = Math.max(2, Math.floor(b.w / 10));
                for (let i = 0; i < winCount; i++) {
                    const xx = wallX + 4 + Math.floor(rng() * Math.max(1, b.w - 10));
                    const isLit = rng() < litFrac;
                    drawLitWindow(ctx, xx, winY, isLit, litColor);
                }
            } else {
                // Default — 4×4 windows on a regular stride with a 1-px dark
                // frame + v23 glow halo around lit ones.
                const stride = (windowPattern === 1) ? 6 : 8;
                for (let xx = wallX + 4; xx < wallX + b.w - 4; xx += stride) {
                    const isLit = rng() < litFrac;
                    drawLitWindow(ctx, xx, winY, isLit, litColor);
                }
            }
        }
        // Inter-story separator lines — implies stacked floors.
        if (numStories > 1) {
            ctx.fillStyle = PALETTE.wall.shade;
            for (let story = 1; story < numStories; story++) {
                const ly = wallY + 2 + story * storyStride;
                ctx.fillRect(wallX + 2, ly, b.w - 4, 1);
            }
        }

        // 4. Door — centred or slightly off-centre, status-coloured glow.
        //    Door position varies per building (3 positions instead of 2).
        const doorVariant = (b.seed >>> 4) % 3;     // 0 left, 1 centre, 2 right
        const doorOffset = (doorVariant === 0) ? -Math.floor(b.w / 4)
                         : (doorVariant === 2) ?  Math.floor(b.w / 4)
                         : 0;
        const doorX = wallX + Math.floor(b.w / 2) - 2 + doorOffset;
        const doorColor = (b.currentFailType === 2 && b.health >= 2)
            ? PALETTE.neon.cyan
            : statusGlow(b.currentFailType);
        // v14 — door scaled up. Frame is 6 px wide with a 4-px inset glass
        // panel and a 2-px-tall coloured lintel that strobes with status.
        ctx.fillStyle = PALETTE.window.frame;
        ctx.fillRect(doorX - 1, wallY + 1, 8, WALL_H - 1);
        ctx.fillStyle = doorColor;
        ctx.fillRect(doorX, wallY + 1, 6, 2);                // wider lintel
        ctx.fillStyle = PALETTE.window.dark;
        ctx.fillRect(doorX, wallY + 3, 6, WALL_H - 5);
        // 1-px highlight on the door frame top + sides for relief.
        ctx.fillStyle = PALETTE.wall.hi;
        ctx.fillRect(doorX - 1, wallY + 1, 1, WALL_H - 1);   // left frame edge

        // 4a. v9b — ground-floor extras: awning canopy above the door,
        //     stair entrance projecting south of the wall, or a marquee
        //     strip along the top of the wall band. Picked per-seed.
        drawGroundFloorVariant(ctx, b, wallX, wallY, doorX, WALL_H,
                               groundFloorVariant, animFrame);

        // 4c. v9b — vertical neon side-sign. Sometimes a building gets a
        //     thin neon strip running down its east or west wall edge
        //     (the "NEON / BAR / RAMEN" vertical signs in the reference).
        //     Drawn as a 1-px-wide bright stripe in the south wall band.
        const sideNeonOn = ((b.seed >>> 21) & 7) < 2;             // ~25 % of buildings
        if (sideNeonOn) {
            const accentColors = [PALETTE.neon.pink, PALETTE.neon.cyan, PALETTE.neon.amber, PALETTE.neon.magenta];
            const sideColor = accentColors[(b.seed >>> 18) % 4];
            const sideRight = ((b.seed >>> 17) & 1) === 0;
            const sx = sideRight ? (wallX + b.w - 2) : (wallX + 1);
            const bri = signBrightnessForAnim(b.currentFailType, animFrame);
            ctx.globalAlpha = bri;
            ctx.fillStyle = sideColor;
            ctx.fillRect(sx, wallY + 1, 1, WALL_H - 2);
            ctx.globalAlpha = 1.0;
        }

        // 4b. Graffiti / decal — 2-3 px decorative marks on the south wall
        //     when wallVariant is the right bit pattern (issue #37). Uses
        //     decorative neon amber/violet, never status hues.
        if (wallVariant === 1 || wallVariant === 2) {
            const decalColor = (wallVariant === 1) ? PALETTE.neon.amber : PALETTE.neon.violet;
            ctx.fillStyle = decalColor;
            // 3-pixel scribble at a per-seed x position, avoiding the door.
            const dxBase = wallX + 1 + ((b.seed >>> 8) % Math.max(1, b.w - 6));
            const dy = wallY + 4 + (((b.seed >>> 16) & 1));
            if (Math.abs((dxBase + 1) - doorX) >= 4) {
                ctx.fillRect(dxBase, dy, 1, 1);
                ctx.fillRect(dxBase + 1, dy + 1, 1, 1);
                ctx.fillRect(dxBase + 2, dy, 1, 1);
            }
        }

        // 5. Roof main fill.
        const roofX = PAD;
        const roofY = PAD;
        ctx.fillStyle = roofBase;
        ctx.fillRect(roofX, roofY, b.w, b.h);

        // 6. Roof corner highlights — top + left lit; bottom + right
        //    shadowed. Imply 3/4 perspective with no gradients.
        ctx.fillStyle = roofHi;
        ctx.fillRect(roofX, roofY, b.w, 1);
        ctx.fillRect(roofX, roofY, 1, b.h);
        ctx.fillStyle = roofLo;
        ctx.fillRect(roofX, roofY + b.h - 1, b.w, 1);
        ctx.fillRect(roofX + b.w - 1, roofY, 1, b.h);

        // 6b. v18 — CHAMFERED ROOF CORNERS. ~20 % of buildings ≥ 48×48 get
        //     their NW + NE roof corners pixel-staircase-rounded (3-step
        //     diagonal) so the city silhouette isn't purely orthogonal.
        //     Drawn by painting over the corner area with sidewalk-coloured
        //     pixels in a staircase pattern. Same trick the v9b L-shape
        //     uses; this is a milder version applied to ALL non-L corners.
        const chamferOn = b.w >= 48 && b.h >= 48 && ((b.seed >>> 22) & 7) < 2;
        if (chamferOn) {
            ctx.fillStyle = PALETTE.street.sidewalk;
            // NW corner — 3-step staircase
            ctx.fillRect(roofX,     roofY,     1, 1);
            ctx.fillRect(roofX + 1, roofY,     1, 1);
            ctx.fillRect(roofX,     roofY + 1, 1, 1);
            // NE corner — 3-step staircase
            ctx.fillRect(roofX + b.w - 1, roofY,     1, 1);
            ctx.fillRect(roofX + b.w - 2, roofY,     1, 1);
            ctx.fillRect(roofX + b.w - 1, roofY + 1, 1, 1);
            // 1-px diagonal seam highlight where the chamfer meets the roof.
            ctx.fillStyle = roofHi;
            ctx.fillRect(roofX + 2, roofY,     1, 1);
            ctx.fillRect(roofX + 1, roofY + 1, 1, 1);
            ctx.fillRect(roofX,     roofY + 2, 1, 1);
            ctx.fillRect(roofX + b.w - 3, roofY,     1, 1);
            ctx.fillRect(roofX + b.w - 2, roofY + 1, 1, 1);
            ctx.fillRect(roofX + b.w - 1, roofY + 2, 1, 1);
        }

        // 7. Roof tile texture — visible grid of subtle darker squares.
        //    Reads as "tiled roof" instead of "flat colour rectangle".
        ctx.fillStyle = roofLo;
        ctx.globalAlpha = 0.55;
        for (let yy = roofY + 4; yy < roofY + b.h - 4; yy += 8) {
            ctx.fillRect(roofX + 2, yy, b.w - 4, 1);
        }
        for (let xx = roofX + 8; xx < roofX + b.w - 4; xx += 8) {
            ctx.fillRect(xx, roofY + 2, 1, b.h - 4);
        }
        ctx.globalAlpha = 1.0;

        // 8. Roof speckle — 1-px lighter dabs for weathering.
        ctx.fillStyle = roofHi;
        for (let yy = roofY + 2; yy < roofY + b.h - 2; yy += 3) {
            for (let xx = roofX + 2; xx < roofX + b.w - 2; xx += 3) {
                if (rng() < 0.10) ctx.fillRect(xx, yy, 1, 1);
            }
        }

        // 8b. v10 — typology dispatch. When a building has been assigned a
        //     non-flat typology, the typology renderer paints over the base
        //     roof with a reference-faithful silhouette (nested slabs for
        //     stepped towers, etc.) and the legacy silhouetteVariant is
        //     suppressed to avoid double-stacking shapes on the roof.
        const typology = b.typology || 'flat';
        if (typology !== 'flat') {
            applyTypology(ctx, b, roofX, roofY, typology, roofBase, roofHi, roofLo, vRng, animFrame);
        } else {
            // 8c. v9b — legacy silhouette variant (kept for flat-typology buildings
            //     until v10b–f migrate every archetype onto typologies).
            applySilhouetteVariant(ctx, b, roofX, roofY, silhouetteVariant,
                                   roofBase, roofHi, roofLo, animFrame);
        }

        // 8d. v19 — per-building NEON EDGE PIPING around the roof perimeter.
        //     The reference shows every distinctive tower wearing a bright
        //     neon line around its full outline (hot pink / cyan / amber
        //     mostly). Replaces the previous corner-L halo which was too
        //     subtle. Applied to tier-2+ buildings + 50% of tier-1 (via
        //     seed bit 25). Stepped-tower typology has its own piping
        //     already so it's skipped here to avoid double piping.
        const edgePipingOn = (b.tier >= 2 || ((b.seed >>> 25) & 1) === 0)
            && typology !== 'stepped-tower' && b.w >= 24 && b.h >= 24;
        if (edgePipingOn) {
            applyEdgePiping(ctx, b, roofX, roofY, animFrame);
        }

        // 9. Archetype-specific roof props (AC units, antenna, hologram pad, lanterns).
        renderRoofProps(ctx, b, roofX, roofY, rng, animFrame);

        // 10. Roof sign — small neon billboard in the CENTRE of the roof
        //     (more readable than south edge at zoom).
        renderRoofSign(ctx, b, roofX, roofY, animFrame);

        // 10b. v12 — L-shape carve. Knock a 30 % corner out of larger
        //      buildings so the city silhouette includes L/T-shapes instead
        //      of pure rectangles. Drawn as a sidewalk-coloured overlay
        //      (the carved area looks like a roof terrace / open courtyard
        //      from above) with a 1-px shadow seam along the inside edge.
        //      Skipped for archetypes that paint a top-edge tag through
        //      drawArchetypeTag (ramen / hostel / arcade) — the carve would
        //      otherwise obliterate the tag.
        // v13 — L-shape rate doubled from 12.5% (== 0) to 25% (<= 1)
        // so more buildings break the rectangular silhouette.
        const shapeVariant = (b.seed >>> 24) & 7;
        const hasTopTag = (b.archetype === 'ramen-stall'
                        || b.archetype === 'hostel'
                        || b.archetype === 'arcade');
        if (shapeVariant <= 1 && b.w >= 36 && b.h >= 36 && !hasTopTag) {
            applyLShapeCarve(ctx, b, roofX, roofY);
        }

        // 11. Damage overlay (health <= 2) — cracks across the roof.
        if (b.health <= 2) {
            const crackCount = [6, 3, 1, 0, 0][b.health];
            ctx.fillStyle = PALETTE.wall.shade;
            for (let i = 0; i < crackCount; i++) {
                const sx = roofX + 2 + Math.floor(rng() * (b.w - 4));
                const sy = roofY + 2 + Math.floor(rng() * (b.h - 4));
                const len = 4 + Math.floor(rng() * 6);
                const dirX = (rng() > 0.5) ? 1 : -1;
                for (let j = 0; j < len; j++) {
                    const px = sx + j * dirX;
                    const py = sy + j;
                    if (px >= roofX && px < roofX + b.w && py >= roofY && py < roofY + b.h) {
                        ctx.fillRect(px, py, 1, 1);
                    }
                }
            }
        }

        // 12. Status halo — restrained 2-px corner accents at the four
        //     roof corners, plus a soft outer glow. Avoids the "whole
        //     building is outlined in green" look from the first pass.
        const halo = statusGlow(b.currentFailType);
        const haloBright = (b.currentFailType === 3)
            ? signBrightnessForAnim(b.currentFailType, animFrame)
            : 1.0;
        ctx.fillStyle = dimColor(halo, haloBright);
        // Top-left corner L.
        ctx.fillRect(roofX - 1, roofY - 1, 4, 1);
        ctx.fillRect(roofX - 1, roofY - 1, 1, 4);
        // Top-right corner L.
        ctx.fillRect(roofX + b.w - 3, roofY - 1, 4, 1);
        ctx.fillRect(roofX + b.w, roofY - 1, 1, 4);
        // Bottom-left corner L.
        ctx.fillRect(roofX - 1, roofY + b.h, 4, 1);
        ctx.fillRect(roofX - 1, roofY + b.h - 3, 1, 4);
        // Bottom-right corner L.
        ctx.fillRect(roofX + b.w - 3, roofY + b.h, 4, 1);
        ctx.fillRect(roofX + b.w, roofY + b.h - 3, 1, 4);
        // Soft glow ring (1 px further out, half alpha).
        ctx.globalAlpha = 0.40 * haloBright;
        ctx.fillRect(roofX - 2, roofY - 2, b.w + 4, 1);
        ctx.fillRect(roofX - 2, roofY + b.h + 1, b.w + 4, 1);
        ctx.fillRect(roofX - 2, roofY - 2, 1, b.h + 4);
        ctx.fillRect(roofX + b.w + 1, roofY - 2, 1, b.h + 4);
        ctx.globalAlpha = 1.0;

        state.diag.lastBuildingRenderMs = performance.now() - t0;
        return c;
    }

    // Map archetype name to the corresponding PALETTE.roof key suffix.
    function archetypeKey(arch, suffix) {
        const k = {
            'tenement': 'tenement',
            'office-spire': 'office',
            'megacorp-tower': 'megacorp',
            'karaoke-bar': 'karaoke',
            'ramen-stall': 'ramen',
            'hostel': 'hostel',
            'arcade': 'arcade',
            'noodle-cart': 'noodleCart',
        }[arch] || 'tenement';
        return k + (suffix === 'base' ? '' : suffix);
    }

    // Issue #23 — small bitmap-font label along the TOP edge of a building's
    // roof identifying its archetype (e.g. "RAMEN", "ARCADE", "HOSTEL").
    // Drawn at y+3 so the title sign in the centre never collides with it.
    // Skipped if the tag doesn't fit horizontally inside the roof.
    function drawArchetypeTag(ctx, b, x, y, label, baseColor, animFrame) {
        const w = textWidth(label);
        if (w > b.w - 4) return;
        const tagBri = signBrightnessForAnim(b.currentFailType, animFrame);
        const color = (b.currentFailType === 3 && tagBri < 0.2)
            ? PALETTE.shadow
            : dimColor(baseColor, tagBri);
        const tx = x + Math.floor((b.w - w) / 2);
        drawText(ctx, label, tx, y + 3, color);
    }

    // ---------------------------------------------------------
    //   v9b silhouette + ground-floor variants
    //   Adds shape variety beyond "rectangle with windows": stepped
    //   towers, peaked roofs, L-shape cut-outs, antenna stacks,
    //   billboards. Plus ground-floor extras (awnings, stairs,
    //   marquee strips) at the south wall.
    // ---------------------------------------------------------
    function applySilhouetteVariant(ctx, b, x, y, variant, roofBase, roofHi, roofLo, animFrame) {
        // Variants 0, 6, 7 → plain flat roof (drawn already, no overlay).
        // Variants 1-5 → distinct silhouette modifications.
        if (variant === 1)      drawSteppedTower(ctx, b, x, y, roofBase, roofHi, roofLo);
        else if (variant === 2) drawPeakedRoof(ctx, b, x, y, roofHi, roofLo);
        else if (variant === 3) drawLShapeRoof(ctx, b, x, y);
        else if (variant === 4) drawAntennaStack(ctx, b, x, y, animFrame);
        else if (variant === 5) drawBillboardTop(ctx, b, x, y, animFrame);
    }

    function drawSteppedTower(ctx, b, x, y, base, hi, lo) {
        // Smaller darker slab on top of the main roof → "second floor"
        // tower silhouette. Skipped on tiny buildings (would dominate).
        if (b.w < 28 || b.h < 28) return;
        const upperW = Math.max(10, Math.floor(b.w * 0.55));
        const upperH = Math.max(10, Math.floor(b.h * 0.55));
        // Offset slightly so it isn't dead-centre — gives asymmetry.
        const offX = ((b.seed >>> 5) & 3) - 1;
        const offY = ((b.seed >>> 7) & 3) - 1;
        const ox = x + Math.floor((b.w - upperW) / 2) + offX;
        const oy = y + Math.floor((b.h - upperH) / 2) + offY;
        ctx.fillStyle = lo;
        ctx.fillRect(ox, oy, upperW, upperH);
        ctx.fillStyle = hi;
        ctx.fillRect(ox, oy, upperW, 1);
        ctx.fillRect(ox, oy, 1, upperH);
        ctx.fillStyle = PALETTE.shadow;
        ctx.fillRect(ox, oy + upperH - 1, upperW, 1);
        ctx.fillRect(ox + upperW - 1, oy, 1, upperH);
        // Drop shadow on the lower roof to imply elevation.
        ctx.globalAlpha = 0.30;
        ctx.fillRect(ox + 1, oy + upperH, upperW, 1);
        ctx.globalAlpha = 1.0;
    }

    function drawPeakedRoof(ctx, b, x, y, hi, lo) {
        // Diagonal shadow across half the roof + ridge line → "pitched roof"
        // hint that breaks the perfectly-flat slab look.
        if (b.w < 18 || b.h < 18) return;
        ctx.fillStyle = lo;
        ctx.globalAlpha = 0.40;
        // East-half shadow.
        ctx.fillRect(x + Math.floor(b.w / 2), y, Math.ceil(b.w / 2), b.h);
        ctx.globalAlpha = 1.0;
        // Ridge highlight (vertical line down the middle).
        ctx.fillStyle = hi;
        ctx.fillRect(x + Math.floor(b.w / 2), y + 1, 1, b.h - 2);
    }

    function drawLShapeRoof(ctx, b, x, y) {
        // "Cut" a corner of the roof footprint — paint sidewalk colour over
        // a quarter so the roof reads as L-shaped from above.
        if (b.w < 24 || b.h < 24) return;
        const cutW = Math.floor(b.w / 3);
        const cutH = Math.floor(b.h / 3);
        const corner = (b.seed >>> 11) & 3;
        let cx, cy;
        if (corner === 0)      { cx = x;                   cy = y; }
        else if (corner === 1) { cx = x + b.w - cutW;      cy = y; }
        else if (corner === 2) { cx = x + b.w - cutW;      cy = y + b.h - cutH; }
        else                   { cx = x;                   cy = y + b.h - cutH; }
        ctx.fillStyle = PALETTE.street.sidewalk;
        ctx.fillRect(cx, cy, cutW, cutH);
        // Stable speckle.
        ctx.fillStyle = PALETTE.street.sidewalkHi;
        for (let i = 0; i < 6; i++) {
            const sx = cx + 1 + (((b.seed >>> (i * 2)) ^ i) & 0xFF) % Math.max(1, cutW - 2);
            const sy = cy + 1 + (((b.seed >>> (i * 2 + 1)) ^ (i * 3)) & 0xFF) % Math.max(1, cutH - 2);
            ctx.fillRect(sx, sy, 1, 1);
        }
        // Edge seam between the L's "inner" corner.
        ctx.fillStyle = PALETTE.shadow;
        if (corner === 0 || corner === 3) ctx.fillRect(cx + cutW, cy, 1, cutH);
        if (corner === 0 || corner === 1) ctx.fillRect(cx, cy + cutH, cutW, 1);
        if (corner === 1 || corner === 2) ctx.fillRect(cx - 1, cy, 1, cutH);
        if (corner === 2 || corner === 3) ctx.fillRect(cx, cy - 1, cutW, 1);
    }

    function drawAntennaStack(ctx, b, x, y, animFrame) {
        // 2-3 vertical poles rising from the roof's top edge, each with a
        // blinking light at the tip. Compact + readable at zoom 4×+.
        if (b.w < 14) return;
        const count = 2 + ((b.seed >>> 13) & 1);
        const blink = ((animFrame % 6) < 2);
        const blinkColor = blink ? statusGlow(b.currentFailType) : PALETTE.wall.shade;
        const stride = Math.floor(b.w / (count + 1));
        for (let i = 0; i < count; i++) {
            const ax = x + stride * (i + 1);
            const len = 4 + (((b.seed >>> (i * 3 + 1)) & 3));
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(ax, y + 1 - len, 1, len);
            ctx.fillStyle = blinkColor;
            ctx.fillRect(ax, y - len, 1, 1);
        }
    }

    function drawBillboardTop(ctx, b, x, y, animFrame) {
        // A backboard + neon-edged sign rising from the roof.
        if (b.w < 26) return;
        const bri = signBrightnessForAnim(b.currentFailType, animFrame);
        const colorBase = (b.seed % 2 === 0) ? PALETTE.neon.pink : PALETTE.neon.cyan;
        const color = (b.currentFailType === 3 && bri < 0.2)
            ? PALETTE.shadow : dimColor(colorBase, bri);
        const bw = Math.max(14, Math.floor(b.w * 0.55));
        const bh = 4;
        const bx = x + Math.floor((b.w - bw) / 2);
        const by = y - bh - 1;
        // Supports.
        ctx.fillStyle = PALETTE.wall.hi;
        ctx.fillRect(bx + 1,        by + bh, 1, 1);
        ctx.fillRect(bx + bw - 2,   by + bh, 1, 1);
        // Sign body + neon border.
        ctx.fillStyle = PALETTE.wall.shade;
        ctx.fillRect(bx, by, bw, bh);
        ctx.fillStyle = color;
        ctx.fillRect(bx, by, bw, 1);
        ctx.fillRect(bx, by + bh - 1, bw, 1);
        ctx.fillRect(bx, by, 1, bh);
        ctx.fillRect(bx + bw - 1, by, 1, bh);
        ctx.globalAlpha = 0.45;
        ctx.fillRect(bx + 1, by + 1, bw - 2, bh - 2);
        ctx.globalAlpha = 1.0;
    }

    // ---------------------------------------------------------
    //   v10 typology dispatch (issue #50 spec, issue #11 #44 follow-up)
    //   When a building's typology is non-flat, this function paints over
    //   the base roof with a reference-faithful silhouette. The base roof,
    //   tile texture, and speckle have already been drawn underneath —
    //   most of that gets hidden by the new slabs, which is fine.
    // ---------------------------------------------------------
    // v12 — L-shape footprint carve. The reference shows many buildings
    // with L-shaped, T-shaped, or notched roofs from above. We approximate
    // by overlaying a sidewalk-coloured rectangle on one corner of the roof,
    // creating a visible "notch" that breaks the rectangular silhouette.
    // Only fires for buildings >= 40 px (so the cut is recognisable) and
    // only on the NW or NE corner (south corners would interfere with the
    // visible wall band).
    // v19 — neon edge piping around the roof perimeter. Each building
    // picks ONE accent colour from an 8-entry palette by seed bits 16-18
    // so the city reads as a mix of pink / cyan / amber / violet outlines
    // — the dominant visual feature of the tile-B-01 reference. Strength
    // tracks sign-brightness so failure flashes propagate to the trim.
    function applyEdgePiping(ctx, b, x, y, animFrame) {
        const accent8 = [
            PALETTE.neon.pink,    PALETTE.neon.cyan,    PALETTE.neon.amber,
            PALETTE.neon.violet,  PALETTE.neon.magenta, PALETTE.neon.pink,
            PALETTE.neon.cyan,    PALETTE.neon.amber,
        ];
        const accent = accent8[(b.seed >>> 16) & 7];
        const bri = signBrightnessForAnim(b.currentFailType, animFrame);
        // Cap at 0.75 so the piping doesn't overpower the title or
        // window cluster; below 0.20 (fail strobe trough) skip drawing
        // entirely so the trim flickers along with the warning state.
        if (bri < 0.20) return;
        const trim = dimColor(accent, Math.min(0.75, bri));
        ctx.fillStyle = trim;
        // 1-px-wide border, 1 px inset from the absolute edge so it sits
        // INSIDE the roof rectangle (otherwise it'd be clipped by the
        // halo step).
        ctx.fillRect(x + 1,         y + 1,         b.w - 2, 1);     // top
        ctx.fillRect(x + 1,         y + b.h - 2,   b.w - 2, 1);     // bottom
        ctx.fillRect(x + 1,         y + 1,         1, b.h - 2);     // left
        ctx.fillRect(x + b.w - 2,   y + 1,         1, b.h - 2);     // right
        // 1-px corner dots in a brighter version of the trim — the
        // "neon connectors" at each corner that distinguish edge piping
        // from a plain painted outline.
        ctx.fillStyle = accent;
        ctx.fillRect(x + 1,         y + 1,         1, 1);
        ctx.fillRect(x + b.w - 2,   y + 1,         1, 1);
        ctx.fillRect(x + 1,         y + b.h - 2,   1, 1);
        ctx.fillRect(x + b.w - 2,   y + b.h - 2,   1, 1);
    }

    function applyLShapeCarve(ctx, b, x, y) {
        const cutFrac = 0.32;
        const cutW = Math.floor(b.w * cutFrac);
        const cutH = Math.floor(b.h * cutFrac);
        const isNE = ((b.seed >>> 27) & 1) === 0;
        const cx = isNE ? (x + b.w - cutW) : x;
        const cy = y;

        // Paint the carved area as sidewalk so it reads as "no roof there".
        ctx.fillStyle = PALETTE.street.sidewalk;
        ctx.fillRect(cx, cy, cutW, cutH);

        // Subtle per-cell speckle on the carved area so it doesn't read as
        // a flat block of colour — matches the surrounding sidewalk noise.
        ctx.fillStyle = PALETTE.street.sidewalkHi;
        for (let i = 0; i < 8; i++) {
            const sx = cx + 1 + (((b.seed >>> (i * 2)) ^ i) & 0xFF) % Math.max(1, cutW - 2);
            const sy = cy + 1 + (((b.seed >>> (i * 2 + 1)) ^ (i * 3)) & 0xFF) % Math.max(1, cutH - 2);
            ctx.fillRect(sx, sy, 1, 1);
        }

        // Hard shadow seams along the INSIDE edges (where the roof meets the
        // carved area). The outer edges are already the city ground — no
        // shadow needed there.
        ctx.fillStyle = PALETTE.shadow;
        // Bottom of the cut (always present — runs the full cut width).
        ctx.fillRect(cx, cy + cutH, cutW, 1);
        // Inside vertical edge of the cut.
        if (isNE) {
            ctx.fillRect(cx - 1, cy, 1, cutH);
        } else {
            ctx.fillRect(cx + cutW, cy, 1, cutH);
        }

        // Optional neon edge piping where the roof meets the cut — implies
        // the L-shape boundary has architectural lighting.
        const trim = ((b.seed >>> 28) & 1) === 0 ? PALETTE.neon.cyan : PALETTE.neon.pink;
        ctx.fillStyle = trim;
        ctx.globalAlpha = 0.45;
        ctx.fillRect(cx, cy + cutH + 1, cutW, 1);
        if (isNE) {
            ctx.fillRect(cx - 1, cy + 1, 1, cutH);
        } else {
            ctx.fillRect(cx + cutW, cy + 1, 1, cutH);
        }
        ctx.globalAlpha = 1.0;
    }

    function applyTypology(ctx, b, x, y, kind, roofBase, roofHi, roofLo, vRng, animFrame) {
        if (kind === 'stepped-tower') {
            drawSteppedTowerTypology(ctx, b, x, y, roofBase, roofHi, roofLo, vRng, animFrame);
        } else if (kind === 'long-block') {
            drawLongBlockTypology(ctx, b, x, y, roofBase, roofHi, roofLo, vRng, animFrame);
        } else if (kind === 'storefront') {
            drawStorefrontTypology(ctx, b, x, y, roofBase, roofHi, roofLo, vRng, animFrame);
        } else if (kind === 'installation-roof') {
            drawInstallationRoofTypology(ctx, b, x, y, roofBase, roofHi, roofLo, vRng, animFrame);
        }
    }

    // Reference: tile-B-01.png shows tier-4 cyberpunk towers as 2–3 concentric
    // slabs stacked at slight offsets, each level a darker shade than the one
    // below, with hot-pink neon piping wrapping each slab perimeter and a
    // single column of bright cyan windows running down a long axis. The
    // overall silhouette reads as a stepped pyramid from above — distinct from
    // the flat-rectangle treatment all megacorp/office buildings share today.
    function drawSteppedTowerTypology(ctx, b, x, y, roofBase, roofHi, roofLo, vRng, animFrame) {
        // Need a footprint big enough to fit three nested levels of ≥ 10 px
        // each with at least a 3-px gap between levels (so the piping reads).
        if (b.w < 28 || b.h < 28) return;

        // Pick neon trim colour — pink dominates in the reference, with cyan
        // as an occasional alternate. The whole tower uses ONE trim colour
        // so it reads as a coherent piece, not a random pile of overlays.
        const trimPink = ((b.seed >>> 19) & 3) !== 0;
        const trim = trimPink ? PALETTE.neon.pink : PALETTE.neon.cyan;
        // Trim animates with the sign-brightness curve so it strobes on
        // fail flashes, but cap at 0.65 so it never overpowers the title.
        const bri = signBrightnessForAnim(b.currentFailType, animFrame);
        const trimDim = dimColor(trim, Math.min(0.65, bri));

        // Three concentric slabs. Each level is ~70% of the one below.
        // Slight asymmetric offset per seed so the tower has a "tilt".
        const offSeed1 = ((b.seed >>> 5) & 3) - 1;  // -1, 0, 1, 2
        const offSeed2 = ((b.seed >>> 7) & 3) - 1;
        // v11 — each successive level a touch BRIGHTER than the base
        // (looking up at a tower from above, the upper floors catch more
        // ambient light). Inner slab uses roofHi so any roof clutter
        // drawn on top (AC arrays, water tanks, antennas) reads cleanly
        // against it instead of being swallowed by black-on-black.
        const levels = [
            { w: b.w,              h: b.h,              fill: roofBase, offX: 0,        offY: 0 },
            { w: Math.floor(b.w * 0.72), h: Math.floor(b.h * 0.72),
              fill: roofBase, offX: offSeed1, offY: offSeed2 },
            { w: Math.floor(b.w * 0.44), h: Math.floor(b.h * 0.44),
              fill: roofHi, offX: -offSeed1, offY: -offSeed2 },
        ];

        // Skip the base level — already painted by step 5 of renderBuilding.
        for (let i = 1; i < levels.length; i++) {
            const L = levels[i];
            // Centre each level within its parent + per-seed offset.
            const prev = levels[i - 1];
            const lx = x + Math.floor((b.w - L.w) / 2) + L.offX;
            const ly = y + Math.floor((b.h - L.h) / 2) + L.offY;

            // Drop shadow under the slab — implies elevation off the one below.
            ctx.globalAlpha = 0.40;
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(lx + 1, ly + L.h, L.w, 1);
            ctx.fillRect(lx + L.w, ly + 1, 1, L.h);
            ctx.globalAlpha = 1.0;

            // Slab fill.
            ctx.fillStyle = L.fill;
            ctx.fillRect(lx, ly, L.w, L.h);
            // 1-px top + left highlight, bottom + right shadow — 3/4 perspective.
            ctx.fillStyle = roofHi;
            ctx.fillRect(lx, ly, L.w, 1);
            ctx.fillRect(lx, ly, 1, L.h);
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(lx, ly + L.h - 1, L.w, 1);
            ctx.fillRect(lx + L.w - 1, ly, 1, L.h);

            // Neon piping around the perimeter — 1-px line just inside the
            // edges, dimmed to bri so it strobes with sign brightness.
            ctx.fillStyle = trimDim;
            ctx.fillRect(lx + 1, ly + 1, L.w - 2, 1);
            ctx.fillRect(lx + 1, ly + L.h - 2, L.w - 2, 1);
            ctx.fillRect(lx + 1, ly + 1, 1, L.h - 2);
            ctx.fillRect(lx + L.w - 2, ly + 1, 1, L.h - 2);
        }

        // Bright vertical window column down the centre of the TOP level.
        // The reference shows top-level slabs with a single bright cyan stripe
        // running their full height — implies a stairwell / lit corridor.
        const top = levels[2];
        const tx = x + Math.floor((b.w - top.w) / 2) + top.offX;
        const ty = y + Math.floor((b.h - top.h) / 2) + top.offY;
        const litColor = (b.currentFailType === 2) ? PALETTE.window.lit
                       : (b.currentFailType === 1) ? PALETTE.status.warn
                       : (b.currentFailType === 0) ? PALETTE.status.err
                       :                              PALETTE.status.fail;
        const colX = tx + Math.floor(top.w / 2);
        // Skip the bordering piping (2 px in from each end).
        for (let yy = ty + 3; yy < ty + top.h - 3; yy += 2) {
            ctx.fillStyle = (vRng() < 0.75) ? litColor : PALETTE.window.dark;
            ctx.fillRect(colX, yy, 1, 1);
        }
    }

    // Reference: tile-B-01.png shows tenement / apartment blocks as wide
    // rectangular footprints with a single ROW of bright cyan slot-windows
    // running along the long axis at one end of the roof. The rest of the
    // roof is mostly bare, with 1–2 small dark HVAC squares and the
    // occasional 1-px antenna stalk poking off a corner. Reads as
    // "low-rise apartment" — distinct from the tower vertical-column
    // pattern.
    function drawLongBlockTypology(ctx, b, x, y, roofBase, roofHi, roofLo, vRng, animFrame) {
        // Long axis: horizontal when wider than tall, vertical otherwise.
        const horizontal = b.w >= b.h;
        const litColor = (b.currentFailType === 2) ? PALETTE.window.lit
                       : (b.currentFailType === 1) ? PALETTE.status.warn
                       : (b.currentFailType === 0) ? PALETTE.status.err
                       :                              PALETTE.status.fail;
        const litFracByHealth = [0.0, 0.30, 0.60, 0.85, 1.0];
        const litFrac = litFracByHealth[b.health];

        // Two parallel rows of fat cyan windows along the SHORT-axis end of
        // the roof (north edge on horizontal blocks, west edge on vertical
        // blocks). Reference tenements show 2–3 stacked rows of 2-px-wide
        // window slits clustered against ONE end of the roof — never
        // centred or spread across the full roof. The opposite end is
        // bare (HVAC + antenna land there).
        const STRIP_INSET = 3;
        const SLOT_LONG = 2;     // long-axis dimension (was 2, kept)
        const SLOT_SHORT = 2;    // short-axis dimension (was 1, now 2)
        const ROW_GAP = 4;       // px between the two parallel rows
        if (horizontal) {
            const startX = x + 3;
            const endX = x + b.w - 3;
            for (let row = 0; row < 2; row++) {
                const ry = y + STRIP_INSET + row * ROW_GAP;
                if (ry + SLOT_SHORT > y + b.h - 3) break;
                for (let xx = startX; xx + SLOT_LONG <= endX; xx += 4) {
                    ctx.fillStyle = (vRng() < litFrac) ? litColor : PALETTE.window.dark;
                    ctx.fillRect(xx, ry, SLOT_LONG, SLOT_SHORT);
                }
            }
            // Subtle dark seam below the window cluster.
            const seamY = y + STRIP_INSET + ROW_GAP + SLOT_SHORT + 1;
            if (seamY < y + b.h - 2) {
                ctx.fillStyle = PALETTE.shadow;
                ctx.globalAlpha = 0.55;
                ctx.fillRect(x + 2, seamY, b.w - 4, 1);
                ctx.globalAlpha = 1.0;
            }
        } else {
            const startY = y + 3;
            const endY = y + b.h - 3;
            for (let col = 0; col < 2; col++) {
                const rx = x + STRIP_INSET + col * ROW_GAP;
                if (rx + SLOT_SHORT > x + b.w - 3) break;
                for (let yy = startY; yy + SLOT_LONG <= endY; yy += 4) {
                    ctx.fillStyle = (vRng() < litFrac) ? litColor : PALETTE.window.dark;
                    ctx.fillRect(rx, yy, SLOT_SHORT, SLOT_LONG);
                }
            }
            const seamX = x + STRIP_INSET + ROW_GAP + SLOT_SHORT + 1;
            if (seamX < x + b.w - 2) {
                ctx.fillStyle = PALETTE.shadow;
                ctx.globalAlpha = 0.55;
                ctx.fillRect(seamX, y + 2, 1, b.h - 4);
                ctx.globalAlpha = 1.0;
            }
        }

        // HVAC clutter — 1 or 2 small dark squares clustered on the FAR
        // half of the roof from the window strip. On horizontal blocks
        // the windows live at y+3..y+8, so HVAC lands in y+12..y+(h-4).
        // On vertical blocks the windows live at x+3..x+8 so HVAC lands
        // in x+12..x+(w-4).
        const hvacCount = 1 + (((b.seed >>> 23) & 1));
        const hvacRegion = horizontal
            ? { x0: x + 4,   y0: y + 12, x1: x + b.w - 5, y1: y + b.h - 4 }
            : { x0: x + 12,  y0: y + 4,  x1: x + b.w - 4, y1: y + b.h - 5 };
        for (let i = 0; i < hvacCount; i++) {
            const w0 = Math.max(1, hvacRegion.x1 - hvacRegion.x0);
            const h0 = Math.max(1, hvacRegion.y1 - hvacRegion.y0);
            const hx = hvacRegion.x0 + Math.floor(vRng() * w0);
            const hy = hvacRegion.y0 + Math.floor(vRng() * h0);
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(hx, hy, 3, 3);
            // 1-px lighter cap (vent grille).
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(hx, hy, 3, 1);
        }

        // Optional antenna stalk at the corner farthest from the windows.
        // 1-px line with a single-pixel tip light. Drawn on ~50% of
        // long-blocks (seed bit 28) so identical adjacent buildings differ.
        if (((b.seed >>> 28) & 1) === 0) {
            const ax = horizontal ? (x + b.w - 4) : (x + b.w - 3);
            const ay = horizontal ? (y + b.h - 5) : (y + b.h - 5);
            const len = 4 + (((b.seed >>> 4) & 3));
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(ax, ay - len, 1, len);
            const blink = ((animFrame % 8) < 3);
            ctx.fillStyle = blink ? statusGlow(b.currentFailType) : PALETTE.wall.shade;
            ctx.fillRect(ax, ay - len - 1, 1, 1);
        }
    }

    // Reference: tile-B-01.png shows shopfronts as small flat dark roofs
    // wrapped in a bright neon edge-piping in the shop's accent colour —
    // amber for noodle/ramen, magenta for arcade, pink for karaoke. This
    // is the "shop awning seen from above" — a thin lit strip that
    // immediately signals "this is a small business" from a wide view.
    // We add piping along the north (top) edge only — the south edge
    // already carries the archetype-specific decoration (RAMEN tag,
    // ARCADE marquee, lantern row, noodle wheels), and the east/west
    // edges sometimes carry the v9b side-neon stripe.
    function drawStorefrontTypology(ctx, b, x, y, roofBase, roofHi, roofLo, vRng, animFrame) {
        const colorByArchetype = {
            'ramen-stall':  PALETTE.neon.amber,
            'arcade':       PALETTE.neon.magenta,
            'karaoke-bar':  PALETTE.neon.pink,
            'noodle-cart':  PALETTE.neon.amber,
        };
        const trim = colorByArchetype[b.archetype] || PALETTE.neon.cyan;
        // Use sign-brightness so the trim strobes on fail flashes, but cap
        // at 0.75 so it never fully overpowers the title or wall band.
        const bri = signBrightnessForAnim(b.currentFailType, animFrame);
        const trimDim = dimColor(trim, Math.min(0.75, bri));

        // 1. North-edge piping — 1 px bright strip along the top of the
        //    roof. The archetype tag (RAMEN/ARCADE/HOSTEL) is drawn by
        //    drawArchetypeTag at y + 3 so the piping at y + 0 sits ABOVE
        //    it, framing the tag in the shop's colour.
        ctx.fillStyle = trimDim;
        ctx.fillRect(x + 1, y, b.w - 2, 1);

        // 2. Two 1-px corner "studs" — short vertical extensions of the
        //    piping that wrap around the NW and NE corners. Implies a
        //    physical awning frame, not just a paint line.
        ctx.fillRect(x + 1,         y, 1, 2);
        ctx.fillRect(x + b.w - 2,   y, 1, 2);

        // 3. Soft inset shadow 1 px below the piping — separates the
        //    bright strip from the roof so it reads as an edge piece, not
        //    a paint stripe on the roof surface.
        ctx.fillStyle = PALETTE.shadow;
        ctx.globalAlpha = 0.50;
        ctx.fillRect(x + 2, y + 1, b.w - 4, 1);
        ctx.globalAlpha = 1.0;

        // 4. A single small dark HVAC unit at NE corner (clear of the
        //    south-edge decoration and the piping at the top). Small
        //    buildings (noodle-cart) skip this — too cramped.
        if (b.w >= 14 && b.h >= 14) {
            const hx = x + b.w - 5;
            const hy = y + 3;
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(hx, hy, 2, 2);
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(hx, hy, 2, 1);
        }
    }

    // Reference: tile-B-01.png shows mid-rise offices/megacorps with ONE
    // big distinctive rooftop installation that reads as the building's
    // identity from above — sat dish, hologram pad, cooling tower, or
    // helipad. The installation occupies ~1/4 of the roof and is placed
    // off-centre (typically NE quadrant) so the centred title sign and
    // any legacy archetype prop (sat dish from renderRoofProps) still
    // have room.
    function drawInstallationRoofTypology(ctx, b, x, y, roofBase, roofHi, roofLo, vRng, animFrame) {
        // Need ≥ 18×18 to fit a 9-px installation + 4-px margin. typologyFor
        // already enforced this but defend in case footprints jitter smaller.
        if (b.w < 18 || b.h < 18) return;

        // Pick 1 of 4 features by seed bits 27-28.
        const feature = (b.seed >>> 27) & 3;

        // Anchor point — NE quadrant, 9-px feature with 3-px right margin.
        const ax = x + b.w - 12;
        const ay = y + 3;
        const bri = signBrightnessForAnim(b.currentFailType, animFrame);

        if (feature === 0) {
            // Sat dish — bright outline + dark interior + central post +
            // animated tip light. 9-wide pixel-art parabola: 9-wide top
            // arc, then 5-wide bowl, 3-wide base.
            // Bright outline first (wall.hi reads against the dark roof).
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(ax,     ay + 1, 9, 1);                // top arc
            ctx.fillRect(ax + 1, ay + 5, 7, 1);                // bottom arc
            ctx.fillRect(ax,     ay + 2, 1, 4);                // left edge
            ctx.fillRect(ax + 8, ay + 2, 1, 4);                // right edge
            // Dark dish interior.
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(ax + 1, ay + 2, 7, 3);
            // 1-px highlighted inner rim at the top.
            ctx.fillStyle = PALETTE.wall.mid;
            ctx.fillRect(ax + 1, ay + 2, 7, 1);
            // Vertical mast + animated tip light.
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(ax + 4, ay - 1, 1, 3);
            const blink = ((animFrame % 8) < 3);
            ctx.fillStyle = blink ? statusGlow(b.currentFailType) : PALETTE.wall.shade;
            ctx.fillRect(ax + 4, ay - 2, 1, 1);
        } else if (feature === 1) {
            // Hologram pad — 9×7 dark base with a bright 3×3 lit centre
            // glowing in the building's status colour, plus 1-px corner
            // accents at all 4 corners (reads as 'landing zone with
            // beacon').
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(ax, ay, 9, 7);
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(ax, ay, 9, 1);                        // top edge
            ctx.fillRect(ax, ay, 1, 7);                        // left edge
            ctx.fillStyle = PALETTE.wall.mid;
            ctx.fillRect(ax + 8, ay + 1, 1, 6);                // right edge (shade)
            ctx.fillRect(ax + 1, ay + 6, 7, 1);                // bottom edge (shade)
            // Centre glow — status hue when not OK, cyan when OK.
            const glowColor = (b.currentFailType === 2)
                ? PALETTE.neon.cyan
                : statusGlow(b.currentFailType);
            ctx.fillStyle = dimColor(glowColor, Math.min(0.85, bri));
            ctx.fillRect(ax + 3, ay + 2, 3, 3);
            // Outer pulse — half-alpha ring 1 px out from the glow.
            ctx.globalAlpha = 0.40 * bri;
            ctx.fillRect(ax + 2, ay + 1, 5, 1);
            ctx.fillRect(ax + 2, ay + 5, 5, 1);
            ctx.fillRect(ax + 2, ay + 2, 1, 3);
            ctx.fillRect(ax + 6, ay + 2, 1, 3);
            ctx.globalAlpha = 1.0;
            // 1-px cyan corner accent at NE only — implies "active beacon".
            ctx.fillStyle = PALETTE.neon.cyan;
            ctx.fillRect(ax + 8, ay, 1, 1);
        } else if (feature === 2) {
            // Cooling tower — 7-wide circular dark cylinder with bright
            // top rim, animated steam plume rising NE. Tank silhouette
            // is a 9-tall pixel oval.
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(ax + 1, ay,     5, 1);                // top arc
            ctx.fillRect(ax + 1, ay + 6, 5, 1);                // bottom arc
            ctx.fillRect(ax,     ay + 1, 1, 5);                // left wall
            ctx.fillRect(ax + 6, ay + 1, 1, 5);                // right wall
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(ax + 1, ay + 1, 5, 5);                // interior
            ctx.fillStyle = PALETTE.wall.mid;
            ctx.fillRect(ax + 2, ay + 1, 3, 1);                // top inner rim
            // Steam plume — 3 layers, alternates 8 fps. Spreads NE.
            const puffDense = (animFrame % 4) < 2;
            ctx.globalAlpha = puffDense ? 0.55 : 0.30;
            ctx.fillStyle = '#d9d4e6';
            ctx.fillRect(ax + 6, ay - 1, 1, 1);
            ctx.fillRect(ax + 7, ay - 2, 1, 1);
            if (puffDense) {
                ctx.fillRect(ax + 8, ay - 3, 1, 1);
                ctx.fillRect(ax + 7, ay - 1, 1, 1);
            }
            ctx.globalAlpha = 1.0;
        } else {
            // Helipad — 9×9 dark square with bright cyan "H" + cyan corner
            // lights at all 4 corners + bright top edge for visibility.
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(ax, ay, 9, 9);
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(ax, ay, 9, 1);                        // top edge
            ctx.fillRect(ax, ay, 1, 9);                        // left edge
            ctx.fillStyle = PALETTE.wall.mid;
            ctx.fillRect(ax + 8, ay, 1, 9);                    // right shade
            ctx.fillRect(ax, ay + 8, 9, 1);                    // bottom shade
            // Corner lights — bright cyan dots, sign-brightness-modulated.
            const padCyan = dimColor(PALETTE.neon.cyan, Math.min(0.90, bri));
            ctx.fillStyle = padCyan;
            ctx.fillRect(ax + 1, ay + 1, 1, 1);
            ctx.fillRect(ax + 7, ay + 1, 1, 1);
            ctx.fillRect(ax + 1, ay + 7, 1, 1);
            ctx.fillRect(ax + 7, ay + 7, 1, 1);
            // H glyph — 2 vertical + 1 horizontal stroke. 5-tall.
            ctx.fillRect(ax + 3, ay + 2, 1, 5);
            ctx.fillRect(ax + 5, ay + 2, 1, 5);
            ctx.fillRect(ax + 3, ay + 4, 3, 1);
        }
    }

    function drawGroundFloorVariant(ctx, b, wallX, wallY, doorX, wallH_local, variant, animFrame) {
        // Variant 0 → plain (no extras). Variants 1-3 → add a ground-floor
        // detail at the south wall so the entrance reads as a real shopfront.
        if (variant === 0) return;
        const accentColors = [PALETTE.neon.pink, PALETTE.neon.cyan, PALETTE.neon.amber, PALETTE.neon.violet];
        const accentColor = accentColors[(b.seed >>> 16) % 4];

        if (variant === 1 || variant === 2) {
            // Awning canopy — small overhang ABOVE the door.
            const awningW = 6;
            const awningX = doorX - Math.floor(awningW / 2) + 1;
            const awningY = wallY - 1;
            // Skip if outside the building footprint horizontally.
            if (awningX < wallX || awningX + awningW > wallX + b.w) {
                // fall through to stair drawing
            } else {
                ctx.fillStyle = accentColor;
                ctx.fillRect(awningX, awningY, awningW, 1);
                ctx.fillStyle = PALETTE.shadow;
                if (awningX - 1 >= wallX)       ctx.fillRect(awningX - 1, awningY + 1, 1, 1);
                if (awningX + awningW <= wallX + b.w) ctx.fillRect(awningX + awningW, awningY + 1, 1, 1);
            }
        }
        if (variant === 2 || variant === 3) {
            // Stair entrance projecting south of the door (3 px deep).
            const stairY = wallY + wallH_local;
            const stepW = 4;
            const stepX = doorX - 1;
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(stepX, stairY, stepW, 1);
            ctx.fillStyle = PALETTE.wall.mid;
            ctx.fillRect(stepX - 1, stairY + 1, stepW + 2, 1);
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(stepX - 1, stairY + 2, stepW + 2, 1);
        }
        if (variant === 3) {
            // Marquee strip along the very top of the wall band — a lit
            // neon ribbon between the eave and the first window row.
            const stripY = wallY + 1;
            const bri = signBrightnessForAnim(b.currentFailType, animFrame);
            ctx.globalAlpha = bri;
            ctx.fillStyle = accentColor;
            ctx.fillRect(wallX + 1, stripY, b.w - 2, 1);
            ctx.globalAlpha = 1.0;
        }
    }

    function renderRoofProps(ctx, b, x, y, rng, animFrame) {
        // Issue #37 — per-instance prop variation. The seed picks AC count,
        // antenna style, sat-dish presence, awning palette, etc., so two
        // same-archetype buildings carry different details.
        const propVariant = (b.seed >>> 12) & 7;     // 0..7
        const accent4 = [PALETTE.neon.cyan, PALETTE.neon.pink, PALETTE.neon.amber, PALETTE.neon.violet];
        const accentA = accent4[propVariant & 3];
        const accentB = accent4[(propVariant + 1) & 3];

        // v11 — roof clutter pass. The reference packs roofs with dense
        // small structures: AC arrays (clusters of 3-5 units in a row),
        // water tanks (round dark with bright caps), connecting pipes,
        // multiple antennas. Buildings should read as "industrial roof"
        // not "flat plain".

        // v14 — AC ARRAY scaled up. 3-5 dark units each 6×6 with a 2-px
        // gap, vent-grille pattern visible on top of each unit. The
        // ventilation array reads clearly from default zoom now.
        const acRowCount = Math.min(5, Math.max(3, Math.floor(b.w / 16)));
        const acRowOriginX = x + 6 + ((b.seed >>> 10) & 7);
        const acRowOriginY = y + Math.floor(b.h * 0.65) + ((b.seed >>> 11) & 3);
        if (acRowOriginY + 8 < y + b.h - 4) {
            for (let i = 0; i < acRowCount; i++) {
                const ax = acRowOriginX + i * 8;
                if (ax + 6 > x + b.w - 4) break;
                // Bright body so AC units pop on darker roof slabs.
                ctx.fillStyle = PALETTE.wall.mid;
                ctx.fillRect(ax, acRowOriginY, 6, 6);
                // Vent-grille stripes on top (2 dark horizontal lines).
                ctx.fillStyle = PALETTE.shadow;
                ctx.fillRect(ax + 1, acRowOriginY + 2, 4, 1);
                ctx.fillRect(ax + 1, acRowOriginY + 4, 4, 1);
                // 1-px shadow at the bottom + south edge — physical height cue.
                ctx.fillRect(ax, acRowOriginY + 5, 6, 1);
                ctx.fillRect(ax + 5, acRowOriginY, 1, 6);
                // 1-px highlight on top + west edge.
                ctx.fillStyle = PALETTE.wall.hi;
                ctx.fillRect(ax, acRowOriginY, 6, 1);
                ctx.fillRect(ax, acRowOriginY, 1, 6);
            }
        }

        // v14 — SCATTERED AC — 1-3 additional 6×6 units elsewhere on the
        // roof for "messy real roof" feel.
        const scatteredAc = 1 + ((b.seed >>> 14) & 1);
        for (let i = 0; i < scatteredAc; i++) {
            const ax = x + 6 + Math.floor(rng() * (b.w - 14));
            const ay = y + 6 + Math.floor(rng() * Math.max(1, Math.floor(b.h * 0.55) - 6));
            ctx.fillStyle = PALETTE.wall.mid;
            ctx.fillRect(ax, ay, 6, 6);
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(ax + 1, ay + 2, 4, 1);
            ctx.fillRect(ax + 1, ay + 4, 4, 1);
            ctx.fillRect(ax, ay + 5, 6, 1);
            ctx.fillRect(ax + 5, ay, 1, 6);
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(ax, ay, 6, 1);
            ctx.fillRect(ax, ay, 1, 6);
        }

        // v14 — WATER TANK scaled up. Bigger circular cylinder with a
        // visible top cap and access pipe. Tier ≥ 2 and footprint ≥ 48×48.
        if (b.tier >= 2 && b.w >= 48 && b.h >= 48) {
            const tankBig = b.tier >= 3;
            const tankSize = tankBig ? 12 : 9;
            const tankX = ((b.seed >>> 13) & 1)
                ? (x + b.w - tankSize - 4)
                : (x + 4);
            const tankY = y + 3;
            // Dark cylinder body.
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(tankX, tankY, tankSize, tankSize);
            // Pixel-circle outline.
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(tankX + 1, tankY,            tankSize - 2, 1);
            ctx.fillRect(tankX + 1, tankY + tankSize - 1, tankSize - 2, 1);
            ctx.fillRect(tankX,            tankY + 1, 1, tankSize - 2);
            ctx.fillRect(tankX + tankSize - 1, tankY + 1, 1, tankSize - 2);
            // Bright top cap — water reservoir is lit on top from above.
            ctx.fillStyle = PALETTE.wall.mid;
            ctx.fillRect(tankX + 1, tankY + 1, tankSize - 2, 1);
            // Centre dot — pipe access.
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(tankX + Math.floor(tankSize / 2), tankY + Math.floor(tankSize / 2), 1, 1);
        }

        // PIPE — thin 1-px dark line connecting the AC array to either
        // the water tank or a roof vent. Adds "things are wired together"
        // realism. Drawn on ~50 % of buildings via seed bit 15.
        if (((b.seed >>> 15) & 1) === 0 && b.w >= 20) {
            const pipeY = acRowOriginY - 2;
            if (pipeY > y + 4) {
                ctx.fillStyle = PALETTE.wall.shade;
                ctx.fillRect(acRowOriginX + 1, pipeY, b.w - 8, 1);
                // Bright 1-px highlight on top of the pipe.
                ctx.fillStyle = PALETTE.wall.mid;
                ctx.fillRect(acRowOriginX + 1, pipeY, b.w - 8, 1);
            }
        }

        // VENT STACK — 1×2 dark pipe rising from the roof at a random
        // position on the south half. 1-px lighter cap at top.
        if (((b.seed >>> 16) & 3) !== 0 && b.w >= 18) {
            const vx = x + 4 + Math.floor(rng() * (b.w - 8));
            const vy = y + Math.floor(b.h * 0.40);
            ctx.fillStyle = PALETTE.shadow;
            ctx.fillRect(vx, vy, 2, 3);
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(vx, vy - 1, 2, 1);
        }

        // Optional satellite dish — appears on ~30% of tier ≥ 2 buildings,
        // not on the small ramen-stall/noodle-cart archetypes.
        if (b.tier >= 2 && (propVariant >= 5)
            && b.archetype !== 'ramen-stall'
            && b.archetype !== 'noodle-cart') {
            const dx = x + 3 + Math.floor(rng() * Math.max(1, b.w - 8));
            const dy = y + 3 + Math.floor(rng() * Math.max(1, b.h - 8));
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(dx, dy, 1, 3);
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(dx - 1, dy, 3, 1);
            ctx.fillStyle = accentA;
            ctx.fillRect(dx, dy - 1, 1, 1);
        }

        if (b.archetype === 'office-spire' && b.tier >= 2) {
            // Antenna at one corner — style varies per building.
            const ax = x + b.w - 4;
            const ay = y + 2;
            ctx.fillStyle = PALETTE.wall.hi;
            ctx.fillRect(ax, ay, 1, 4);
            const blink = ((animFrame % 6) < 2);
            const blinkColor = blink ? statusGlow(b.currentFailType) : PALETTE.wall.shade;
            ctx.fillStyle = blinkColor;
            // Cross / dish / single-tip selector.
            if ((propVariant & 3) === 0) {
                // Cross antenna — 3 horizontal pixels through the pole.
                ctx.fillRect(ax - 1, ay + 1, 3, 1);
                ctx.fillRect(ax, ay - 1, 1, 1);
            } else if ((propVariant & 3) === 1) {
                // Dish — small cup at the top.
                ctx.fillStyle = PALETTE.wall.hi;
                ctx.fillRect(ax - 1, ay - 1, 3, 1);
                ctx.fillStyle = blinkColor;
                ctx.fillRect(ax, ay - 2, 1, 1);
            } else {
                // Plain single tip (the legacy look).
                ctx.fillRect(ax, ay - 1, 1, 1);
            }
        }

        if (b.archetype === 'megacorp-tower') {
            // Hologram pad in centre — cyan glow.
            const cx = x + Math.floor(b.w / 2);
            const cy = y + Math.floor(b.h / 2);
            const projBright = signBrightnessForAnim(b.currentFailType, animFrame);
            const projColor = (b.currentFailType !== 2)
                ? dimColor(statusGlow(b.currentFailType), projBright)
                : dimColor(PALETTE.neon.cyan, projBright);
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(cx - 3, cy - 3, 6, 6);
            ctx.fillStyle = projColor;
            ctx.fillRect(cx - 1, cy - 1, 2, 2);
            ctx.globalAlpha = 0.45;
            ctx.fillRect(cx - 2, cy - 2, 4, 4);
            ctx.globalAlpha = 1.0;
        }

        if (b.archetype === 'karaoke-bar') {
            // Lantern row along south edge — colour and count vary per bar.
            const lanternCount = Math.max(2, Math.floor(b.w / 18)) + (propVariant & 1);
            for (let i = 0; i < lanternCount; i++) {
                const lx = x + 4 + Math.floor((b.w - 8) * (i + 0.5) / lanternCount);
                const ly = y + b.h - 3;
                const bri = signBrightnessForAnim(b.currentFailType, animFrame + i * 2);
                ctx.fillStyle = dimColor(accentA, bri);
                ctx.fillRect(lx, ly, 2, 1);
            }
        }

        if (b.archetype === 'ramen-stall') {
            // Awning stripes — per-instance pair drawn from the 4-colour menu
            // so different stalls show different shopfront tints.
            ctx.fillStyle = accentA;
            ctx.fillRect(x + 1, y + b.h - 3, b.w - 2, 1);
            ctx.fillStyle = accentB;
            ctx.fillRect(x + 1, y + b.h - 4, b.w - 2, 1);
            // Steam vent dot at NE corner.
            const sx = x + b.w - 4;
            const sy = y + 2;
            const puff = (animFrame % 6) < 3;
            ctx.fillStyle = '#d9d4e6';
            ctx.globalAlpha = puff ? 0.55 : 0.30;
            ctx.fillRect(sx, sy, 2, 2);
            ctx.fillRect(sx - 1, sy + 1, 1, 1);
            ctx.globalAlpha = 1.0;
            // Hanging lantern at SW corner.
            ctx.fillStyle = dimColor(PALETTE.neon.pink,
                signBrightnessForAnim(b.currentFailType, animFrame));
            ctx.fillRect(x + 2, y + b.h - 6, 2, 1);
            // Archetype tag — "RAMEN" in amber along the top edge (issue #23).
            drawArchetypeTag(ctx, b, x, y, 'RAMEN', PALETTE.neon.amber, animFrame);
        }

        if (b.archetype === 'hostel') {
            // Three balcony rails along the south face — implies stacked levels.
            ctx.fillStyle = PALETTE.neon.cyan;
            for (let i = 0; i < 3; i++) {
                ctx.fillRect(x + 2, y + b.h - 4 + i * 0, b.w - 4, 1);
                // Wait — overlapping. Use separate y offsets:
            }
            // Actually do it right with distinct rails:
            ctx.fillStyle = PALETTE.hostel ? '#5a8aa2' : PALETTE.neon.cyan;
            for (let row = 0; row < 3; row++) {
                const ry = y + b.h - 5 + row * 1;   // 3 px tall band
                if (ry >= y && ry < y + b.h) {
                    ctx.fillStyle = (row === 1) ? PALETTE.neon.cyan : PALETTE.wall.hi;
                    ctx.fillRect(x + 2, ry, b.w - 4, 1);
                }
            }
            // Archetype tag — "HOSTEL" in cyan along the top edge (issue #23,
            // replacing the previous 1-px cyan strip that hinted at the label).
            drawArchetypeTag(ctx, b, x, y, 'HOSTEL', PALETTE.neon.cyan, animFrame);
        }

        if (b.archetype === 'arcade') {
            // Triangular marquee at south edge — arrow colour varies per arcade.
            const ay = y + b.h - 6;
            const ax = x + Math.floor(b.w / 2);
            const bri = signBrightnessForAnim(b.currentFailType, animFrame);
            const arrowColor = (propVariant & 2) ? PALETTE.neon.magenta : PALETTE.neon.violet;
            ctx.fillStyle = dimColor(arrowColor, bri);
            // Arrow body (triangle drawn as stacked rows).
            for (let i = 0; i < 3; i++) {
                const half = 3 - i;
                ctx.fillRect(ax - half, ay + i, half * 2 + 1, 1);
            }
            // Marquee bulbs flanking the arrow — colour varies too.
            ctx.fillStyle = dimColor(accentA,
                signBrightnessForAnim(b.currentFailType, animFrame + 2));
            ctx.fillRect(x + 2, ay + 1, 1, 1);
            ctx.fillRect(x + b.w - 3, ay + 1, 1, 1);
            // Archetype tag — "ARCADE" in magenta along the top edge (issue #23).
            drawArchetypeTag(ctx, b, x, y, 'ARCADE', arrowColor, animFrame);
        }

        if (b.archetype === 'noodle-cart') {
            // Two wheels at south edge (cart on wheels).
            ctx.fillStyle = PALETTE.wall.shade;
            ctx.fillRect(x + 1, y + b.h - 1, 2, 1);
            ctx.fillRect(x + b.w - 3, y + b.h - 1, 2, 1);
            // Chimney at NE corner with smoke puff.
            ctx.fillStyle = PALETTE.steel ? PALETTE.steel.mid : '#4a4f5c';
            ctx.fillRect(x + b.w - 3, y + 1, 1, 3);
            const puff = (animFrame % 4) < 2;
            ctx.fillStyle = '#aaa';
            ctx.globalAlpha = puff ? 0.55 : 0.30;
            ctx.fillRect(x + b.w - 3, y, 1, 1);
            ctx.fillRect(x + b.w - 2, y - 1, 1, 1);
            ctx.globalAlpha = 1.0;
        }

        // v12 — TOWER CLUTTER PASS. Tier-3+ buildings get the "busy
        // industrial roof" treatment from the reference: 2-3 antenna stalks
        // of varying heights, an extra water tank cluster, and a small
        // rising billboard on supports. Without this towers read as plain
        // slabs even after the typology shape pass.
        if (b.tier >= 3 && b.w >= 30 && b.h >= 30) {
            const antennaCount = 2 + ((b.seed >>> 9) & 1);          // 2 or 3 antennas
            for (let i = 0; i < antennaCount; i++) {
                const ax = x + 4 + ((b.seed >>> (i * 3 + 5)) & 7) +
                           Math.floor((b.w - 12) * (i / Math.max(1, antennaCount)));
                if (ax >= x + b.w - 3) break;
                const ay = y + 6 + ((b.seed >>> (i * 3 + 7)) & 3);
                const aLen = 5 + ((b.seed >>> (i * 3 + 9)) & 3);
                // Stalk.
                ctx.fillStyle = PALETTE.wall.hi;
                ctx.fillRect(ax, ay - aLen, 1, aLen);
                // Cross-piece on some antennas — turns single stalk into
                // a small antenna tower.
                if (i === 1) {
                    ctx.fillRect(ax - 1, ay - aLen + 2, 3, 1);
                }
                // Blinking tip light — uses status colour so failing
                // buildings strobe red.
                const blink = ((animFrame + i * 2) % 7) < 3;
                ctx.fillStyle = blink ? statusGlow(b.currentFailType) : PALETTE.wall.shade;
                ctx.fillRect(ax, ay - aLen - 1, 1, 1);
            }

            // Second water tank (independent of the typology's tank).
            if (b.w >= 36 && b.h >= 36) {
                const t2x = x + Math.floor(b.w * 0.55);
                const t2y = y + Math.floor(b.h * 0.40);
                ctx.fillStyle = PALETTE.shadow;
                ctx.fillRect(t2x, t2y, 5, 5);
                ctx.fillStyle = PALETTE.wall.hi;
                ctx.fillRect(t2x + 1, t2y, 3, 1);
                ctx.fillRect(t2x + 1, t2y + 4, 3, 1);
                ctx.fillRect(t2x, t2y + 1, 1, 3);
                ctx.fillRect(t2x + 4, t2y + 1, 1, 3);
                ctx.fillStyle = PALETTE.wall.mid;
                ctx.fillRect(t2x + 1, t2y + 1, 3, 1);
            }

            // Rising billboard — small sign body raised above the roof on
            // 2 thin supports, the "rooftop neon" silhouette dominant in
            // the reference image. Picks colour from accentA + applies the
            // sign-brightness curve so it pulses on failures.
            if (b.w >= 28) {
                const bw = Math.min(14, Math.floor(b.w * 0.5));
                const bh = 4;
                const bx = x + Math.floor((b.w - bw) / 2);
                const by = y + 2;                              // sits ON the roof
                // 2 support struts.
                ctx.fillStyle = PALETTE.wall.shade;
                ctx.fillRect(bx + 2,        by + bh, 1, 2);
                ctx.fillRect(bx + bw - 3,   by + bh, 1, 2);
                // Backboard.
                ctx.fillStyle = PALETTE.shadow;
                ctx.fillRect(bx, by, bw, bh);
                // Neon body — colour drives off seed bits 22-23 so adjacent
                // towers get different colours.
                const bbColor = accent4[(b.seed >>> 22) & 3];
                const bbBri = signBrightnessForAnim(b.currentFailType, animFrame);
                ctx.fillStyle = dimColor(bbColor, Math.min(0.85, bbBri));
                ctx.fillRect(bx + 1, by + 1, bw - 2, bh - 2);
            }
        }
    }

    // v11 — sign placement dispatch. Each building picks 1 of 5 sign
    // positions by seed bits 3-5, so two adjacent same-archetype buildings
    // get *visually different signs in different places*, matching the
    // reference where every building carries a unique sign style.
    function renderRoofSign(ctx, b, x, y, animFrame) {
        const title = normaliseTitle(b.title).slice(0, 14);
        if (!title) return;
        const placement = (b.seed >>> 3) & 7;
        const bri = signBrightnessForAnim(b.currentFailType, animFrame);
        const colorBase = (b.seed % 2 === 0) ? PALETTE.neon.pink : PALETTE.neon.cyan;
        const color = (b.currentFailType === 3 && bri < 0.2)
            ? PALETTE.shadow
            : dimColor(colorBase, bri);

        if (placement === 2 && b.w >= 24) {
            renderSignSouthExtruded(ctx, b, x, y, title, color, bri);
        } else if (placement === 3 && b.h >= 28) {
            renderSignEastVertical(ctx, b, x, y, title, color, bri);
        } else if (placement === 4 && b.w >= 28) {
            renderSignRoofBillboard(ctx, b, x, y, title, color, bri);
        } else if (placement === 5 && b.w >= 24) {
            renderSignRoofCornerNE(ctx, b, x, y, title, color, bri);
        } else {
            // Default — roof-centre. Used for placements 0-1, 6-7, and as
            // the fallback when the chosen placement doesn't fit the
            // building's footprint.
            renderSignRoofCenter(ctx, b, x, y, title, color, bri);
        }
    }

    // The legacy roof-centred sign — bitmap title in a neon-outlined dark
    // backboard, centred on the roof. ~40 % of buildings.
    function renderSignRoofCenter(ctx, b, x, y, title, color, bri) {
        const maxLineW = b.w - 6;
        const maxCharsPerLine = Math.max(1, Math.floor((maxLineW + 1) / FONT5x7_ADVANCE));
        const lines = wrapBitmapText(title, maxCharsPerLine, 2);
        if (!lines.length) return;
        const lineWidths = lines.map(textWidth);
        const widest = Math.max.apply(null, lineWidths);
        const sw = Math.min(maxLineW, widest) + 4;
        const sh = lines.length * 8 + 1;
        const sx = x + Math.floor((b.w - sw) / 2);
        const sy = y + Math.floor((b.h - sh) / 2);
        ctx.fillStyle = PALETTE.wall.shade;
        ctx.fillRect(sx, sy, sw, sh);
        if (bri > 0.2) {
            ctx.fillStyle = color;
            ctx.fillRect(sx, sy, sw, 1);
            ctx.fillRect(sx, sy + sh - 1, sw, 1);
            ctx.fillRect(sx, sy, 1, sh);
            ctx.fillRect(sx + sw - 1, sy, 1, sh);
        }
        for (let li = 0; li < lines.length; li++) {
            const lw = lineWidths[li];
            const lx = sx + Math.floor((sw - lw) / 2);
            const ly = sy + 1 + li * 8;
            drawText(ctx, lines[li], lx, ly, color);
        }
    }

    // South-extruded hanging shop sign — protrudes 1-2 px below the south
    // wall band, hanging from supports. Mimics the "?" / "RAMEN" /
    // "GUNS" hanging signs visible all along the bottom of tile-B-01.
    function renderSignSouthExtruded(ctx, b, x, y, title, color, bri) {
        const wallH = (typeof b.wallH === 'number') ? b.wallH : 10;
        // Sign body — wide enough for first ~5 chars of title.
        const short = title.slice(0, Math.min(7, title.length));
        const tw = textWidth(short);
        if (tw + 4 > b.w - 2) return renderSignRoofCenter(ctx, b, x, y, title, color, bri);
        const sw = tw + 4;
        const sh = 9;
        // Drawn relative to the canvas coordinates *south* of the building
        // roof (roof y-range is 0..b.h relative to (x,y), wall band lives
        // at y+b.h..y+b.h+wallH, extruded sign south of that).
        const sx = x + Math.floor((b.w - sw) / 2);
        const sy = y + b.h + wallH;       // sits below wall band
        // Two short support struts coming out of the wall band.
        ctx.fillStyle = PALETTE.wall.hi;
        ctx.fillRect(sx + 1,      sy - 1, 1, 1);
        ctx.fillRect(sx + sw - 2, sy - 1, 1, 1);
        // Backboard + neon frame.
        ctx.fillStyle = PALETTE.wall.shade;
        ctx.fillRect(sx, sy, sw, sh);
        if (bri > 0.2) {
            ctx.fillStyle = color;
            ctx.fillRect(sx, sy, sw, 1);
            ctx.fillRect(sx, sy + sh - 1, sw, 1);
            ctx.fillRect(sx, sy, 1, sh);
            ctx.fillRect(sx + sw - 1, sy, 1, sh);
        }
        const lx = sx + Math.floor((sw - tw) / 2);
        const ly = sy + 1;
        drawText(ctx, short, lx, ly, color);
    }

    // East-edge vertical sign — letters stacked vertically down the east
    // wall of the roof. First 2-3 chars only. Mimics the "NEO" vertical
    // neon strip clearly visible at the left edge of tile-B-01.
    function renderSignEastVertical(ctx, b, x, y, title, color, bri) {
        // 2 to 3 letters fit comfortably in a vertical stack.
        const chars = title.replace(/\s+/g, '').slice(0, 3);
        if (!chars.length) return;
        const letterH = 7;
        const letterW = 5;
        const stackH = chars.length * (letterH + 1);
        const sw = letterW + 4;
        const sh = stackH + 4;
        if (sh > b.h - 4) return renderSignRoofCenter(ctx, b, x, y, title, color, bri);
        const sx = x + b.w - sw - 2;        // 2 px from east edge
        const sy = y + Math.floor((b.h - sh) / 2);
        // Backboard.
        ctx.fillStyle = PALETTE.wall.shade;
        ctx.fillRect(sx, sy, sw, sh);
        // Neon outline.
        if (bri > 0.2) {
            ctx.fillStyle = color;
            ctx.fillRect(sx, sy, sw, 1);
            ctx.fillRect(sx, sy + sh - 1, sw, 1);
            ctx.fillRect(sx, sy, 1, sh);
            ctx.fillRect(sx + sw - 1, sy, 1, sh);
        }
        // Letters stacked top to bottom.
        for (let i = 0; i < chars.length; i++) {
            const lx = sx + Math.floor((sw - letterW) / 2);
            const ly = sy + 2 + i * (letterH + 1);
            drawText(ctx, chars[i], lx, ly, color);
        }
    }

    // Roof-mounted billboard rising on supports above the roof. The
    // billboard sits ABOVE the roof footprint by 3-4 px on two small
    // struts. Mimics the rooftop billboards visible all over the
    // top half of tile-B-01.
    function renderSignRoofBillboard(ctx, b, x, y, title, color, bri) {
        const short = title.slice(0, Math.min(7, title.length));
        const tw = textWidth(short);
        if (tw + 4 > b.w - 4) return renderSignRoofCenter(ctx, b, x, y, title, color, bri);
        const sw = tw + 4;
        const sh = 9;
        const sx = x + Math.floor((b.w - sw) / 2);
        const sy = y - sh - 1;             // sits 1 px above roof
        // Struts.
        ctx.fillStyle = PALETTE.wall.hi;
        ctx.fillRect(sx + 1,       sy + sh, 1, 1);
        ctx.fillRect(sx + sw - 2,  sy + sh, 1, 1);
        // Drop shadow on the roof under the billboard.
        ctx.fillStyle = PALETTE.shadow;
        ctx.globalAlpha = 0.40;
        ctx.fillRect(sx + 2, sy + sh + 1, sw - 4, 1);
        ctx.globalAlpha = 1.0;
        // Backboard + neon frame.
        ctx.fillStyle = PALETTE.wall.shade;
        ctx.fillRect(sx, sy, sw, sh);
        if (bri > 0.2) {
            ctx.fillStyle = color;
            ctx.fillRect(sx, sy, sw, 1);
            ctx.fillRect(sx, sy + sh - 1, sw, 1);
            ctx.fillRect(sx, sy, 1, sh);
            ctx.fillRect(sx + sw - 1, sy, 1, sh);
        }
        const lx = sx + Math.floor((sw - tw) / 2);
        const ly = sy + 1;
        drawText(ctx, short, lx, ly, color);
    }

    // NE-corner small sign — 3-letter abbreviation in a small box at the
    // NE corner of the roof. Subtle, doesn't dominate the building. The
    // most common variety in the reference's tight storefront row.
    function renderSignRoofCornerNE(ctx, b, x, y, title, color, bri) {
        const abbrev = title.replace(/\s+/g, '').slice(0, 3);
        if (!abbrev.length) return;
        const tw = textWidth(abbrev);
        const sw = tw + 4;
        const sh = 9;
        const sx = x + b.w - sw - 2;
        const sy = y + 2;
        ctx.fillStyle = PALETTE.wall.shade;
        ctx.fillRect(sx, sy, sw, sh);
        if (bri > 0.2) {
            ctx.fillStyle = color;
            ctx.fillRect(sx, sy, sw, 1);
            ctx.fillRect(sx, sy + sh - 1, sw, 1);
            ctx.fillRect(sx, sy, 1, sh);
            ctx.fillRect(sx + sw - 1, sy, 1, sh);
        }
        const lx = sx + Math.floor((sw - tw) / 2);
        const ly = sy + 1;
        drawText(ctx, abbrev, lx, ly, color);
    }

    function buildingHasLiveFX(b) {
        if (isReducedMotion()) return false;
        if (b.currentFailType === 1 || b.currentFailType === 3) return true;
        if (b.currentFailType === 2 && b.health === 4) return true;
        return false;
    }

    // =========================================================
    //                     MAIN RAF LOOP
    // =========================================================
    function drawFrame() {
        if (!state.running || !state.ctx) return;
        const t0 = performance.now();
        const { ctx, canvas } = state;
        ctx.imageSmoothingEnabled = false;
        const W = canvas.width;
        const H = canvas.height;
        const camX = Math.floor(state.camX);
        const camY = Math.floor(state.camY);

        // 1. Clear with the void colour so the visible page never flashes
        //    a non-palette tone.
        ctx.fillStyle = PALETTE.ground.void;
        ctx.fillRect(0, 0, W, H);

        // 2. Ground (street + plaza + sidewalk + base) blitted as a single
        //    pre-rendered city canvas, sliced to the viewport.
        if (state.ground) {
            // Source rect = whatever portion of the city falls in the viewport.
            const srcX = Math.max(0, camX);
            const srcY = Math.max(0, camY);
            const srcW = Math.min(state.ground.width - srcX, W);
            const srcH = Math.min(state.ground.height - srcY, H);
            if (srcW > 0 && srcH > 0) {
                ctx.drawImage(
                    state.ground,
                    srcX, srcY, srcW, srcH,
                    Math.max(0, -camX), Math.max(0, -camY), srcW, srcH);
            }
        }

        // 3. Buildings — viewport-culled blit. Cull margin uses the building's
        //    cached canvas height (b.h + wallH + 2×PAD), not a fixed +8, so
        //    tier-4 towers with tall wall bands don't get clipped when their
        //    roof is just above the top of the viewport (Hermes review #174).
        let visible = 0;
        for (const b of state.buildings) {
            // v11 — PAD bumped to 12 in renderBuilding (was 5) for the
            // south-extruded sign + roof billboard envelopes. Cull margin
            // tracks the cached canvas size exactly so signs that extend
            // outside the roof footprint don't get cull-clipped.
            const screenX = b.x - camX - 12;
            const screenY = b.y - camY - 12;
            const wallExtent = (typeof b.wallH === 'number') ? b.wallH : 7;
            const canvasH = b.h + wallExtent + 24;
            const canvasW = b.w + 24;
            if (screenX + canvasW < 0 || screenX > W) continue;
            if (screenY + canvasH < 0 || screenY > H) continue;
            visible++;
            if (b.canvas) ctx.drawImage(b.canvas, screenX, screenY);
        }

        // 3b. v13 — skywalks between adjacent tier-3+ tower roofs. Drawn
        //     after buildings so the bridges sit visually on top of the
        //     street + below the NPC overlay.
        renderSkywalks(ctx, state.skywalks, camX, camY, W, H, state.animFrame | 0);

        // 3c. v21 — moving vehicles on roads. Drawn after buildings but
        //     before NPCs so cars pass UNDER pedestrians at corners
        //     (NPCs are crossing roads).
        renderVehicles(ctx, camX, camY, W, H);

        // 4. NPC overlay (issue #24) — walks each animTick, drawn fresh
        //    every RAF on top of buildings so motion is visible.
        renderNpcs(ctx, state.animFrame | 0, camX, camY, W, H);

        // 5. Ambient particle overlay (issue #25) — steam, dust, lens
        //    flare on lampposts. Reduced-motion variants handled inside.
        renderParticles(ctx, state.animFrame | 0, camX, camY, W, H);

        // 6. Tier-4 holographic projections (issue #26) — small cyan
        //    glyphs floating above landmark buildings. Status-tinted
        //    + strobed when the check is failing.
        renderHolograms(ctx, state.animFrame | 0, camX, camY, W, H);

        // 7. Construction frames for brand-new checks (issue #27) —
        //    tarp + scaffold poles + sparks + "NEW" banner, disassembled
        //    progressively over the first 7 days.
        renderConstructionFrames(ctx, state.animFrame | 0, camX, camY, W, H);

        state.diag.visibleBuildings = visible;
        state.diag.lastFrameMs = performance.now() - t0;
        state.diag.framesDrawn++;
        state.rafId = requestAnimationFrame(drawFrame);
    }

    // =========================================================
    //                  PAN / ZOOM / HIT-TEST
    // =========================================================
    function clampCam() {
        if (!state.canvas) return;
        const maxX = Math.max(0, state.cityWidth - state.canvas.width);
        const maxY = Math.max(0, state.cityHeight - state.canvas.height);
        if (state.camX < 0) state.camX = 0;
        if (state.camY < 0) state.camY = 0;
        if (state.camX > maxX) state.camX = maxX;
        if (state.camY > maxY) state.camY = maxY;
    }

    function clientToGameCoords(ev) {
        const rect = state.canvas.getBoundingClientRect();
        const cssX = ev.clientX - rect.left;
        const cssY = ev.clientY - rect.top;
        return {
            x: Math.floor(cssX / scale()),
            y: Math.floor(cssY / scale()),
        };
    }

    function onMouseDown(ev) {
        if (ev.button !== 0) return;
        state.isDragging = true;
        state.dragMoved = false;
        state.dragStartClientX = ev.clientX;
        state.dragStartClientY = ev.clientY;
        state.dragStartCamX = state.camX;
        state.dragStartCamY = state.camY;
    }

    function onMouseMove(ev) {
        if (!state.isDragging) return;
        const dx = ev.clientX - state.dragStartClientX;
        const dy = ev.clientY - state.dragStartClientY;
        if (Math.abs(dx) > 3 || Math.abs(dy) > 3) state.dragMoved = true;
        state.camX = Math.floor(state.dragStartCamX - dx / scale());
        state.camY = Math.floor(state.dragStartCamY - dy / scale());
        clampCam();
    }

    function onMouseUp() { state.isDragging = false; }

    function onClick(ev) {
        if (state.dragMoved) return;
        if (!state.canvas) return;
        const p = clientToGameCoords(ev);
        const worldX = p.x + Math.floor(state.camX);
        const worldY = p.y + Math.floor(state.camY);
        for (const b of state.buildings) {
            if (worldX >= b.x && worldX < b.x + b.w
                && worldY >= b.y && worldY < b.y + b.h) {
                if (state.dotnetRef) {
                    state.dotnetRef.invokeMethodAsync('OnBuildingClicked', b.id);
                }
                return;
            }
        }
    }

    function onWheel(ev) {
        ev.preventDefault();
        if (ev.deltaY < 0) doZoomIn();
        else if (ev.deltaY > 0) doZoomOut();
    }

    // ---------------------------------------------------------
    //   Touch input — single-finger pan + two-finger pinch zoom
    //   so the grid is usable on mobile / tablet without a mouse.
    // ---------------------------------------------------------
    function onTouchStart(ev) {
        if (!state.canvas) return;
        if (ev.touches.length === 1) {
            // Begin pan — mirror onMouseDown.
            state.isDragging = true;
            state.dragMoved = false;
            state.dragStartClientX = ev.touches[0].clientX;
            state.dragStartClientY = ev.touches[0].clientY;
            state.dragStartCamX = state.camX;
            state.dragStartCamY = state.camY;
            state.pinchStartDist = 0;
            // Remember tap-start for a click-equivalent on touchend.
            state.tapStartClientX = ev.touches[0].clientX;
            state.tapStartClientY = ev.touches[0].clientY;
            state.tapStartTime = (performance.now ? performance.now() : Date.now());
            ev.preventDefault();
        } else if (ev.touches.length === 2) {
            // Begin pinch — stop any pan in progress, kill the pending tap
            // (so when the second finger lifts at the end of the pinch it
            // doesn't fire OnBuildingClicked on whatever's under it —
            // Hermes review #178), record starting distance.
            state.isDragging = false;
            state.dragMoved = true;
            state.tapStartTime = 0;
            state.pinchStartDist = touchDistance(ev.touches[0], ev.touches[1]);
            state.pinchStartZoomIndex = state.zoomIndex;
            ev.preventDefault();
        }
    }

    function onTouchMove(ev) {
        if (!state.canvas) return;
        if (ev.touches.length === 1 && state.isDragging) {
            const t = ev.touches[0];
            const dx = t.clientX - state.dragStartClientX;
            const dy = t.clientY - state.dragStartClientY;
            if (Math.abs(dx) > 6 || Math.abs(dy) > 6) state.dragMoved = true;
            state.camX = Math.floor(state.dragStartCamX - dx / scale());
            state.camY = Math.floor(state.dragStartCamY - dy / scale());
            clampCam();
            ev.preventDefault();
        } else if (ev.touches.length === 2 && state.pinchStartDist > 0) {
            const dist = touchDistance(ev.touches[0], ev.touches[1]);
            const ratio = dist / state.pinchStartDist;
            // Map the continuous ratio to the discrete ZOOM_LEVELS array.
            // Pinch out (>1.20) bumps zoom in; pinch in (<0.83) zooms out.
            let target = state.pinchStartZoomIndex;
            if (ratio > 1.20) target = Math.min(ZOOM_LEVELS.length - 1,
                state.pinchStartZoomIndex + Math.floor(Math.log(ratio) / Math.log(1.20)));
            else if (ratio < 0.83) target = Math.max(0,
                state.pinchStartZoomIndex - Math.floor(Math.log(1 / ratio) / Math.log(1.20)));
            if (target !== state.zoomIndex) {
                state.zoomIndex = target;
                resizeCanvas();
            }
            ev.preventDefault();
        }
    }

    function onTouchEnd(ev) {
        if (!state.canvas) return;
        // touchcancel never fires a tap.
        const isCancel = ev.type === 'touchcancel';
        // If a single tap landed without dragging or pinching, treat as a
        // click on the first building under the released touch point.
        if (!isCancel && !state.dragMoved
            && ev.changedTouches && ev.changedTouches.length === 1
            && state.tapStartTime
            && (performance.now ? performance.now() : Date.now()) - state.tapStartTime < 400) {
            const t = ev.changedTouches[0];
            const rect = state.canvas.getBoundingClientRect();
            const cssX = t.clientX - rect.left;
            const cssY = t.clientY - rect.top;
            const gx = Math.floor(cssX / scale());
            const gy = Math.floor(cssY / scale());
            const worldX = gx + Math.floor(state.camX);
            const worldY = gy + Math.floor(state.camY);
            for (const b of state.buildings) {
                if (worldX >= b.x && worldX < b.x + b.w
                    && worldY >= b.y && worldY < b.y + b.h) {
                    if (state.dotnetRef) {
                        state.dotnetRef.invokeMethodAsync('OnBuildingClicked', b.id);
                    }
                    break;
                }
            }
        }
        state.isDragging = false;
        state.pinchStartDist = 0;
        state.tapStartTime = 0;
    }

    function touchDistance(a, b) {
        const dx = a.clientX - b.clientX;
        const dy = a.clientY - b.clientY;
        return Math.hypot(dx, dy);
    }

    function doZoomIn() {
        if (state.zoomIndex < ZOOM_LEVELS.length - 1) {
            state.zoomIndex++;
            resizeCanvas();
        }
    }
    function doZoomOut() {
        if (state.zoomIndex > 0) {
            state.zoomIndex--;
            resizeCanvas();
        }
    }

    function resizeCanvas() {
        const c = state.canvas;
        if (!c) return;
        const rect = c.parentElement.getBoundingClientRect();
        const s = scale();
        const nw = Math.max(64, Math.floor(rect.width / s));
        const nh = Math.max(64, Math.floor(rect.height / s));
        c.width = nw;
        c.height = nh;
        c.style.width = (nw * s) + 'px';
        c.style.height = (nh * s) + 'px';
        c.style.imageRendering = 'pixelated';
        state.ctx = c.getContext('2d');
        state.ctx.imageSmoothingEnabled = false;
        clampCam();
    }

    function onVisibility() {
        if (document.hidden) {
            state.running = false;
            if (state.rafId) { cancelAnimationFrame(state.rafId); state.rafId = null; }
            stopAnimTick();
        } else if (state.canvas) {
            state.running = true;
            drawFrame();
            ensureAnimTick();
        }
    }

    function onKeyDown(ev) {
        if (!state.canvas) return;
        const step = TILE * 2;
        switch (ev.key) {
            case 'ArrowLeft':  state.camX -= step; clampCam(); ev.preventDefault(); break;
            case 'ArrowRight': state.camX += step; clampCam(); ev.preventDefault(); break;
            case 'ArrowUp':    state.camY -= step; clampCam(); ev.preventDefault(); break;
            case 'ArrowDown':  state.camY += step; clampCam(); ev.preventDefault(); break;
            case '+': case '=': doZoomIn(); ev.preventDefault(); break;
            case '-': case '_': doZoomOut(); ev.preventDefault(); break;
        }
    }

    // =========================================================
    //              8 fps ANIMATION TICK
    // =========================================================
    function animTick() {
        if (!state.running) return;
        if (isReducedMotion()) { stopAnimTick(); return; }
        state.animFrame = (state.animFrame + 1) | 0;
        state.diag.animTicks++;
        const animFrame = state.animFrame;
        for (const b of state.buildings) {
            if (!buildingHasLiveFX(b)) continue;
            b.canvas = renderBuilding(b, animFrame);
        }
        // Walk NPCs along their loops (issue #24).
        tickNpcs();
        // v21 — advance moving vehicles along their road segments.
        tickVehicles();
        // Drift dust motes upward (issue #25).
        tickParticles();
    }

    function ensureAnimTick() {
        if (state.animTimer || !state.canvas) return;
        if (isReducedMotion()) return;
        const hasLiveBuildings = state.buildings.some(buildingHasLiveFX);
        const hasNpcs = state.npcs && state.npcs.length > 0;
        const hasParticles = state.particles
            && ((state.particles.steamVents && state.particles.steamVents.length > 0)
                || (state.particles.motes && state.particles.motes.length > 0));
        if (!hasLiveBuildings && !hasNpcs && !hasParticles) return;
        state.animTimer = setInterval(animTick, ANIM_TICK_MS);
    }

    function stopAnimTick() {
        if (state.animTimer) { clearInterval(state.animTimer); state.animTimer = null; }
    }

    // =========================================================
    //                       PUBLIC API
    // =========================================================
    window.SuperStatusGrid = {
        init(canvasEl, dotnetRef) {
            state.canvas = canvasEl;
            state.dotnetRef = dotnetRef;

            resizeCanvas();

            state.resizeObserver = new ResizeObserver(() => resizeCanvas());
            state.resizeObserver.observe(canvasEl.parentElement);

            state.handlers = {
                mousedown: onMouseDown, mousemove: onMouseMove,
                mouseup:   onMouseUp,   mouseleave: onMouseUp,
                click:     onClick,     wheel: onWheel,
                keydown:   onKeyDown,
                touchstart: onTouchStart, touchmove: onTouchMove,
                touchend:   onTouchEnd,  touchcancel: onTouchEnd,
            };
            canvasEl.addEventListener('mousedown', state.handlers.mousedown);
            canvasEl.addEventListener('mousemove', state.handlers.mousemove);
            canvasEl.addEventListener('mouseup',   state.handlers.mouseup);
            canvasEl.addEventListener('mouseleave',state.handlers.mouseleave);
            canvasEl.addEventListener('click',     state.handlers.click);
            canvasEl.addEventListener('wheel',     state.handlers.wheel, { passive: false });
            canvasEl.addEventListener('keydown',   state.handlers.keydown);
            // Touch handlers must be `passive: false` so preventDefault works
            // and the page doesn't pan/zoom while the user is dragging the
            // grid.
            canvasEl.addEventListener('touchstart',  state.handlers.touchstart,  { passive: false });
            canvasEl.addEventListener('touchmove',   state.handlers.touchmove,   { passive: false });
            canvasEl.addEventListener('touchend',    state.handlers.touchend,    { passive: false });
            canvasEl.addEventListener('touchcancel', state.handlers.touchcancel, { passive: false });
            // Also disable the iOS double-tap-to-zoom behaviour on the canvas.
            canvasEl.style.touchAction = 'none';
            document.addEventListener('visibilitychange', onVisibility);

            state.running = true;
            drawFrame();
        },

        setBuildings(buildingDtos) {
            const layout = layoutCity(buildingDtos || []);
            const wasEmpty = state.buildings.length === 0;
            state.buildings = layout.positioned;
            const animFrame = state.animFrame | 0;
            let count = 0;
            for (const b of state.buildings) {
                b.canvas = renderBuilding(b, animFrame);
                count++;
            }
            state.diag.buildingsRendered = count;
            state.lampposts = generateLamppostPositions(layout);
            state.skywalks = generateSkywalks(layout.positioned);     // v13 — bridges
            state.vehicles = generateVehicles(layout);                // v21 — moving traffic
            state.ground = renderGround(layout);
            state.npcs = generateNpcs(layout);
            state.particles = generateParticles(layout);
            // On the FIRST setBuildings call (canvas just mounted), centre
            // the camera on the city so the user opens /grid and sees
            // buildings, not just street margins.
            if (wasEmpty && state.canvas) {
                state.camX = Math.max(0,
                    Math.floor((state.cityWidth - state.canvas.width) / 2));
                state.camY = Math.max(0,
                    Math.floor((state.cityHeight - state.canvas.height) / 2));
            }
            clampCam();
            ensureAnimTick();
        },

        updateBuilding(id, partial) {
            const idx = state.buildings.findIndex(x => x.id === id);
            if (idx < 0) return;
            const b = state.buildings[idx];
            if (typeof partial.ageDays === 'number') b.ageDays = partial.ageDays;
            if (typeof partial.uptime30d === 'number') b.uptime30d = partial.uptime30d;
            if (typeof partial.uptime7d === 'number') b.uptime7d = partial.uptime7d;
            if (typeof partial.currentFailType === 'number') b.currentFailType = partial.currentFailType;
            if (typeof partial.consecutiveFailures === 'number') b.consecutiveFailures = partial.consecutiveFailures;
            if (partial.lastCheckUtc) b.lastCheckIso = partial.lastCheckUtc;
            const prevTier = b.tier;
            b.tier = computeTier(b.ageDays, b.uptime30d);
            b.health = computeHealth(b.uptime7d);
            b.canvas = renderBuilding(b, state.animFrame | 0);
            // v13 — if the tier crossed the skywalk eligibility threshold
            // (3), regenerate state.skywalks so new bridges appear and old
            // ones disappear under live polling (Hermes review #207).
            if ((prevTier < 3) !== (b.tier < 3)) {
                state.skywalks = generateSkywalks(state.buildings);
            }
            ensureAnimTick();
        },

        zoomIn: doZoomIn,
        zoomOut: doZoomOut,

        destroy() {
            state.running = false;
            if (state.rafId) cancelAnimationFrame(state.rafId);
            stopAnimTick();
            if (state.resizeObserver) state.resizeObserver.disconnect();
            if (state.canvas && state.handlers) {
                state.canvas.removeEventListener('mousedown', state.handlers.mousedown);
                state.canvas.removeEventListener('mousemove', state.handlers.mousemove);
                state.canvas.removeEventListener('mouseup',   state.handlers.mouseup);
                state.canvas.removeEventListener('mouseleave',state.handlers.mouseleave);
                state.canvas.removeEventListener('click',     state.handlers.click);
                state.canvas.removeEventListener('wheel',     state.handlers.wheel);
                state.canvas.removeEventListener('keydown',   state.handlers.keydown);
                state.canvas.removeEventListener('touchstart',  state.handlers.touchstart);
                state.canvas.removeEventListener('touchmove',   state.handlers.touchmove);
                state.canvas.removeEventListener('touchend',    state.handlers.touchend);
                state.canvas.removeEventListener('touchcancel', state.handlers.touchcancel);
            }
            document.removeEventListener('visibilitychange', onVisibility);
            state.canvas = null; state.ctx = null; state.dotnetRef = null;
            state.buildings = [];
            state.ground = null;
            state.npcs = [];
            state.lampposts = [];
            state.particles = null;
            state.cityWidth = 0; state.cityHeight = 0;
            state.rafId = null; state.resizeObserver = null; state.handlers = null;
        },

        diagnostics() {
            const archetypeBreakdown = state.buildings.reduce((acc, b) => {
                acc[b.archetype] = (acc[b.archetype] || 0) + 1;
                return acc;
            }, {});
            const npcsWithBubble = state.npcs
                ? state.npcs.reduce((n, x) => n + (x.bubble ? 1 : 0), 0)
                : 0;
            const steamVents = (state.particles && state.particles.steamVents)
                ? state.particles.steamVents.length : 0;
            const dustMotes = (state.particles && state.particles.motes)
                ? state.particles.motes.length : 0;
            const tier4Holograms = state.buildings.reduce(
                (n, b) => n + (b.tier === 4 ? 1 : 0), 0);
            const constructionFrames = state.buildings.reduce(
                (n, b) => n + ((typeof b.ageDays === 'number' && b.ageDays < 7) ? 1 : 0), 0);
            return {
                buildings: state.buildings.length,
                visibleBuildings: state.diag.visibleBuildings,
                buildingsRendered: state.diag.buildingsRendered,
                archetypes: archetypeBreakdown,
                npcs: state.npcs ? state.npcs.length : 0,
                npcsWithBubble,
                lampposts: state.lampposts ? state.lampposts.length : 0,
                skywalks: state.skywalks ? state.skywalks.length : 0,
                vehicles: state.vehicles ? state.vehicles.length : 0,
                steamVents,
                dustMotes,
                tier4Holograms,
                constructionFrames,
                lastBuildingRenderMs: Number(state.diag.lastBuildingRenderMs.toFixed(2)),
                animFrame: state.animFrame,
                animTicks: state.diag.animTicks,
                animRunning: state.animTimer !== null,
                zoom: scale(),
                cam: { x: state.camX, y: state.camY },
                cityWidth: state.cityWidth,
                cityHeight: state.cityHeight,
                lastFrameMs: Number(state.diag.lastFrameMs.toFixed(2)),
                framesDrawn: state.diag.framesDrawn,
                jsHeapMB: (performance.memory && performance.memory.usedJSHeapSize)
                    ? Number((performance.memory.usedJSHeapSize / 1048576).toFixed(1))
                    : null,
                canvasNative: state.canvas
                    ? { w: state.canvas.width, h: state.canvas.height }
                    : null,
            };
        },
    };
})();
