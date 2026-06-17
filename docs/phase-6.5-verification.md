# Phase 6.5 — High Contrast theme sweep + visual audit

Phase 6.5 wires the dormant `Resources/HighContrast.xaml` dictionary into
the app at runtime and resolves the P1 amber-banner dark-mode contrast
regression. CI does not cover any of this (HC is a runtime resource-merge
flip; the regression is a token-value choice). This doc walks the manual
gates that the human must verify on a real Windows box.

> Run non-elevated for UI inspection. No service-side changes — the
> existing service binary is fine; only the UI assembly changed.

---

## Pre-flight dependencies

No new external tools. Built-in surfaces only:

- **Windows Settings → Accessibility → Contrast themes** is the canonical
  HC toggle. (`Get-Help Settings -Online` does NOT exist; the UI is the
  contract.) Aquatic / Desert / Dusk / Night sky are the four shipped
  themes; any of them activates `SystemParameters.HighContrast = true`.
- Optional: **Win + Left-Alt + PrintScreen** is the OS-level HC keyboard
  shortcut for toggling whichever HC theme is currently selected.

---

## 0. One-time build

UI-only change. No service rebuild.

```powershell
# Non-elevated:
cd C:\dev\zenvizor
dotnet build .\ZenVizor.slnx -c Debug
dotnet test  .\ZenVizor.slnx -c Debug
```

Test totals to expect:

- `ZenVizor.Core.Tests` — 109 pass
- `ZenVizor.Storage.Tests` — 127 pass
- `ZenVizor.Ipc.Tests` — 54 pass
- `ZenVizor.Attribution.Tests` — 69 pass
- `ZenVizor.Integration.Tests` — 56 pass

**Total: 415 pass.** Same total as the end of Phase 6.4 — no new tests
land in 6.5 (HC is a runtime concern; no headless harness for OS theme
flips exists).

Launch the UI from the bin output:

```powershell
& .\src\ZenVizor.Ui\bin\Debug\net10.0-windows\ZenVizor.Ui.exe
```

---

## Gate 1 — P1 amber-banner dark-mode contrast

The pre-fix bug: dark-theme caution banners painted body text at `#8A5A00`
on the dark amber tint (`#2EF1AD34`), which is ~1.3:1 and unreadable. The
fix moves `status.caution.text` to a theme-aware brush owned by
`BrandAccent.{Light,Dark}.xaml` — `#8A5A00` in light (~5.2:1 on the pale
tint) and `#F1AD34` in dark (clears AA against the dark tint).

**Surfaces where the amber banner shows:**

