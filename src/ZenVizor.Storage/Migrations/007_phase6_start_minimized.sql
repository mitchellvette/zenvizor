-- Phase 6.3 — Tray polish adds a "start minimized" UI preference.
-- Boolean (0/1). Default 0 — manual launches show the window normally.
-- Set to 1 to make UI launches drop straight to the tray (the boot-time
-- silent-launch surface). Mirrored to %LocalAppData%\ZenVizor\ui.start-minimized
-- so App.OnStartup can read the value synchronously before MainWindow
-- paints (no IPC dependency on the critical-path startup frame, mirrors
-- the appearance.theme pattern).

INSERT OR IGNORE INTO settings (key, value) VALUES
    ('ui.start_minimized', '0');
