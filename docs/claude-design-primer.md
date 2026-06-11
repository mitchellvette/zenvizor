# ZenVizor — primer for Claude Design

Paste this into Claude Design (claude.ai/design) at the start of a mockup
session. It is the XAML-free projection of `docs/design-system.md`; if a
token here ever diverges from that file, **the design-system file wins**
and this one needs updating to match.

---

## What ZenVizor is

A lightweight, passive Windows network monitor / reporter. Sits in the
system tray, surfaces a desktop app on demand. It attributes up/down
network traffic to the originating process, stores history locally, and
shows a near-live dashboard plus historical drill-down. **Not** a
firewall — there is no blocking or active intervention; only observation
and reporting.

Native Windows 11 app: Mica backdrop, Fluent control vocabulary, light
or dark following OS theme.

---

## The seven surfaces

1. **Dashboard** — current activity. Live rates chart (2s poll, 2-min
   trailing window) above a top-10 talkers list. Has warming-up and
   service-disconnected banners. The "what's happening right now" view.
2. **Per-App** — apps ranked by total bytes over a chosen window
   (1h/24h/7d/30d/90d). Data grid. Double-click → App Detail.
3. **App Detail** — drill-down for one app. Header + summary card +
   traffic-over-time chart + two side-by-side grids (Connections /
   Recent Sessions). The busiest screen. Single-click on a row in
   Per-App or Reports navigates here (canonical drill: hover chevron +
   `Cursor=Hand` + single click).
4. **History** — aggregate timeline. Same window picker, chart only.
5. **Reports** — daily report (live, post-Phase 5). Date picker + anchor
   menu (Avg7d / Avg30d / Avg90d). Hero numerics, 24-hour sparkline,
   Top Apps grid, Uncommon Talkers row, Notable section. Top Apps row
   and Uncommon Talker mini-card drill to App Detail with the report
   date.
6. **Alerts** — placeholder pre-Phase 6 (alert feed).
7. **Settings** — placeholder pre-Phase 6 (autostart, retention, theme).

Chrome around every page:

- `FluentWindow` with Mica backdrop and a custom title bar.
- Left navigation rail (220px) — Dashboard / Per-App / History /
  Reports / Alerts in the menu; Settings in the footer.
- Bottom status bar: colored dot + "Service: connected (…)" /
  "Service: disconnected (…)".
- System tray icon (close-to-tray; right-click → Show / Exit).

---

## State matrix — non-happy states per surface

The polish lives in these cells. Cover them in mockups.

| Surface     | empty                    | loading                       | warming                          | service-disconnected           | error (query failed) |
|-------------|--------------------------|-------------------------------|----------------------------------|--------------------------------|----------------------|
| Dashboard   | covered by warming       | needs treatment (first paint) | banner: "Warming up. First flush bucket lands within ~5 s." | banner: "Service disconnected (\<reason\>); retrying" | currently merged with disconnected |
| Per-App     | needs treatment           | wait cursor today; needs visual treatment (skeleton / caption) | n/a (history surface) | needs treatment (split from generic error) | banner: "Query failed (\<type\>): \<msg\>" |
| App Detail  | "No traffic recorded in this window." centered in chart card | wait cursor; needs treatment | n/a | needs treatment | banner |
| History     | "No traffic recorded in this window." | wait cursor; needs treatment | n/a | needs treatment | banner |
| Reports     | "Empty" + "Quiet day" variants (full page; live)  | ProgressRing overlay on first paint | n/a (history surface)  | banner + dim    | banner |
| Alerts      | needs deliberate "Phase 6" treatment | n/a | n/a | n/a | n/a |
| Settings    | needs deliberate "Phase 6" treatment | n/a | n/a | n/a | n/a |

---

## Color tokens (UI chrome)

Every brush theme-swaps with the OS Light/Dark theme.

