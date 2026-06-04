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
   Recent Sessions). The busiest screen.
4. **History** — aggregate timeline. Same window picker, chart only.
5. **Reports** — placeholder pre-Phase 5 (daily report + CSV export).
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
| Dashboard   | covered by warming       | needs treatment (first paint) | banner: "warming up — first flush bucket lands within ~5 s" | banner: "service disconnected (\<reason\>)" | currently merged with disconnected |
| Per-App     | needs treatment           | wait cursor today; needs visual treatment (skeleton / caption) | n/a (history surface) | needs treatment (split from generic error) | banner: "Query failed (\<type\>): \<msg\>" |
| App Detail  | "No traffic recorded in this window." centered in chart card | wait cursor; needs treatment | n/a | needs treatment | banner |
| History     | "No traffic recorded in this window." | wait cursor; needs treatment | n/a | needs treatment | banner |
| Reports     | needs deliberate "Phase 5" treatment | n/a | n/a | n/a | n/a |
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
| `accent.default`               | Primary interactive accent (matches OS accent) |
| `accent.secondary`             | Secondary accent (hover/pressed) |
| `accent.tertiary`              | Tertiary accent (focused state) |
| `status.success`               | Success foreground |
| `status.success.background`    | Success banner background |
| `status.caution`               | Caution foreground |
| `status.caution.background`    | Caution banner background |
| `status.critical`              | Error foreground |
| `status.critical.background`   | Error banner background |
| `status.neutral`               | Neutral / informational |
| `status.neutral.background`    | Neutral banner background |
| `status.connected`             | Service-status dot when pipe is up |
| `status.warming`               | Dot/banner while warming |
| `status.disconnected`          | Dot/banner when pipe is down |
| `border.card`                  | Card stroke |
| `border.subtle`                | Lighter divider stroke |

**Mica + contrast note.** Anything that carries text or data must sit on
`surface.card` (opaque), not `surface.card.alt` (translucent), or text
contrast becomes desktop-wallpaper-dependent.

---

## Data-viz palette (charts)

Separate from the UI chrome — chart paints do not inherit the UI theme.
Values are from the Okabe-Ito colorblind-safe palette and are legible on
both Light and Dark Mica without per-theme swapping.

| Token              | Role                                              | Hex       |
|--------------------|---------------------------------------------------|-----------|
| `chart.upSeries`   | Upload series                                     | `#56B4E9` |
| `chart.downSeries` | Download series                                   | `#E69F00` |
| `chart.wan`        | WAN-class endpoint                                | `#0072B2` |
| `chart.local`      | LAN/local endpoint                                | `#009E73` |
| `chart.series.1`   | Categorical slot 1                                | `#0072B2` |
| `chart.series.2`   | Categorical slot 2                                | `#E69F00` |
| `chart.series.3`   | Categorical slot 3                                | `#009E73` |
| `chart.series.4`   | Categorical slot 4                                | `#CC79A7` |
| `chart.series.5`   | Categorical slot 5                                | `#56B4E9` |
| `chart.series.6`   | Categorical slot 6                                | `#D55E00` |
| `chart.series.7`   | Categorical slot 7                                | `#F0E442` |
| `chart.series.8`   | Categorical slot 8                                | `#999999` |

Two chart styles in use today:

- **Line series** (Up / Down) for ≤24h windows — `chart.upSeries`,
  `chart.downSeries`.
- **Stacked column** (Up bottom, Down top) for >24h windows — same two
  tokens; segments stack into a single bar per bucket.

---

## Typography

Three fonts (all on disk):

| Family       | Role token   | Weights              | Use |
|--------------|--------------|----------------------|-----|
| **Urbanist** | `font.display` | Light, Regular, Bold | Primary UI — body, headers, captions |
| **NF Code**  | `font.mono`  | Regular only         | Numeric, paths, hex, code-like text — anywhere column alignment matters |
| **Nuqun**    | `font.brand` | Regular only         | Wordmark / decorative only — **not** for body text |

Size scale (mirrors Wpf.Ui FontTypography):

| Token                  | px |
|------------------------|----|
| `font.size.caption`    | 12 |
| `font.size.body`       | 14 |
| `font.size.subtitle`   | 16 |
| `font.size.title`      | 20 |
| `font.size.title.large`| 24 |
| `font.size.display`    | 32 |

Weights: `font.weight.regular`, `font.weight.semibold`, `font.weight.bold`.

Usage rules:

- Body: `font.display` Regular 14.
- Card titles / page subtitles: `font.display` SemiBold 16/20.
- Numeric / paths / IPs: `font.mono` Regular 14.
- Wordmark/splash: `font.brand`. **Nowhere else.**

---

## Spacing

4-based. `space.12` for medium gaps (not `space.10`).

| Token        | px |
|--------------|----|
| `space.4`    | 4  |
| `space.8`    | 8  |
| `space.12`   | 12 |
| `space.16`   | 16 |
| `space.24`   | 24 |
| `space.32`   | 32 |
| `space.48`   | 48 |

Usage:

- `space.24` — page outer margin.
- `space.16` — gap between major card stacks.
- `space.12` — card header to body.
- `space.8` — label to value, banner padding.
- `space.4` — glyph to text.

---

## Corner radius

| Token        | px | Use                           |
|--------------|----|-------------------------------|
| `radius.sm`  | 4  | Inline banners, chips         |
| `radius.md`  | 6  | Cards (current convention)    |
| `radius.lg`  | 8  | Large surfaces, dialogs       |

---

## Density

For data-dense grids the **compact** variant applies. Default Fluent
spacing feels too airy.

| Token                  | Value | Use                            |
|------------------------|-------|--------------------------------|
| `density.row.default`  | 28 px | Standard DataGrid rows         |
| `density.row.compact`  | 22 px | Data-dense DataGrid rows       |

Compact cell padding: 6,2. Compact font size: `font.size.body` (14).

Apply compact on: Per-App `AppsGrid`, App Detail `ConnectionsGrid`,
App Detail `SessionsGrid`. Dashboard's talkers ListView stays at
`default`.

---

## Component vocabulary

Recurring building blocks. Mockup annotations name these by component
plus tokens.

- **FluentWindow chrome** — Mica backdrop, custom title bar, no
  visible OS chrome above the title bar.
- **Navigation rail** — 220px left pane, icon + label per item, footer
  item for Settings. Icons are Fluent System Icons (DataPie, Apps,
  History, DocumentText, Alert, Settings).
- **Status banner** — inline strip: subtle / caution / critical
  background, matching foreground, `radius.sm`, `space.8` padding.
- **Card** — `Border` with `surface.card` background, `border.card`
  stroke, `radius.md`, inner `space.12` padding.
- **Summary card** (App Detail header) — two-line card; line 1
  `text.primary`, line 2 `text.secondary`.
- **DataGrid row** — compact density on data-dense grids; alternating
  background is `surface.subtle.alt`; selection inherits Wpf.Ui default.
- **Chart card** — card outer + `lvc:CartesianChart` inside, legend on
  top, `MinHeight: 180`. Series colored from `chart.*`.
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
