# ZenVizor — Design System

> A lightweight, open-source **passive network monitor for Windows**. ZenVizor gives you a complete, at-your-fingertips view of local network traffic: real-time live monitoring, per-app attribution, and daily / weekly / monthly reports — with **zero traffic of its own**.

This repository is the **brand & design system** for ZenVizor: the visual foundations, tokens, fonts, iconography, a faithful UI kit of the desktop app, and the writing/voice guidelines an agent or designer needs to produce on-brand work.

---

## 1. Product context

ZenVizor is a native **Windows desktop application** (WPF + WPF-UI / Fluent, .NET 10). It is **passive and visibility-only** — there is no firewall, no blocking, no traffic shaping, and the tool emits no network traffic of its own. It exists to answer one question fast: *"What is talking to the network, how much, and is that expected?"*

What it does:
- **Live activity** — near-real-time per-app upload/download rates and a "top talkers" view.
- **Per-app attribution** — traffic resolved to the originating process, including `svchost` → service-name resolution, publisher / signature status, and a user-writable-path heuristic.
- **History & reports** — user-defined time windows, daily/weekly/monthly overview reports (exportable to CSV / HTML).
- **Alerts** — a feed of raised alerts (e.g. an unsigned binary from a user-writable path making connections), with acknowledge.
- **Settings** — autostart, retention windows, purge, flush/bucket intervals, theme.

It runs quietly in the system tray, close-to-tray by default, and ships **light and dark themes** of equal weight.

> **Primary persona:** the hands-on power user / admin who keeps it in the background and checks in. The UI is optimized for *fast answers*, not for living inside all day.

### Sources used to build this system
- **GitHub (product / codebase, private):** `mitchellvette/zenvizor` — https://github.com/mitchellvette/zenvizor
  - Informed *what the product is* and its information architecture: `docs/zenvizor-prd.md`, `src/ZenVizor.Ui/MainWindow.xaml`, `src/ZenVizor.Ui/Views/DashboardPage.xaml`.
  - **Note:** the app's existing WPF-UI styling, fonts, and colors were treated as *placeholders*. This design system is a **ground-up brand build**. The only fixed inputs were the **name (ZenVizor)** and **purple as a secondary color**.
