# ZenVizor mockup annotation guide

Instructions to Claude Design (claude.ai/design) for annotating ZenVizor
mockups so the hand-off back to Claude Code is mechanical.

The contract: mockup annotations reference design tokens by **semantic
name**. Claude Code re-implements every mockup by hand as idiomatic XAML
against Wpf.Ui; nothing produced in Claude Design is portable to WPF
directly. Stable token names are the only thing the two tools share.

Source of truth: `docs/design-system.md`. The condensed paste-into-design
projection is `docs/claude-design-primer.md`.

---

## 1. Reference tokens by semantic name, never by raw value

✅ "background = `surface.card`"
✅ "label color = `text.secondary`"
✅ "outer margin = `space.24`"

❌ "background = #1B1B1B"
❌ "label color = 70% white"
❌ "outer margin = 24px"

If a hex code or pixel value appears in an annotation, the implementer has
to guess which token you meant — that's where drift starts.

If a mockup needs a value that has **no existing token**, see §6 (new
token convention) — name it first, use the name in the annotation, and
list it in the design-system table.

---

## 2. Label every component with the WPF control it represents

Every box/group on the mockup needs a control label so Claude Code knows
what to type. Use these labels (verbatim — they map 1:1 to existing
Wpf.Ui / WPF controls already in the codebase):

| Mockup intent          | Control label              |
|------------------------|----------------------------|
| App-chrome window      | `ui:FluentWindow`          |
| Title bar              | `ui:TitleBar`              |
| Left nav rail          | `ui:NavigationView`        |
| Nav rail item          | `ui:NavigationViewItem`    |
| Inline glyph           | `ui:SymbolIcon`            |
| Text run               | `ui:TextBlock` / `TextBlock` |
| Push button            | `ui:Button`                |
| Dropdown               | `ComboBox`                 |
| Card container         | `Border`                   |
| Row container          | `StackPanel` / `Grid`      |
| Tabular grid           | `DataGrid`                 |
| Simple list (Dashboard talkers) | `ListView`        |
| Series chart           | `lvc:CartesianChart`       |
| Status dot             | `Ellipse`                  |
| Loading spinner        | `ui:ProgressRing`          |
| Tray context menu      | `ContextMenu`              |

If a component in a mockup doesn't fit any of these, propose the closest
Wpf.Ui control + the property override in the annotation (e.g. "treat as
`ui:Card` with `Padding=space.16`") and the implementer can decide.

---

## 3. Specify density where it varies

Three density levels exist:

- `density: comfortable` — Wpf.Ui defaults
- `density: default` — current ZenVizor look (mid-tight)
- `density: compact` — for data-dense `DataGrid`s; maps to
  `style.datagrid.compact` (RowHeight 22, cell padding 6,2, body font size)

If a mockup doesn't specify density, the implementer assumes `default`.
Label `compact` explicitly on any data grid (Per-App, Connections,
Sessions). The Dashboard's ListView is `default` and should stay there.

---

## 4. Call out states

Default treatment is the unannotated mockup. Surface non-happy states
explicitly with one of these tags:

- `state: default`
- `state: hover`
- `state: pressed`
- `state: disabled`
- `state: empty` — surface has loaded but has no data to show
- `state: loading` — surface is fetching, no data yet
- `state: warming` — capture started, first flush bucket pending
- `state: disconnected` — service pipe is down
- `state: error` — query failed for any other reason

Where a state changes the surface's typography, color, or copy, annotate
the changed tokens explicitly (e.g. "state: disconnected — banner
background = `status.critical.background`, foreground = `status.critical`,
copy = 'service disconnected (\<reason\>)'").

The per-screen state matrix in `docs/design-system.md §2` lists which
states each page actually needs — match it; don't invent extra states.

---

## 5. Include explicit layout hints

WPF row virtualization, NavigationView's `DynamicScrollViewer`, and
`NavigationCacheMode.Enabled` together make layout subtle. If a mockup
has any of these characteristics, annotate them so the implementer doesn't
miss them:

- **`MinHeight: <n>` / `MaxHeight: <n>`** — for cards/grids that need to
  cap their height so the inner DataGrid virtualizes. The implementer
  also has to wire `EnforceDataGridBounds`-style code in code-behind on
  `Loaded` + `SizeChanged`. Note when this applies.
- **`MinHeight: 180`** on chart cards — current convention; chart shrinks
  awkwardly below this.
- **`scroll: page`** vs **`scroll: pane`** — does the whole page scroll,
  or does only a region inside the page scroll? Pages today let the
  NavigationView scroll the whole pane; a card-internal scroll is a
  deliberate choice that needs a `MaxHeight` on the inner control.
- **`navigationCache: enabled`** — every page in MainWindow.xaml.cs is
  cached, so `Loaded` does NOT refire on nav-rail revisit. If the
  mockup adds new bounds-enforcement code (a new grid, etc), call out
  that it must be wired on `SizeChanged` too, not just `Loaded`.

---

## 6. Naming new tokens

When you introduce a token that doesn't exist yet, name it with the
established pattern:

```
<category>.<role>[.<modifier>]
```

- `<category>` — `surface`, `text`, `accent`, `status`, `border`, `chart`,
  `space`, `radius`, `font`, `density`.
- `<role>` — what it's for (`primary`, `success`, `card`, `body`).
- `<modifier>` — optional sub-role (`alt`, `background`, `compact`).

Examples that follow the pattern:

✅ `surface.card.elevated`
✅ `text.link.hover`
✅ `chart.series.9`

❌ `cardBackgroundElevated` — wrong case, missing dots
❌ `linkHoverColor` — wrong case, missing dots
❌ `series9Chart` — wrong category order

Dotted lowercase literal form is **the contract**. Never PascalCase. Never
strip the dots. Claude Code uses the literal string as the `x:Key`.

When a new token is needed, list it in the mockup-handoff notes so it
gets added to `docs/design-system.md` and
`src/ZenVizor.Ui/Resources/DesignTokens.xaml` before the XAML
implementation begins.

---

## 7. Annotation example

For reference, this is what a well-annotated card on the App Detail page
looks like:

```
[Border]   surface: surface.card
           border: border.card, 1px
           radius: radius.md
           padding: space.12
           margin-bottom: space.12

  [TextBlock]   font: font.display, font.size.subtitle, font.weight.semibold
                color: text.primary
                copy: "chrome.exe (app id 42)"

  [TextBlock]   font: font.display, font.size.body
                color: text.secondary
                margin-top: space.4
                copy: "Publisher: Google LLC  |  Signature: ValidEmbedded  |  Grain: Hourly"

  state: disconnected
    [Border]   background: status.critical.background
               foreground: status.critical
               copy: "service disconnected — query stale"
```

That's everything the implementer needs to produce a 1:1 XAML diff.
