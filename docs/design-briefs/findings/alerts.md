# Pre-brief — Alerts (spec-derived, Group B)

Placeholder screen today. Derived from `docs/zenvizor-prd.md` §6, §7.6,
§9 + `docs/zenvizor-sprint-plan.md` Phase 6.

---

## 1. Purpose & IA placement

- **Purpose:** feed of raised alerts with an acknowledge flow. PRD §6
  calls Alerts "seam #2" — the alert pipeline is one of the three
  structural seams designed in from the start. MVP wires exactly one
  real alert (PRD §6, Phase 6: unsigned binary from a user-writable
  path making network connections).
- **IA placement:** fifth item in the left nav rail. `Symbol="Alert24"`.
  `NavigationCacheMode.Enabled`. Today routed to `AlertsPage :
  PlaceholderPage` with subtitle "Generic alert feed (seam #2) —
  Phase 6."

## 2. Requirements lifted from the spec

### Alert entity (PRD §7.6)

| Field | Type | Notes |
|---|---|---|
| `alert_id` | INTEGER PK | |
| `type` | TEXT | Extensible string (`UnsignedFromUserPath` is the MVP type) |
| `severity` | TEXT | `Info` | `Warning` | `Critical` |
| `created_at` | INTEGER | Unix ms |
| `source_monitor` | TEXT | Which `IMonitor` raised it |
| `entity_kind` | TEXT | `App` | `Session` | (future) `Device`, `File` |
| `entity_ref` | TEXT | Id / key of the referenced entity |
| `title` | TEXT | Short headline |
| `detail` | TEXT | Longer copy |
| `acknowledged_at` | INTEGER NULL | Null until acknowledged |

### IPC surface (PRD §9.1 / §9.2)

- `GetAlerts(filter)` → query existing alerts.
- `AcknowledgeAlert(alertId)` → mark acknowledged.
- **`AlertRaised(alert)`** server-push notification — drives the
  live feed (and the optional toast).

### Phase 6 scope (sprint plan)

- Alert pipeline + entity + `GetAlerts` / `AcknowledgeAlert` IPC +
  `AlertRaised` push + Alerts feed UI + optional toast.
- **First real alert:** unsigned binary from user-writable path
  making network connections. Derivable purely from data we already
  have — no new monitor, no network call.
- Retention: alerts kept until acknowledged + 90 days, configurable
  (PRD §7.9).

## 3. Proposed layout & interaction (durable layers)

> The layout below locks the visual language, IA, layout, and
> interaction. Field-level binding decisions (severity-to-color
> mapping refinements, filter chip vocabulary, "Show more" copy
> details) are provisional. See §5.

Root: `<Grid Margin="24">` with rows
(`Auto / Auto / *`).

### Row 0 — header

- `<ui:TextBlock>` with `Style="{StaticResource text.subtitle}"`,
  content "Alerts".
- `text.caption` `text.secondary` subtitle: "Local observations
  flagged for review. ZenVizor never blocks — it observes." (The
  second sentence is editable; the first locks the framing.)
- Right-aligned filter strip — three filter chips:
  - `[All]` (default selected)
  - `[Unacked]`
  - `[Acked]`
  Chip = small `ui:Button` styled with `radius.pill` (half-height)
  + `accent.fill` background when selected, `surface.layer` +
  `text.secondary` foreground when unselected. Toggle on click;
  multi-selection NOT required (radio-like behavior).

### Row 1 — count strip

- `text.caption` `text.secondary` line: `"N alerts · M unacked"`.
  Auto-updates as `AlertRaised` notifications arrive.

### Row 2 — alert feed

- Vertical scrolling list of alert cards. `<ListView>` with custom
  `ItemTemplate` — NOT DataGrid (rows are heterogeneous, the body
  wraps, severity is rendered as a bar).
- ItemContainer style: `Padding=space.12,space.8`,
  `Margin=0,space.4,0,0`.

