# Known bugs

Defects in ZenVizor that are reproducible but not yet fixed. Tracking them
here keeps the diagnosis work visible so future sessions don't restart from
zero and don't re-attempt approaches we've already ruled out.

## How to use this doc

- **Active** — bugs we can reproduce and have decided to accept for now.
  Each entry is self-contained: a future session should be able to pick it
  up cold.
- **Resolved** — once a fix ships, move the entry down here with the fix
  commit + a one-line "what worked" instead of deleting it. Future
  regressions of the same shape become much faster to diagnose with the
  receipt nearby.
- **Adding a new entry** — copy the template at the bottom. Be honest about
  what's still hypothesis vs. confirmed; future-you will thank present-you
  for the distinction.

---

## Active

### TRAY-01 — Tray context menu lingers after Exit click

**Surface:** UI — system tray context menu
**Severity:** Cosmetic. Exit path only. App functions correctly; process exits
cleanly within a few seconds.
**First observed:** 2026-06-03 (UI polish interlude, after `SystemThemeWatcher`
was wired and the menu styled correctly — pre-styling the menu was unstyled
but did not visibly linger).

**Symptom**

User right-clicks the system tray icon → clicks **Exit**. The main window
closes immediately. The tray context menu popup remains visible on screen for
roughly 3 seconds before disappearing. The process exits in the same window.

**What we know (verified against `dotnet/wpf` source)**

- H.NotifyIcon.Wpf v2.2.0 uses the stock
  `System.Windows.Controls.ContextMenu` with `PlacementMode.AbsolutePoint`.
  No custom dismiss mechanism; relies on WPF's standard Popup teardown.
- WPF `ContextMenu.HookupParentPopup` does
  `_parentPopup.SetResourceReference(Popup.PopupAnimationProperty, SystemParameters.MenuPopupAnimationKey)`
  on the inner Popup. The popup inherits the Windows system menu fade
  animation.
- `Popup._asyncDestroy` is a `DispatcherTimer` whose interval is
  `animating ? AnimationDelayTime : TimeSpan.Zero`. The popup HWND is
  destroyed (`DestroyWindow`) from that timer's tick.
- `Popup.Closed` (and thus `ContextMenu.Closed`) is raised from that same
  timer tick, **after** `DestroyWindow`. So by the time `Closed` fires the
  HWND should be gone.

**Attempts so far** (chronological — each verified non-working by the user)

1. **Synchronous `Close()` in click handler.** Window started tearing down
   before the popup HWND processed its hide message → menu stayed on screen
   while window closed. Worst variant.
2. **`Dispatcher.BeginInvoke(Close, DispatcherPriority.Background)`.** Same
   symptom — Background (priority 4) still runs before the dispatcher
   processes the popup's deactivation messages.
3. **`cm.IsOpen = false` synchronously + `BeginInvoke(Close, ContextIdle)` +
   removed `Tray.Dispose()` from `OnClosed`** (H.NotifyIcon's
   `TaskbarIcon.DisposeAfterExit` auto-hooks `Application.Exit` and disposes
   after dispatcher drains). Window now closes immediately, but menu still
   lingers — `ContextIdle` (priority 3) fires before `_asyncDestroy` timer
   ticks at `Input` priority.
4. **`ContextMenu.Closed` event subscription + override inner Popup's
   `PopupAnimation` to `None` via `LogicalTreeHelper.GetParent(cm) as Popup`.**
   No change. The `LogicalTreeHelper` parent of a tray ContextMenu may not
   actually be its hosting Popup, so the override likely never took effect;
   alternatively the animation is sourced from somewhere outside the WPF
   Popup model entirely.

**Hypotheses untested**

- The lingering popup may not be the WPF `Popup` HWND at all. It could be a
  DWM/composition-layer artifact (Win32 menu fade is partly driven by the
  desktop compositor, not by the popup HWND's own message pump). If so,
  nothing inside the WPF dispatcher loop can shorten it.
- H.NotifyIcon calls `SetForegroundWindow` on the menu HWND
  (`TaskbarIcon.ContextMenu.Wpf.cs` line ~46). That foreground transition
  may create a Win32-level activation cycle that has to unwind regardless
  of our dispatcher choices.
- `LogicalTreeHelper.GetParent(cm)` for a tray ContextMenu may return null
  (since the menu has no logical parent in our visual tree — it's
  programmatically attached to a `TaskbarIcon` that lives in a separate
  HwndSource). Without verifying that the cast succeeded, the
  `PopupAnimation` override silently no-ops.
- The system menu fade may be controlled by `SystemParameters.MenuFade` /
  user accessibility settings rather than by `MenuPopupAnimation`. If so,
  the override doesn't reach the actual fade driver.

**Possible next paths** (if/when this becomes worth fixing)

1. **Verify the `LogicalTreeHelper.GetParent` result.** Add a debug log to
   confirm the parent is actually a `Popup`. If it's null, the
   `PopupAnimation` override never ran. Try `VisualTreeHelper.GetParent`,
   or walk up the visual tree from the ContextMenu's child MenuItem.
2. **Try `Environment.Exit(0)` from the Exit click handler.** Bypasses the
   WPF shutdown path entirely; the OS reaps the popup HWND when the
   process terminates. Trade-off: no graceful cleanup
   (`SystemThemeWatcher.UnWatch`, poller dispose, tray dispose all skipped).
   Likely acceptable for the Exit path — UI is read-only and pollers cancel
   gracefully on process death.
3. **File an issue against HavenDV/H.NotifyIcon** — the tray sample they
   ship is windowless; nobody has empirically tested the
   ContextMenu-dismiss + Window-close interaction on the WPF target. Their
   issue tracker has nothing matching this symptom; we'd be the first
   report.
4. **Switch to Hardcodet.NotifyIcon.Wpf** (the older fork H.NotifyIcon
   derives from) and see if its menu-show path behaves differently.

**Workaround**

None. The lag is purely visual; nothing the user does meaningfully
mitigates it. App exits cleanly.

**Reproduce**

1. Launch `ZenVizor.Ui` via `dotnet run --project src/ZenVizor.Ui -c Release`.
2. Once the window appears, click the X to close-to-tray.
3. Right-click the tray icon in the system notification area.
4. Click **Exit**.
5. Observe: main window disappears immediately, tray popup stays visible for ~3 s.

---

## Resolved

*(none yet — when a fix lands, move the entry here with the resolving commit
hash and a one-line "what worked" note.)*

---

## Entry template

```markdown
### ID-NN — Short title

**Surface:** Which part of the app
**Severity:** Critical / Major / Minor / Cosmetic
**First observed:** YYYY-MM-DD (context — phase, commit, recent change)

**Symptom**

What the user sees, concretely. Include a screenshot path if one exists
under `debugging/screenshots/`.

**What we know**

The diagnosis we've actually confirmed (vs. guessed). Cite source files /
line numbers / external issue links where applicable.

**Attempts so far**

Chronological list. For each: what was tried, why we thought it would
work, what actually happened. Failures are as valuable as successes here.

**Hypotheses untested**

Ideas we haven't gotten to. Keeping them written down stops the next
session from re-deriving the same list.

**Possible next paths**

Concrete actions to try when the bug is revisited.

**Workaround**

Anything the user can do meanwhile.

**Reproduce**

Numbered steps.
```