- **Iconography:** Microsoft **Fluent System Icons** (the set WPF-UI's `SymbolIcon` draws from) — https://github.com/microsoft/fluentui-system-icons (MIT). Specific SVGs are vendored into `assets/icons/`.

Anyone with access should read `mitchellvette/zenvizor` directly (especially `docs/zenvizor-prd.md` §11 "UI / information architecture") to build higher-fidelity work.

---

## 2. Brand strategy

**Name:** **ZenVizor** — *zen* (calm, quiet, unobtrusive) + *vizor* (visibility, a HUD visor).

**The feeling:** *flying a calm spaceship from a utopian future.* Think **Echo from Overwatch** — elegant, aerodynamic, porcelain-smooth surfaces with luminous violet light. **Not** cyberpunk, not sci-fi clutter, no glowing-doodad chrome. Controls are intuitive and minimally intrusive; information is at your fingertips; everything is quiet until it needs not to be.

**Three principles**
1. **Calm by default.** The UI recedes. Color and motion are reserved for meaning (a live rate, an alert, a selection). Big, legible numbers; generous space.
2. **Aerodynamic clarity.** Smooth surfaces, soft elevation, clean reads. Nothing decorative competes with the data.
3. **Honest instrumentation.** This is a monitoring tool — it never fabricates precision. The visual language favors exact, tabular, trustworthy data.

**Color identity:** a porcelain/steel **primary** (the aerodynamic "body") carrying a luminous **violet secondary** as the interactive accent (LSU-tiger-purple anchored). Violet is the visor light — used for selection, focus, the live "upload" series, and CTAs.

---

## 3. Foundation: built on WPF-UI (Fluent)

This is a **hard requirement** and shapes every component. The system keeps Fluent's *bones* and re-skins them:
- **Window chrome:** a `FluentWindow` with an integrated `TitleBar`, **Mica** backdrop (the tinted, layered desktop material), min/max/close.
- **Navigation:** a left `NavigationView` rail (~220px) with `SymbolIcon` items and a footer item (Settings).
- **Materials:** **Mica** (window backdrop) and **Acrylic** (frosted flyouts / command surfaces), plus Fluent's layered *fill* families (`CardBackgroundFill`, `SubtleFill`, `ControlFill`) and a bottom **control-elevation border**.
- **Type ramp:** the Fluent ladder (Caption → Body → Subtitle → Title → Title Large → Display).
- **Icons:** Fluent System Icons.

The *brand* layer (porcelain + violet palette; Urbanist / Nuqun / NFCode type; the visor mark; the spacious calm) sits on top of these bones.

---

## 4. Content fundamentals (voice & copy)

ZenVizor's voice is **calm, precise, and quietly confident** — a competent co-pilot, never chatty, never alarmist. It reflects the product: passive, honest, at-your-fingertips.

**Tone & stance**
- **Plain and exact over clever.** Say the true thing in the fewest words. "Service disconnected (pipe closed)." not "Oops! We lost the connection 😬".
- **Honest about limits.** The product never fabricates precision, and neither does the copy. When attribution is uncertain, say so: *"(unknown)"*, *"warming up — first flush bucket lands within ~5 s"*, *"svchost.exe [Dnscache, NlaSvc]"*.
- **Quiet, not silent.** Status is always legible but never loud. Alerts are factual, not fear-mongering: *"Unsigned binary from a user-writable path is making connections."*
- **Second person, present tense.** Address the user as **you**; describe the system in plain present tense ("ZenVizor watches…", "The service owns the database"). The product refers to itself as **ZenVizor** or **the service**, rarely "we."

**Casing & mechanics**
- **Sentence case everywhere** — headings, buttons, labels, menu items. ("Current activity", "Per-app", "Purge history", "Export report".) Never Title Case UI, never ALL-CAPS except tiny eyebrow labels and unit tags.
- **Numbers are first-class.** Always show units (`B/s`, `KB/s`, `MB/s`, `GB/s`); use tabular figures; round honestly (`1.4 MB/s`, `0 B/s`). Timestamps `HH:mm:ss` for live, `MMM d` for history.
- **Technical terms kept exact.** `PID`, `TCP`/`UDP`, `WAN`/`Local`, `Signed`/`Unsigned`/`Invalid`/`Unchecked`, `svchost.exe`. Don't dumb these down — the audience is technical.
- **Em dashes for asides; no exclamation points** in product UI. Sparing parentheticals for qualifiers.

**Emoji & decoration:** **No emoji.** No mascots, no exclamatory microcopy. Personality comes from precision and restraint, not punctuation.

**Examples (from the product surface)**
- Section title: *"Current activity"*
- Empty/warming state: *"warming up — first flush bucket lands within ~5 s"*
- Error: *"service disconnected (pipe closed)"*
- Alert title: *"Unsigned binary making connections"* · detail: *"chrome_helper.exe is unsigned and runs from %TEMP% — it has contacted 3 WAN endpoints."*
- Setting label: *"Keep high-resolution samples for"* → `30 days`
- Button: *"Acknowledge"*, *"Export report"*, *"Purge history…"* (trailing ellipsis = opens a confirm step)

---

## 5. Visual foundations

### Color
- **Primary — Steel.** A cool, faintly-blue neutral scale (`--steel-*`). This is the porcelain body: backgrounds, cards, chrome, text. Surfaces are near-white in light mode and deep cool-charcoal (never pure black) in dark mode.
- **Secondary / accent — Violet.** `--violet-*`, anchored on **LSU tiger purple `#461d7c`** at 800, with a luminous `#8254e6`/`#9a72f0` for glow. Used *only* for meaning: selection, focus rings, the upload data series, primary buttons, the focal node in the logo. Never as large fills.
- **Semantic:** emerald (healthy / Signed / success), amber (warning / connecting), coral (danger / unsigned-from-temp / critical), blue (info / WAN). All slightly desaturated to stay calm.
- **Data series:** the up/down series are **upload = violet, download = teal** (`chart.upSeries` / `chart.downSeries`), and they theme-swap with light/dark. The **categorical** chart palette (`chart.series.1–8`, plus `chart.wan` / `chart.local`) is the **Okabe-Ito colorblind-safe** set — fixed hex, does *not* theme-swap, and matches the native app exactly. (Note: violet/teal is a deliberate brand choice for up/down and is less colorblind-distinct than the app's stock blue/orange; the categorical palette preserves safety for multi-series charts.)
- Defined as semantic tokens that flip between light/dark — see `colors_and_type.css`.

> **Token names are reconciled with the native app's XAML spec.** Canonical CSS variables mirror the app's dotted token names with hyphens (`surface.card` → `--surface-card`, `accent.default` → `--accent-default`, `space.12` → `--space-12`, `font.display` → `--font-display`). Legacy names (`--fg1`, `--fill-card`, `--accent`, `--series-up`…) remain as back-compat aliases. Where the brand value differs from the value shipping in XAML today (radii, type sizes), `colors_and_type.css` carries a **reconciliation crosswalk** documenting the before→after as a migration map — those tokens are buildable natively, not mock-only.

### Type
- **Brand / hero — Nuqun** (`font.brand` / `--font-brand`): a distinctive futuristic display face. Used **only** for hero moments — the wordmark, the Display size, brand surfaces. Never body, titles, or dense UI. Ships Regular only, so set at weight 400 (no faux-bold), with comfortable positive tracking (`--tracking-display`).
- **Daily driver / UI — Urbanist** (`font.display` / `--font-display`; legacy alias `--font-sans`): the everyday geometric sans — nav, body, labels, page titles, most of the interface. This is the **primary UI font role**. Weights 300 / 400 / **600 (SemiBold)** / 700 — 600 is a real weight (used for titles, subtitles, body-strong, eyebrows), no longer faux-bolded.
- **System / data — NFCode** (`font.mono` / `--font-mono`): strict monospace with tabular figures for rates, byte counts, ports, IDs, technical tags (`ZenVizor.Ipc.v1`, `:443`, `pid 8841`).
- **Ramp:** Display 68 / Title Large 40 / Title 28 / Subtitle 20 / Body Large 18 / Body 14 / Caption 12 (`font.size.*`; line-heights/weights in the CSS). Only the Display size renders in Nuqun (`font.brand`); everything from Title-Large down is Urbanist (`font.display`). Eyebrow labels track wide + uppercase. *Native note:* the app currently ships the smaller Fluent ladder (Display 32 / Title 20 / Title-Large 24 / Subtitle 16); these brand sizes are the reconciled target — see the crosswalk in `colors_and_type.css`.
- All three are **self-hosted** from `fonts/` via `@font-face` in `colors_and_type.css` — no CDN, works offline.

### Space & layout
- **4px base grid**; spacious by default (24px page margins, 12–24px gaps). Tokens are named by **pixel value** to match the XAML convention: `--space-4 / 8 / 12 / 16 / 20 / 24 / 32 / 40 / 48 / 64` (`space.12` = 12px). The native app's spacing set {4,8,12,16,24,32,48} is a clean subset; the brand adds {20,40,64}. *(Legacy ordinal names `--space-1…16` were removed — they collided with the by-value scheme.)*
- Left nav rail ~220px, content area with 24px margins, a persistent bottom status strip.
- Charts and tables are the heroes — give them room; align numeric columns right with tabular figures.

### Radii & shape
- Controls **6px** (`radius.sm`), cards/rows **10px** (`radius.md`), overlays/flyouts/hero surfaces **14px** (`radius.lg`), pills/status chips fully rounded (`radius.pill`); `radius.xs` = 4px. Aerodynamic = lean on the larger radii for big surfaces; keep dense data rows tighter.
- *Native note:* the app ships the stock Fluent ladder (4/6/8) today; these brand radii are the reconciled target and are buildable via a `CornerRadius` resource swap — see the crosswalk in `colors_and_type.css`.

### Elevation, glass & materials
- **Mica** tints the window backdrop (a soft, layered desktop material — `--surface-window` / legacy `--bg-app`). Mica is a **window-level** material — WPF sets it via `WindowBackdropType` through DWM. There is **no per-element backdrop** in WPF, so don't design "frosted panels" floating over content.
- **The App-Detail flyout is OPAQUE** — it uses `surface.layer` (`--surface-layer`), *not* a frosted panel. A per-element Acrylic surface isn't achievable in WPF (and would be a contrast + perf liability anyway). **Real Acrylic is reserved for OS-level surfaces only** — context menus and the tray menu, which Windows renders for us. The web kit's `--acrylic-bg` + `backdrop-filter` is a **mock-only approximation** of those OS menus and has no per-element XAML counterpart. Glass stays **tasteful and reserved**, and **never under text-bearing data cards**.
- **Metallic / brushed-steel finish (systemic, STATIC).** Surfaces and controls carry a subtle *lit-from-above* read: a faint vertical gradient (`--metal-card`, `--metal-control`, `--metal-bar`), a crisp top **edge-light** highlight (`inset 0 1px 0 var(--edge-light)`), and a specular **sheen** overlay (`--sheen`) layered on top of the base fill. The sheen is a **static** highlight — never an animated sweep. Buttons use this most overtly — the primary is a glossy violet (`--accent-grad` + sheen + white top-edge), the secondary a frosted brushed-steel chip; toggles, segmented controls, selects, KPI tiles, cards, the title bar and status strip all share the finish. The effect is strongest in dark mode (cockpit panels) and whisper-quiet in light (porcelain). Natively these map to `LinearGradientBrush` + a 1px `Border` — composited once, ~free. Reach for these tokens on any new surface so it matches.
- **Text-bearing cards are OPAQUE.** Cards that carry text/data use `surface.card` (`--surface-card`, opaque) over the Mica, with a 1px hairline stroke (`--border-card`) and a soft, cool-tinted shadow (`--shadow-sm/md/lg`) — **no live backdrop-blur** (it would make text contrast wallpaper-dependent and costs GPU). The translucent variant `surface.card.alt` (`--fill-card`) exists only where Mica show-through is intentional. A subtle bottom control-elevation border gives controls a lit-from-above read.
- Reserve the **violet glow** (`--shadow-glow`) for focus and the live/primary affordance only.
- **Polished-metal surfaces (brand moments only).** For the logo lockup, hero/empty states, dividers and marketing — not dense data UI — use the chrome/steel surface fills: `--surface-chrome`, `--surface-brushed`, `--surface-graphite`, `--surface-chrome-violet`. These are planar, reflective gradients (a ~50% highlight band reads as a chrome horizon) — inspired by liquid-metal editorial work but kept calm and legible. Pair with the visor mark or large type; keep them out of tables and controls.

### High contrast (Windows HC)
- This system **hard-overrides every brush**, which is exactly what disables Windows High Contrast — WPF normally yields to `SystemColors`, but only when you *don't* override. **Stance:** ship a **separate High Contrast `ResourceDictionary`** that remaps the semantic tokens (`surface.*`, `text.*`, `accent.*`, `status.*`, `border.*`) onto system brushes (`SystemColors.WindowBrush`, `ControlTextBrush`, `HighlightBrush`, …), swapped in when `SystemParameters.HighContrast` is true. It's a **bounded but non-automatic** dev task — see §8. (Web mocks have the analogous `@media (forced-colors: active)` path; the brand palette is not expected to render in HC.)

### Motion
- **Calm and aerodynamic — glide, never bounce.** Easing `--ease-glide` (a smooth decelerate). Durations 120/200/320ms. Use opacity + small translate fades; selection slides; the live chart streams. No springy overshoot, no infinite decorative loops. Respect `prefers-reduced-motion`.

### Interaction states
- **Hover:** subtle fill wash (`--fill-subtle`) or a one-step-lighter control fill; never a jarring color change.
- **Press:** a slightly deeper fill (`--fill-subtle-2`) and a tiny scale-down (~0.98) on buttons. No color flip.
- **Selected (nav/tab):** an accent **pill/indicator** + accent text/icon; background stays calm.
- **Focus:** a 2px violet focus ring (`--accent`) with a soft glow — always visible for keyboard users.
- **Disabled:** `--fg-disabled`, reduced opacity, no shadow.

### Imagery
- The product is data-first; it ships **no photography**. "Imagery" = charts, the visor mark, and Fluent iconography. If marketing imagery is ever needed, keep it **cool-toned, clean, aerodynamic** (porcelain whites, deep cool charcoals, violet light) — no warm grain, no stock-photo clutter.

---

## 6. Iconography

- **System:** Microsoft **Fluent System Icons** (https://github.com/microsoft/fluentui-system-icons, MIT) — the exact family WPF-UI's `SymbolIcon` renders, so the kit matches the real app. The **24px** grid is the default; **Regular** weight for most UI, **Filled** for the *selected* nav state and status glyphs.
- **Format:** individual **SVGs vendored into `assets/icons/`** (flattened, names like `data_pie.svg`, `data_pie-filled.svg`). Fills are normalized to `currentColor` so icons inherit text color and theme automatically — inline them (or use a CSS mask) to tint with `--accent` etc.
- **Nav mapping (matches the app):** Dashboard → `data_pie`, Per-app → `apps`, History → `history`, Reports → `document_text`, Alerts → `alert`, Settings → `settings`.
- **Semantics:** `shield_checkmark` = Signed, `shield_error`/`shield_dismiss` = Unsigned/Invalid, `globe` = WAN, `home` = Local, `arrow_upload`/`arrow_download` = up/down traffic, `circle-filled` = status dot, `pulse`/`gauge` = live activity.
- **No emoji. No unicode pictographs as icons.** Use the Fluent set or nothing. Stroke weight and corner character are consistent because they all come from one family — don't mix in another icon set.
- Need an icon that isn't vendored yet? Pull the matching `ic_fluent_<name>_24_regular.svg` from the Fluent repo into `assets/icons/` the same way (normalize fill to `currentColor`).

---

## 7. Repository index / manifest

**Root**
- `README.md` — this file.
- `colors_and_type.css` — all design tokens: color primitives, light/dark semantic tokens, the type ramp + helper classes, radii, spacing, density, motion. Canonical variable names mirror the **native app's dotted XAML tokens** (`--surface-card`, `--accent-default`, `--space-12`, `--font-display`…), with legacy names kept as back-compat aliases and a **reconciliation crosswalk** in the file header. **Import this first** in any artifact.
- `SKILL.md` — Agent-Skill manifest so this system can be used directly by Claude Code.

**`assets/`**
- `icons/` — vendored Fluent System Icons (SVG, `currentColor`).
- `zenvizor-mark-onlight.svg` / `zenvizor-mark-ondark.svg` — the visor brand mark.

**`fonts/`** — self-hosted brand webfonts (Urbanist, NFCode, Nuqun) wired via `@font-face` in `colors_and_type.css`.

**`preview/`** — small HTML specimen cards that populate the Design System tab (logo, colors, type, spacing, elevation, components, icons).

**`ui_kits/zenvizor-app/`** — the faithful, interactive recreation of the ZenVizor desktop app.
- `index.html` — the running app shell (nav between screens, theme toggle).
- `README.md` — what's covered and how to compose it.
- JSX component files — window chrome, nav rail, cards, tables, charts, alerts, settings, etc.

> No slide template was provided, so no `slides/` are included.

---

## 8. Caveats & substitutions

- **Fonts** are the uploaded brand families — **Nuqun** (hero/display), **Urbanist** (UI), **NFCode** (data/mono) — self-hosted from `fonts/` via `@font-face`. No CDN; works offline.
- **Icons** are the genuine Microsoft Fluent System Icons (MIT). A focused subset is vendored; pull more as needed.
- The **brand mark** is an original geometric "visor arc" created for this system (no prior logo existed).
- Existing app screenshots/styling were intentionally **not** mirrored — this is a ground-up brand applied to the product's real information architecture.

### Native reconciliation — dev action items
The reconciled token values are buildable in XAML (resource-value swaps); the full before→after is the **crosswalk** in the `colors_and_type.css` header. Several items need real work, not just a value swap:
1. **Compact grid row height.** The larger reconciled type (`subtitle` 20, `title` 28) inside the 22–28px compact DataGrid rows (`density.row.compact` / `.default`) needs a row-height / line-height pass so dense grids don't clip.
2. **Split shared `CornerRadius` keys.** Wpf.Ui shares some `CornerRadius` resources across controls *and* cards; pushing cards to `radius.md` (10px) may drag button/input radius along unless those resource keys are separated first.
3. **DynamicResource for theme-swapping tokens.** Every brush defined in both the light and dark blocks must be a `DynamicResource` (not `StaticResource`) or the OS theme toggle won't repaint it. Theme-invariant tokens (spacing, radii, fonts, the fixed Okabe-Ito categorical palette) may stay `StaticResource`.
4. **Add the chart-chrome tokens.** LiveCharts2 paints none of the axis/grid/label/tooltip/legend chrome from the theme — wire `chart.axis`, `chart.gridline`, `chart.axis.label`, `chart.tooltip.bg/.text`, `chart.legend.text` (all theme-swapping) before the first App-Detail / History chart.
5. **Opaque flyout + OS-only Acrylic.** Make the App-Detail flyout an opaque `surface.layer` panel; reserve real Acrylic for OS context/tray menus (no per-element backdrop in WPF).
6. **High Contrast `ResourceDictionary`.** Provide the HC token remap onto `SystemColors`, activated on `SystemParameters.HighContrast` — bounded, but it will not happen automatically while the brand brushes are hard-overridden.

Tokens flagged `needs-migration` in the crosswalk (radii, type sizes) are the items to apply on the native side; `intentional dev.` rows (chart up/down colors) are deliberate brand deviations, not bugs.