- **Settings page** — when the service is older than the UI (the "Settings
  can't be loaded" informational banner) or any debounce-save error.
- **History / Per-App / App Detail / Alerts pages** — "Query failed" or
  "Warming up" warm banners.
- **Dashboard** — warming-banner above the live chart on cold start
  (first ~5s after service starts).

Two scenarios to walk:

1. **Force a banner in dark theme.** The fastest reliable path: temporarily
   stop the service so the next page load surfaces the disconnected-style
   caution banner. From elevated PS:
   ```powershell
   Stop-Service ZenVizor
   ```
   In the UI, navigate Settings → confirm the **body text is the bright
   amber** and readable against the dark tint. Repeat on History and
   Alerts. Then:
   ```powershell
   Start-Service ZenVizor
   ```
   and confirm the banners clear.

2. **Force a banner in light theme.** Settings → Appearance → Theme →
   Light, repeat the stop/start dance, confirm the body text is the **dark
   amber `#8A5A00`** and still legible against the light pale-amber tint.

If either contrast read is illegible (text invisible or close to the
background), the per-theme override on `status.caution.text` in
`BrandAccent.*.xaml` is wrong. Re-check the values match the
`docs/design/colors_and_type.css` crosswalk.

---

## Gate 2 — High Contrast merge + per-page audit

Open the UI BEFORE flipping HC. With a typical workload running, walk the
six data pages once in regular dark theme so you have the visual baseline.

Then enable HC: **Settings → Accessibility → Contrast themes → Aquatic →
Apply.** The Aquatic theme is the most aggressive (saturated cyan
backdrop + bright cyan highlight), so any token that didn't collapse
will jump out.

After ~1 second the ZenVizor window should re-paint. Confirm:

- **Theme card on Settings** — a soft amber caution-style notice appears
  beneath the Theme combobox: "Windows High Contrast is active. The OS
  contrast theme overrides this app's light or dark choice; changing the
  picker has no visible effect until High Contrast is turned off." The
  combobox itself still works — clicking through Light / Dark / Follow
  system should be a no-op for the rendered UI in HC.
- **Surface backgrounds collapse.** Cards should paint
  `SystemColors.ControlColor` (a flat HC surface) — NOT the brand
  metal-card gradient.
- **Text** is `WindowTextColor` (saturated against `Window`).
- **Accent / selection chrome** (NavigationView selected item, focus
  rings) should be `HighlightColor` — NOT brand violet.
- **No drop shadows** on cards (HC `shadow.card` is collapsed to
  `Opacity=0`).
- **No metallic gradient** on the brushed cards (`metal.card` collapsed
  to a solid `Control` fill).

**Per-page walkthrough.** Visit each page; expect each to be legible:

- **Dashboard** — live chart with axis/gridline visible, axis labels
  paint as `GrayText` (NOT the user's chosen-theme constant — the
  Phase 6.5 ChartTheming refactor reads tokens from the resource
  dict so HC overrides flow into SkiaSharp paints). "Up" and "Down"
  series distinguishable (collapsed to `Highlight` + `WindowText` —
  technically a known reduced contrast over Okabe-Ito but acceptable).
- **Per-App** — drill row hover chevron + cursor still resolve. DataGrid
  rows readable; hovered row tint should be `Highlight` at low alpha
  (the `accent.subtle` collapse).
- **App Detail** — drill list, connection table, time chart legible.
- **History** — bucketed chart axes labeled, tooltip readable.
- **Reports** — Notable Today card chip ("Alerts · #N") visible; click it
  and confirm Alerts deep-link still scrolls + highlights (the wink is
  intentionally skipped under HC — MotionPolicy gate).
- **Alerts feed** — severity tile background collapses to `Highlight`,
  text on the tile remains legible (`HighlightText`). Expand a card via
  the chevron — the chevron rotation animation still plays (XAML trigger;
  intentionally not gated, see "Known limits" below).
- **Settings** — Reset history modal: open it, click Cancel, confirm the
  modal scrim and dialog content paint with HC colours.

**Rotate through the other three contrast themes** (Desert / Dusk /
Night sky) at least once to confirm the brush re-resolution works without
needing an app restart. The `DynamicResource` against `SystemColors.*ColorKey`
in `HighContrast.xaml` is doing the re-paint work.

**Flip HC OFF.** Toggle back to the default Windows theme. ZenVizor's
brand violet / metal cards / drop shadows should return without an app
restart. The Theme card's HC notice should disappear.

---

## Gate 3 — Status banner sweep

Phase 6.5 standardized service-disconnect banners across every page:
**caution-amber background + `PlugDisconnected20` glyph + `status.caution.text`
foreground + "Service disconnected. ..." copy.** Pre-6.5 the pages
diverged: three were critical-red without a glyph, one was a Dashboard
amber-then-red split, two already followed the amber+icon pattern, and
Reports silently rendered the disconnect banner against an unresolved
`status.critical.text` token (no text foreground at all). All folded.

To exercise, stop the service from an elevated PS:

```powershell
Stop-Service ZenVizor
```

Then walk every data page. Each disconnect banner should now look the
same — amber band, plug glyph on the left, body text. The Dashboard
banner copy still distinguishes retrying (`"Service disconnected (...);
retrying."`) from steady-stale (`"Service disconnected (...). Last
refresh stale."`); only the color split was retired.

Pages to check: Dashboard, Per-App, App Detail, History, Reports,
Alerts, Settings. The Settings disconnect banner now also shows the
`PlugDisconnected20` glyph (pre-6.5 it had the icon element but never
set the symbol).

Restart the service:

```powershell
Start-Service ZenVizor
```

Banners clear; pages refresh. Re-trigger any single page's load to
confirm the non-disconnect amber-warning class (`Warning20` glyph,
`"Query failed (...)"` copy) still paints distinctly.

## Gate 4 — Critical text contrast on dark

Phase 6.5 introduced `status.critical.text` so body text on
`status.critical.background` separates cleanly in dark. Light theme:
`#D62B62` (same as `status.critical`; works fine on the pale tint).
Dark theme: `#FBA3B7` (light coral pink; clears AA against the dark
`#2EF5547F` tint over the slate card).

Surfaces to inspect in **dark theme** (the regression surface):

- **Alerts feed** — every Critical-severity alert tile: the 24-pt
  severity icon inside the tinted square should read as a lighter pink
  against the dark-pink tint, NOT the same washy hue. The small
  "Critical" pill next to the headline should be the same lighter pink.
- **Reports — Top Apps tile** with Unsigned / Invalid signature: the
  signature pill text + icon should read clearly. The icon-tile glyph
  inside the row's left tile should be the lighter pink.
- **Reports — Notable Today** Critical-severity card: the IconGlyph
  inside the tinted IconTile should be the lighter pink.
- **Reports — Risky Paths section header**: the shield-error glyph in
  the small tinted square should be the lighter pink.

Switch to **light theme** and confirm none of the above regressed — the
`#D62B62` light value matches the previous `status.critical` brand
magenta exactly, so light should look identical to pre-6.5.

In **HC** the `status.critical.text` token collapses to
`SystemColors.WindowText`, same treatment as the other text tokens. A
quick toggle through Aquatic should confirm critical text remains
readable as WindowText.

## Gate 5 — Reduced-motion check

Two animations are now gated on `MotionPolicy.AnimationsEnabled`
(`!SystemParameters.HighContrast && SystemParameters.ClientAreaAnimation`):

1. **Alerts nav-rail badge pulse** — fires on `AlertRaised` push. Under
   HC, the badge count still increments but the pulse ring is suppressed.
2. **Reports → Alerts deep-link wink** — fires on chip click. Under HC,
   the row appears at full opacity immediately (no 0.35→1.0 fade).

To exercise:

- **Badge pulse** — with HC ON, trigger a fresh alert (`Reset history`
  in Settings, then re-run the unsigned-from-user-path trigger binary).
  Confirm the Alerts badge **increments without a pulse ring**.
- **Deep-link wink** — with HC ON, click the `Alerts · #N` chip on a
  Notable Today card. The Alerts page should open, scroll the target
  alert into view, and **not** wink it (the row paints at 1.0 immediately).

Flip HC OFF and re-trigger both — pulse + wink should return.

---

## Known limits documented for the audit

These are accepted boundaries, not bugs:

1. **Desert (light HC) reads as a near-Dark UI.** ZenVizor's HC
   treatment is tuned for the three dark-backdrop HC themes
   (Aquatic / Dusk / Night sky). Desert is HC's only light-backdrop
   theme; under Desert, Wpf.Ui's own chrome surfaces (button
   backgrounds, ContextMenu, the NavigationView regions we don't
   override) continue painting from the user's pre-HC chosen theme
   (almost always Dark), so the UI reads as a dark surface with the
   Desert highlight color rather than the light-backdrop OS contract.
   Documented in `HighContrast.xaml` header. Fix path is either
   snapping the Wpf.Ui ApplicationTheme to Light when the OS HC
   theme is light, or overriding every Wpf.Ui chrome brush in the
   HC dict — both are substantive token walks deferred until a user
   depends on Desert. The HC notice on the Settings Theme card lets
   the user know the OS theme owns the chrome.