| Token                          | Use |
|--------------------------------|-----|
| `surface.background`           | Page root background (sits under Mica) |
| `surface.card`                 | **Opaque** card background — use for cards that carry text/data over Mica |
| `surface.card.alt`             | Slightly translucent card — only where Mica show-through is intended |
| `surface.layer`                | Section grouping above background |
| `surface.subtle`               | Inline hint/banner background |
| `surface.subtle.alt`           | DataGrid alternating row |
| `text.primary`                 | Body text |
| `text.secondary`               | Captions, metadata |
| `text.tertiary`                | De-emphasized text (de-emphasized column headers, breadcrumbs) |
| `text.disabled`                | Disabled state |
| `text.inverse`                 | Light text on dark surface |
| `text.on-accent`               | Text painted on an accent fill |
| `accent.default`               | Primary interactive accent — **text / borders / focus**. Brand violet (violet-600 light / violet-500 dark). Never use as a filled-button background — dark theme is too light for white text. |
| `accent.secondary`             | Secondary accent (hover/pressed) — brand violet |
| `accent.tertiary`              | Tertiary accent (focused state) — brand violet |
| `accent.fill`                  | **Accent SURFACE** (filled buttons, pills, selection bars) carrying on-accent (white) text. Constant brand violet `#6D3FD1` in BOTH themes so white text clears AA 4.5:1 regardless. |
| `accent.text`                  | Foreground accent text on neutral surface.card (eyebrows, small accent labels) — violet-700 light / violet-300 dark for AA contrast |
| `accent.subtle`                | Soft brand-violet tint for selected/hovered accent surfaces (e.g. NavigationView selected-item background) — 10% alpha light / 18% alpha dark |
| `surface.tooltip.scrim`        | Translucent contrasting scrim for popovers (e.g. Per-App ImagePath tooltip). text.primary at 84% alpha; foreground text on it is `text.inverse`. |
| `status.success`               | Success foreground |
| `status.success.background`    | Success banner background |
| `status.caution`               | Caution foreground — dots / graphics / icon fills |
| `status.caution.background`    | Caution banner background |
| `status.caution.text`          | Caution **text** on a caution-tint background (AA-safe). Darker amber light `#8A5A00`; bright amber on dark already passes against the dark tint. |
| `status.critical`              | Error foreground |
| `status.critical.background`   | Error banner background |
| `status.neutral`               | Neutral / informational |
| `status.neutral.background`    | Neutral banner background |
| `status.connected`             | Service-status dot when pipe is up |
| `status.warming`               | Dot/banner while warming |
| `status.warming.background`    | Warming-banner background (paint matches `status.caution.background`; distinct key so warming can be repointed independently) |
| `status.disconnected`          | Dot/banner when pipe is down |
| `border.card`                  | Card stroke |
| `border.subtle`                | Lighter divider stroke |

**Mica + contrast note.** Anything that carries text or data must sit on
`surface.card` (opaque, flat) OR `metal.card` (opaque gradient), never on
`surface.card.alt` (translucent), or text contrast becomes
desktop-wallpaper-dependent.

---

## Metallic surfaces + elevation

The canonical card recipe (`metal.card + border.card + radius.card +
shadow.card`) is the default for every text- and data-bearing card. See
the Component vocabulary section below for the standing rule. Flat
`surface.card` is the exception — only for nested-card situations or
HC mode.

| Token            | Use |
|------------------|-----|
| `metal.card`     | Opaque brushed-card gradient. Light: white → cool-steel. Dark: 3-stop with baked-in `edge.light` catch-light. Always opaque so text-bearing cards keep contrast over Mica. |
| `metal.control`  | Same family tuned for short surfaces (~30–40 px controls — ComboBox, refresh button, filter input). Opaque in both themes. |
| `edge.light`     | Thin top catch-light on cards / control rims. ~55% white light / ~9% white dark. In dark theme, composited INTO `metal.card` at offset 0.015 so cards don't need a separate Rectangle overlay. |
| `shadow.sm`      | Softer / smaller elevation. Use for info-strip cards that sit alongside (not above) a primary data card — Per-App's summary strip is the reference. |
| `shadow.card`    | Canonical card elevation. Hardware-composited DropShadowEffect; static cards don't redraw on idle. |

---

## Data-viz palette (charts)

Chart paints don't inherit the UI theme — values are wired in C# code.

The **Up / Down** series are a deliberate brand deviation from the
fixed-Okabe-Ito set: they are brand violet / teal and **theme-swap** via
`BrandAccent.{Light,Dark}.xaml`. The **categorical** ramp
(`chart.series.1..8`, `chart.wan`, `chart.local`) stays fixed Okabe-Ito
(colorblind-safe) for multi-series charts.

| Token              | Role                                              | Hex       |
|--------------------|---------------------------------------------------|-----------|
| `chart.upSeries`   | Upload series — brand violet, theme-swaps         | `#6D3FD1` light / `#9A72F0` dark |
| `chart.downSeries` | Download series — brand teal, theme-swaps         | `#20B6C6` light / `#34D0E0` dark |
| `chart.wan`        | WAN-class endpoint (Okabe-Ito, fixed)             | `#0072B2` |
| `chart.local`      | LAN/local endpoint (Okabe-Ito, fixed)             | `#009E73` |
| `chart.series.1`   | Categorical slot 1                                | `#0072B2` |
| `chart.series.2`   | Categorical slot 2                                | `#E69F00` |
| `chart.series.3`   | Categorical slot 3                                | `#009E73` |
| `chart.series.4`   | Categorical slot 4                                | `#CC79A7` |
| `chart.series.5`   | Categorical slot 5                                | `#56B4E9` |
| `chart.series.6`   | Categorical slot 6                                | `#D55E00` |
| `chart.series.7`   | Categorical slot 7                                | `#F0E442` |
| `chart.series.8`   | Categorical slot 8                                | `#999999` |

