# Tracked follow-ups

Passive, deferred items that surfaced while shipping epic work but
weren't in scope for the release that uncovered them. None of these
block a release — they're parked here so they don't get lost as more
users land and bug/improvement signal arrives.

Each item names the release that surfaced it, the symptom, and the
shape of the fix. Items are removed from this file when shipped
(absorbed into the next release that picks them up) or reclassified
(promoted to their own epic, or closed as won't-fix with rationale).

---

## UI polish

### Chart axis label rendering (SkiaSharp)

- **Surfaced in:** 1.1.0 (Epic A rendering-discipline sweep).
- **Symptom:** History + AppDetail chart X/Y axis labels remain soft
  after the app-wide WPF rendering-discipline trio
  (`UseLayoutRounding` + `TextOptions.TextFormattingMode=Display` +
  `TextRenderingMode=ClearType` + `TextHintingMode=Fixed`) was applied
  at every tree root.
- **Why the trio doesn't reach them:** LiveCharts2 renders axis labels
  via SkiaSharp's own glyph rasterizer, not WPF. They never inherit
  from the MainWindow / Page tree.
- **Fix shape:** tune `ChartTheming.cs` / `SKPaint` — subpixel text,
  hinting level, typeface choice. Distinct from any WPF-side change.

### HWND-owning popup text rendering

- **Surfaced in:** 1.1.0 (same sweep).
- **Symptom:** WPF `Tooltip`, `ContextMenu`, and `ComboBox` dropdowns
  may render text softer than the rest of the app.
- **Why the trio doesn't reach them:** these live in their own HWND
  and do NOT inherit from MainWindow or the hosting Page.
- **Fix shape:** implicit App-level styles (e.g.
  `<Style TargetType="ToolTip">` with the same setters as the root
  trio). The Epic-A custom-range flyout was built as an in-page
  overlay specifically to sidestep this; the rule for new larger
  popover surfaces is already captured in `docs/design-system.md` §9.

### App-wide Wpf.Ui v4.0.2 ComboBox compact style

- **Surfaced in:** 1.1.0 (Epic A custom-range time pickers).
- **Symptom:** Wpf.Ui's default ComboBox template reserves substantial
  internal padding for the chevron, clipping narrow content (the
  hour / minute / AM-PM pickers needed `Padding="6,2,2,2"` + wider
  widths to lay out cleanly).
- **Fix shape:** keyed `Style x:Key="combobox.compact"` overriding the
  template padding once. Apply across filter inputs, settings
  dropdowns, and any future narrow-content combos.

### App-wide Wpf.Ui v4.0.2 NumberBox compact + clamp pass

- **Surfaced in:** 1.1.0 (Settings → Alert threshold polish).
- **Symptom:** the default NumberBox chrome has a clear-X button
  inherited from `Wpf.Ui.Controls.TextBox` that can wipe a value in
  one click; the default `ValidationMode=InvalidInputOverwritten`
  reverts (rather than clamps) on overflow. Fixed locally for the
  three Settings → Alert threshold NumberBoxes
  (`ClearButtonEnabled=False` + widen + `ValidationMode=Disabled` +
  commit-time `Math.Clamp` in the handler).
- **Outstanding:** the five Retention NumberBoxes on the same page
  share the same chrome and behaviour, but typical retention values
  (days / months / years) fit in 3 digits + chrome so the issue was
  never user-flagged. Same three-line fix pattern applies.
- **Fix shape:** keyed `Style x:Key="numberbox.compact"` if the
  pattern repeats elsewhere; otherwise apply the three-line fix
  in-place to the Retention five.

---

## Why this list lives here

The Epic-A roadmap spec is the canonical record for *that epic*; it
notes its own scope and outcome. Items that *surfaced* during Epic A
but apply *app-wide* belong here, not buried in Epic A's spec, so the
next person reading them (a) finds them without knowing which release
introduced them and (b) doesn't have to read a closed epic to discover
open work.
