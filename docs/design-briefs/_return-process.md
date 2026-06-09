# Pre-handback checklist — paste into Claude Design at session end

Paste this prompt at the END of a Claude Design session, once the mock
direction is settled. It ensures the final mock carries everything
Claude Code needs to re-implement it as XAML.

Audience: Claude Design. Not Claude Code.

---

Before delivering the final mock, confirm each item below. Update the
mock or its annotations where any item is missing.

## 1. Token annotations

Every visual element is labeled with a canonical dotted-name token.
No raw hex values. No legacy CSS aliases (`--fg1`, `--fill-card`,
`--series-up`, etc.). Examples: `surface.card`, `text.secondary`,
`status.caution.background`, `radius.card`, `space.16`,
`chart.upSeries`.

## 2. State coverage

Every state listed in the brief's §4 is rendered. ONE steady-state
variant is additionally rendered in dark theme so theme-swap behavior
is auditable.

## 3. New tokens (if any)

Any token introduced by the mock that isn't in the primer's token
table is declared with:

- the canonical dotted name (`<category>.<role>[.<modifier>]`),
- the proposed value (hex, or `"alias of <existing token>"`),
- a one-line rationale for why a new token is needed.

## 4. New patterns (if any)

Any new visual pattern the mock introduces (component shape,
affordance, layout shape) carries a one-line statement of what the
pattern is. Example: `"Summary strip = 3-cell horizontal block,
caption eyebrow above mono value, surface.subtle background."` Lets
Claude Code implement it consistently when other screens adopt it.

## 5. Layout / density hints

Inline notes where they matter for implementation:

- `MinHeight=…`, `MaxHeight=…` where the layout requires a floor or
  cap.
- `scroll: pane` / `scroll: page` / `scroll: none` on the scrolling
  surface.
- `density: compact` / `density: default` on data grids and lists
  where it differs from default.

## 6. Copy strings

Empty-state, loading caption, disconnected banner, error banner —
delivered inline with the surface that paints them. No separate copy
aggregator section. Filter / placeholder text declared inline on the
input that holds it.

## 7. Variant selection (where applicable)

If the brief's §8.4 invited multiple variant proposals, the final
mock declares which variant was selected — clear enough that Claude
Code does not have to guess.

---

Output the final mock as a PDF with all items above confirmed.