### Chart-chrome tokens

LiveCharts2 paints **none** of the axis / gridline / label / tooltip /
legend chrome from the UI theme. These must be set explicitly on every
chart (wired by `ChartBuilder` / `ChartTheming.Apply`).

| Token                 | Use                                                  |
|-----------------------|------------------------------------------------------|
| `chart.axis`          | Axis line stroke (theme-swap; `border.subtle`-ish)   |
| `chart.gridline`      | Gridline stroke (lower-alpha border.subtle)          |
| `chart.axis.label`    | Axis tick labels (`text.tertiary`)                   |
| `chart.tooltip.bg`    | Tooltip surface — OPAQUE so contrast is stable       |
| `chart.tooltip.text`  | Tooltip label text (`text.primary`)                  |
| `chart.legend.text`   | Legend label text (`text.secondary`)                 |

Two chart styles in use today:

- **Line series** (Up / Down) for ≤24h windows — `chart.upSeries`,
  `chart.downSeries`.
- **Stacked column** (Up bottom, Down top) for >24h windows — same two
  tokens; segments stack into a single bar per bucket.

---

## Typography

Three fonts (all on disk):

| Family       | Role token   | Weights                     | Use |
|--------------|--------------|-----------------------------|-----|
| **Urbanist** | `font.display` | Light, Regular, **SemiBold**, Bold | Primary UI — body, headers, captions. SemiBold is the brand's title/subtitle weight — real `Urbanist-SemiBold.ttf` on disk; do NOT let WPF synthesize the weight from Regular (produces blurry strokes). |
| **Overpass Mono** | `font.mono`  | Variable (pinned Regular)   | Numeric, paths, hex, code-like text — anywhere column alignment matters |
| **Nuqun**    | `font.brand` | Regular only                | Wordmark / decorative only — **not** for body text |

Size scale (brand-reconciled):

| Token                  | px | Renders in        |
|------------------------|----|-------------------|
| `font.size.caption`    | 12 | `font.display`    |
| `font.size.body`       | 14 | `font.display`    |
| `font.size.body.large` | 18 | `font.display`    |
| `font.size.subtitle`   | 20 | `font.display`    |
| `font.size.metric`     | 24 | `font.display`    |
| `font.size.title`      | 28 | `font.display`    |
| `font.size.title.large`| 40 | `font.display`    |
| `font.size.display`    | 68 | **`font.brand` (Nuqun)** |

Weights: `font.weight.regular` (400), `font.weight.semibold` (600),
`font.weight.bold` (700).

Usage rules:

- Body: `font.display` Regular 14.
- Card titles / page subtitles: `font.display` SemiBold 20 (`text.subtitle`).
- Headline metric values (status / KPI cards): `font.display` SemiBold 24
  (`text.metric`). Override `FontFamily` to `font.mono` for numeric values.
- Numeric / paths / IPs: `font.mono` Regular 14.
- Eyebrows: `font.display` SemiBold 12, uppercase the source string at the
  call site (`Text="STATUS"`). WPF has no letter-spacing on `TextBlock`,
  so the brand's `tracking-wide` (0.04em) is unachievable without per-character
  Runs — accepted limit because eyebrows are short and uppercase.
- Wordmark/splash: `font.brand`. **Nowhere else.**

---

## Spacing

4-based. `space.12` for medium gaps (not `space.10`). App set {4,8,12,16,24,32,48};
brand adds {20,40,64}.

| Token        | px |
|--------------|----|
| `space.4`    | 4  |
| `space.8`    | 8  |
| `space.12`   | 12 |
| `space.16`   | 16 |
| `space.20`   | 20 |
| `space.24`   | 24 |
| `space.32`   | 32 |
| `space.40`   | 40 |
| `space.48`   | 48 |
| `space.64`   | 64 |

Usage:

- `space.24` — page outer margin.
- `space.16` — gap between major card stacks.
- `space.12` — card header to body.
- `space.8` — label to value, banner padding.
- `space.4` — glyph to text.

---

## Corner radius

Two layers — **scale tokens** (raw px) and **semantic role tokens**
(intent-named, point at a scale step). Mockup annotations should reference
the role tokens.

### Scale

| Token         | px   |
|---------------|------|
| `radius.xs`   | 4    |
| `radius.sm`   | 6    |
| `radius.md`   | 10   |
| `radius.lg`   | 14   |
| `radius.xl`   | 20   |
| `radius.pill` | 9999 (sentinel; set to half control height in practice) |

