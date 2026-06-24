# Epic F — App-detail path actions (Reveal in Explorer)

**Release:** 1.5.0 (minor) · bundled with Epics G + I (tiny on its own) ·
**Status:** spec
**Depends on:** nothing

---

## Summary

The app-detail page's image-path row supports copy-to-clipboard only. Add a
**Reveal in Explorer** action that opens Windows Explorer with the binary
selected, so a user investigating an alert can jump straight to the file on
disk. The binary itself is never executed.

## Current behavior (verified)

- The path row is a click-anywhere `Border` bound to `MouseLeftButtonUp` →
  `OnCopyPathClick` (`AppDetailPage.xaml.cs:391`), which copies
  `_lastSummary.ImagePath` via `TryCopyToClipboard` and shows the "Copied to
  clipboard" toast.
- `_lastSummary` is the `AppListEntry` for the app — carries `ImagePath`,
  `SignatureStatus`, `IsUserWritablePath`.
- There is no reveal / open-containing-folder action today.

## Scope

**In:**
- A **Reveal in Explorer** action that runs
  `explorer.exe /select,"<image path>"` so the binary is highlighted in its
  folder.

**Out:**
- Launching the file. Never `Process.Start` the image path directly — this is
  a security tool inspecting potentially-hostile binaries.
- Anything beyond a graceful message when the path is gone.

## Design

- **Affordance.** The row already maps click → copy (drill-grid muscle
  memory). Add a *distinct* explicit affordance for reveal rather than
  overloading the row click. Recommend a small icon button (Folder/Open
  glyph) revealed on row hover, mirroring the hover-copy-chip pattern in the
  Connections grid (`OnCopyEndpointClick`). A right-click context menu with
  "Copy path" + "Reveal in Explorer" is the alternative. **Open decision.**
- **Mechanism.**
  ```
  Process.Start(new ProcessStartInfo
  {
      FileName        = "explorer.exe",
      Arguments       = $"/select,\"{path}\"",
      UseShellExecute = true,
  });
  ```
  `/select` highlights the file without executing it.
- **Missing-file guard.** If `File.Exists(path)` is false (app moved /
  uninstalled since capture), `/select` would open Explorer with nothing
  selected. Guard: if the file is gone but the parent dir exists, open the
  parent; otherwise show a "File not found" toast (reuse `ShowToast`).
- **Path handling.** The path comes from our own DB (`apps.image_path`), not
  user input, but still quote the `/select` argument. Do not build a shell
  command string — pass `explorer.exe` + arguments via `ProcessStartInfo`.

## Invariant guards

- **Invariant 1 (zero own network):** `explorer.exe /select` is a local
  shell action, no network. Safe.
- **Never execute the binary** — `/select` only highlights. This is the
  load-bearing security property of the feature.
- **Invariant 3 (non-elevated UI):** Explorer launches in the user's context.
  System-folder binaries are browsable (Explorer honors ACLs); no elevation
  is requested.

## Open decisions

1. **Affordance:** hover icon-button (recommended) vs. context menu vs. split
   the row into copy + reveal zones.
2. **File moved/deleted:** open parent dir (recommended) vs. toast-only.

## Acceptance criteria

- Reveal opens Explorer with the binary highlighted in its folder.
- A missing file falls back to the parent dir, or a clear "File not found"
  toast — never a silent failure or an unhandled exception dialog.
- The binary is never launched.
- Existing copy-path behavior is unchanged.

## Version classification

**1.5.0 (minor).** New user-facing control. No contract change. Bundled with
Epics G + I in 1.5.0.