#### Alert-card template

A horizontal grid (`8px / Auto / *` columns):

1. **Severity bar** — left edge, `Width=4`, full row height. Color:
   - `Critical` → `status.critical`
   - `Warning` → `status.caution`
   - `Info` → `status.neutral`
2. **Type icon** — `ui:SymbolIcon` in `space.32` size, foreground
   tracks severity. Icon picked from the `type` string (MVP:
   `UnsignedFromUserPath` → `ShieldDismiss24` or `ShieldQuestion24`;
   icon-to-type lookup is provisional, see §5).
3. **Content column** — vertical StackPanel:
   - **Title** — `text.body.strong`, copy = `alert.title`.
   - **Detail** — `text.body`, `TextWrapping=Wrap`,
     `MaxHeight=text.body.LineHeight * 3` (i.e. 3 lines), then "Show
     more" link (`text.eyebrow` styled link or `accent.default`
     foreground) that expands to full detail.
   - **Metadata strip** — `text.caption` `text.secondary` line
     with three pieces, pipe-separated visually but rendered as
     three TextBlocks with `space.8` gap:
     `text.mono` `2026-06-03 14:32` · source: `<source_monitor>` · ref:
     `<entity_kind>/<entity_ref>`.
   - **Acknowledge button** — `ui:Button` with `SymbolIcon
     CheckmarkCircle20` + text "Acknowledge". Right-aligned beneath
     the metadata strip. Disabled when `acknowledged_at` is non-null;
     button text becomes "Acknowledged" with `text.tertiary`
     foreground.
- Acknowledged cards: `Opacity=0.6` + severity bar foreground
  fades to `text.tertiary`. Still in the feed when the `All` or
  `Acked` filter is active.

### Detail expansion (NOT a flyout in MVP)

- "Show more" expands the card inline (the card grows vertically).
  No flyout / no separate detail page in MVP — fits the simplicity
  of the alert payload.
- If the brief proposes a flyout for detail (e.g. for future
  entity-kind=Device alerts with rich detail), it MUST be opaque +
  `surface.layer` per the locked decision. Feature-class, not MVP.

## 4. State coverage

| State | Treatment |
|---|---|
| empty (no alerts in filter) | Centered `ui:SymbolIcon ShieldCheckmark48` (`text.tertiary` foreground) above `text.body` `text.secondary` "No alerts in this view." |
| loading (initial GetAlerts query) | Default Fluent `ProgressRing` centered in the feed viewport. No shimmer. |
| disconnected (pipe down) | `status.critical.background` banner inline beneath header: "Service disconnected — last refresh stale." Filter chips remain operative (no-op while disconnected). Acknowledge buttons disable. |
| error (query failed) | `status.caution.background` banner: "Failed to load alerts: `<msg>`". |
| live tick (AlertRaised push) | New card prepends to the top of the list with a one-second `status.critical.background` / `.caution.background` flash that fades to the card's resting color. **Single fade animation, ~200ms each direction** — no continuous animation, honors the light-and-fast principle. |
| no warming state | History-ish surface; queries SQLite. |

## 5. Provisional-data flag — MANDATORY

The brief **locks** the durable layers:

- Visual language (tokens, type ramp, density).
- IA placement and the three-zone composition (header + filter /
  count / feed).
- Alert card template (severity bar, icon, title, wrapped detail,
  metadata strip, ack button).
- Interaction model (filter chips, acknowledge flow, inline detail
  expansion).
- State matrix.
- Severity-to-color mapping using the three status tokens.

The brief **flags as provisional**, to lock at Phase 6
implementation:

- Exact `Alert` entity rendering — PRD §7.6 fixes the schema, but
  which fields appear in the metadata strip vs the detail body is
  presentation that should track Phase 6.
- Severity-to-color mapping refinements beyond the three semantic
  statuses (e.g. if a fourth severity emerges).
