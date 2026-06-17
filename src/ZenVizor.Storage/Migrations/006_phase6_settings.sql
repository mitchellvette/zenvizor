-- Phase 6 introduces the Settings page surface. Two new keys are seeded
-- here so they exist before the SettingsRepository round-trips them; users
-- who hand-edited via SQL keep their value via INSERT OR IGNORE.
--
-- autostart.mode mirrors the SCM start mode for the ZenVizor service. The
-- SCM value is authoritative; this row is the UI's cached last-known view
-- (the Settings page reconciles on every load via ServiceStartModeManager).
-- 'Automatic' = boot-start; 'Manual' = user-launched. 'Disabled' is a valid
-- SCM enum value but not exposed in the UI per the §6.2 Q3 decision.
--
-- appearance.theme drives the WPF theme override. 'system' = follow OS
-- (SystemThemeWatcher); 'light' / 'dark' = explicit override that unwires
-- the watcher. The UI also caches this at %LocalAppData%\ZenVizor\ui.theme
-- so startup theming doesn't block on the service pipe.

INSERT OR IGNORE INTO settings (key, value) VALUES
    ('autostart.mode',      'Automatic'),
    ('appearance.theme',    'system');