### Roles

| Token             | Points at   | Use                                    |
|-------------------|-------------|----------------------------------------|
| `radius.control`  | `radius.sm` | Buttons, inputs, chips, inline banners |
| `radius.card`     | `radius.md` | Cards, list rows                       |
| `radius.overlay`  | `radius.lg` | Flyouts, dialogs, popovers             |

---

## Density

For data-dense grids the **compact** variant applies. Default Fluent
spacing feels too airy.

| Token                  | Value | Use                            |
|------------------------|-------|--------------------------------|
| `density.row.default`  | 28 px | Standard DataGrid rows         |
| `density.row.compact`  | 32 px | Data-dense DataGrid rows       |

Compact cell padding: `12,0` (horizontal-only — vertical padding in a
fixed-height row shrinks the content slot and WPF layout-clips
descenders). Compact font size: `font.size.body` (14).

Apply compact on: Per-App `AppsGrid`, App Detail `ConnectionsGrid`,
App Detail `SessionsGrid`, Reports `TopAppsGrid` (the Top Apps grid
overrides RowHeight to 56 for its two-line App-cell layout, while still
using the compact style for header / cell padding / font). Dashboard's
talkers ListView stays at `default`.

---

## Component vocabulary

Recurring building blocks. Mockup annotations name these by component
plus tokens.

- **FluentWindow chrome** — Mica backdrop, custom title bar, no
  visible OS chrome above the title bar.
- **Navigation rail** — 220px left pane, icon + label per item, footer
  item for Settings. Icons are Fluent System Icons (DataPie, Apps,
  History, DocumentText, Alert, Settings).
- **Status banner** — inline strip: caution / critical / warming
  background, matching foreground (`status.caution.text` on caution
  for AA-safe small text); `radius.control`, `8,4` padding.
- **Card — canonical recipe.** Every text- and data-bearing card on
  every page uses the same recipe; this is a standing decision, do not
  re-litigate per screen:
  - Background: `metal.card`
  - Stroke: `border.card` 1 px
  - Radius: `radius.card` (role token, resolves to 10)
  - Elevation: `shadow.card` (use `shadow.sm` for info-strip cards
    that sit alongside, not above, a primary data card)
  Flat `surface.card` is the exception — only for nested-card
  situations or HC mode collapse.
- **Summary card** (App Detail header) — two-line card; line 1
  `text.primary`, line 2 `text.secondary`.
- **DataGrid row** — compact density on data-dense grids; alternating
  background is `surface.subtle.alt`; single-click drills; selected row
  carries an `accent.default` 3 px left selection pill.
- **Chart card** — canonical recipe outer + `lvc:CartesianChart` inside,
  legend on top, `MinHeight: 180`. Series colored from `chart.*`.
- **No-data overlay** — single centered `TextBlock`, `text.secondary`,
  copy "No traffic recorded in this window." Doesn't capture chart
  input.
- **Bottom-bar connection indicator** — small ellipse (`status.connected`
  / `.warming` / `.disconnected`) + `TextBlock` ("Service: connected
  (\<version\>, proto \<n\>)" or "Service: \<message\>").
- **Tray icon menu** — OS-standard context menu. Items: Show ZenVizor,
  separator, Exit. No tokenization (OS chrome).

---

## Annotation vocabulary (recap from the mockup template)

Each component in a mockup should carry:

- The token names for surface / text / accent / border colors used.
- The font role + size + weight tokens.
- Spacing tokens for padding/margin.
- A density tag if it differs from `default`.
- State tags (`state: default`, `state: hover`, `state: pressed`,
  `state: disabled`, `state: empty`, `state: loading`,
  `state: warming`, `state: disconnected`, `state: error`).
- Layout hints (`MinHeight`, `MaxHeight`, `scroll: page`/`pane`) where
  layout matters.

When you need a token that doesn't exist yet, name it with the existing
pattern `<category>.<role>[.<modifier>]` — dotted lowercase, never
PascalCase. Example: `surface.card.elevated`, not `cardBackgroundElevated`.

---

## Project principles to preserve in design decisions

- **Discovery > ranking.** Never cap drill-down lists by score/bytes — a
  malicious leech process won't be in any top-N. If a surface has too
  much data, coarsen by time (rollups, downsample). Don't hide rows
  behind ellipses or "see more" gates.
- **Light and fast.** Idle CPU <1%, working set <80 MB. Don't introduce
  animation/blur/glow that pays no benchmark dividend.
- **Honest attribution.** Don't visually imply precision we don't have.
  When `svchost` hosts several services, list them all and report the
  PID-level total — never split bytes across co-hosted services.
- **Passive only.** Never visually hint at "block this app" or any
  active intervention. ZenVizor observes; it never blocks.