2. **`style.button.accent.fill` Style.Resources brushes do NOT collapse
   under HC.** The hover / pressed gradient brushes are inlined inside
   the Style's `Style.Resources` scope, which is not on the app resource
   surface, so `HighContrast.xaml` can't reach them. Buttons in HC will
   read brand-tuned on hover / press rather than collapsed. Comment-only
   note in `HighContrast.xaml`. Fix is a future refactor (lift to app
   resources or write an HC Style variant).

3. **Alerts expand-chevron rotation is not gated on MotionPolicy.** The
   animation is two XAML `BeginStoryboard` triggers on the rotation
   transform — gating from code requires lifting the triggers into
   code-behind for no real accessibility win (200 ms property change is
   functionally instantaneous and not a motion-vocabulary concern).

4. **Chart series palette collapses to three values** under HC
   (`Highlight` / `WindowText` / `GrayText`). The fixed Okabe-Ito 8-slot
   categorical ramp can't be honoured because HC has no equivalent
   distinct palette. Documented as a stance in
   `docs/design-system.md` "High Contrast strategy."

5. **`text.eyebrow` accent text reads as body text** under HC because
   `accent.text` collapses to `WindowText`. By design — HC has no
   "accent foreground text" concept.

---

## Pass criteria

The Phase 6.5 gate passes when:

- All four HC themes (Aquatic / Desert / Dusk / Night sky) flip cleanly
  with no app restart required.
- Every data page is legible (body text passes "can read at arm's length"
  without squinting).
- The amber caution banner reads cleanly in BOTH dark AND light non-HC
  themes (P1 regression resolved).
- Every page paints the same caution-amber + `PlugDisconnected20`
  banner when the service is stopped (Gate 3 sweep — no critical-red
  outliers).
- Critical text on `status.critical.background` is legible in dark mode
  (Gate 4 — lighter pink against the tinted backdrop instead of
  same-hue washy).
- The Theme card on Settings shows the HC notice while HC is active and
  hides it when HC is off.
- Badge pulse + Alerts wink are suppressed under HC (visible the rest
  of the time).
- No XAML error / first-chance exception in the debug output during HC
  flip events.

Document any surface that fails legibility in a follow-up note for a
6.5b pass — DO NOT block 6.6+ on per-page polish gaps unless body text
is genuinely unreadable.