- Filter chip vocabulary — `All` / `Unacked` / `Acked` are the
  MVP-minimum filters. Additional filters (`by type`, `by source`,
  `by entity_kind`) are explicitly out of scope for Phase 6 polish
  (feature-class).
- Type-to-icon mapping. MVP knows one type (`UnsignedFromUserPath`).
  The lookup function `iconForType(string)` is provisional —
  more types arrive post-MVP as future `IMonitor`s land
  (PRD §10).
- Toast notification visual — toast is a Win11 system surface
  (`ToastNotificationManager`); it doesn't take ZenVizor tokens.
  Out of mockup scope; the brief notes the trigger exists, not the
  visual.

## 6. Two designed states — MANDATORY

### (a) Interim placeholder treatment

Wears in the shipped polish-interlude app between now and Phase 6.

- Centered StackPanel on `surface.background`.
- `<ui:SymbolIcon Symbol="Alert48"
  Foreground="{DynamicResource text.tertiary}">`.
- `text.title.large` "Alerts".
- `text.body.large` `text.secondary` "Coming in Phase 6 — feed of
  raised observations with acknowledge flow."
- Optional `text.caption` `text.secondary` "First alert:
  unsigned binaries from user-writable paths making network
  connections."

### (b) Eventual functional layout

The layout in §3 above. The brief asks for both mocks side by side.

## 7. WPF translation gotchas

- **`ListView` with custom `ItemTemplate`:** same
  `NavigationView` infinite-extent caveat — set `MaxHeight`
  programmatically on `Loaded` + `SizeChanged` so the inner
  `VirtualizingStackPanel` virtualizes. Memory:
  `project_wpfui_navigationview_scrollviewer.md`.
- **Filter chips** in Wpf.Ui: there is no first-class "chip"
  control. Implement as a `ui:Button` with a custom `Style` keyed
  `style.chip.filter` — `radius.pill` (half-height), `space.4`
  vertical padding, `space.12` horizontal. Add the style to
  `DesignTokens.xaml` as part of the polish-pass call-site sweep.
  Annotate the chip with that style name in the mockup.
- **`AlertRaised` push wiring:** the service raises via
  `IZenVizorIpc` push (StreamJsonRpc supports notifications). The
  UI subscribes once on `AlertsPage` ctor; unsubscribes on
  `Unloaded`. **The Page's `Loaded` does NOT refire on nav-rail
  revisit** because of `NavigationCacheMode.Enabled` — wire the
  push subscription via the constructor (Page is created once),
  not via `Loaded`.
- **Toast notification:** Windows 11 `ToastNotificationManager`
  takes XML payload; no design tokens apply. The brief notes the
  trigger; visual fidelity is OS-owned.
- **Inline detail expansion:** the card grows the ListView's row
  height. WPF `ListView` with `VirtualizationMode=Recycling` does
  NOT play well with variable row heights — switch to
  `VirtualizationMode=Standard` for this ListView OR set
  `VirtualizingPanel.ScrollUnit=Pixel`. Either way, document the
  behavior in the brief so the implementer doesn't blindly copy
  Per-App's settings.

## 8. Out-of-scope — features flagged for later

- **Multi-select acknowledge.** Adds bulk action — feature.
- **Filter beyond `All` / `Unacked` / `Acked`.** Filtering by type
  / source / severity is feature-class.
- **Alert detail flyout / dedicated detail page.** Inline expansion
  covers MVP needs. If added later, opaque + `surface.layer`.
- **Sound / system notification beyond Windows toast.** Toast is
  the OS-canonical surface; custom sounds = feature.
- **Snooze / mute by type.** New per-type state. Feature.
- **Forward / share alert** (email, copy link). Hard NO on email
  per zero-own-traffic; copy-to-clipboard is fine but feature-class.
- **Active-action affordances on alert cards.** No "kill this
  process," no "block this app." Passive-only invariant —
  HARD NO.
