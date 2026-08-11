# Log O'clock repository guidance

Before changing this project, read:

1. `docs/NEW_CONTEXT.md`
2. `docs/ARCHITECTURE.md`
3. The relevant row in `docs/FEATURE_MAP.md`
4. `docs/DATA_AND_STORAGE.md` for persistence/integration changes
5. `docs/CodexDarkDesignRules.md` for UI changes

The root `README.md` is the detailed implemented-feature inventory. Source and tests take precedence over historical notes in `outputs`.

## Critical rules

- SQLite is the per-profile source of truth. CSV, Google Sheets, and daily backups are derived outputs.
- Store timestamps in UTC and convert to local time only at display/calendar boundaries.
- Preserve the single-running-entry invariant and transactional timer switch/split operations.
- Net duration is `end - start - exclusions`; completed net entries below 60 seconds are deleted.
- Never persist unknown foreground-window titles or audio/media observations.
- Trello is read-only inbound task sync. Google Sheets is outbound export/merge and does not import entries into SQLite.
- Client/project removal is destructive; task-only removal generally remains archival for history.
- Keep every profile's database, settings, exports, mappings, and credentials isolated.
- Use semantic WPF resources and preserve named controls, context menus, double-click actions, keyboard handling, click-away persistence, and smooth scrolling.
- After direct store mutations from UI code, raise the controller data-change notification when the UI/tray/cloud queue must refresh.
- Schema changes require a `SchemaVersion` bump, guarded migration, upgrade tests, and compatibility with the existing pre-upgrade backup.

## Verification

- Build with zero warnings; warnings are errors.
- Run the complete automated test suite.
- Run the relevant embedded WPF smoke flag with an isolated `PROJECT_TIME_TRACKER_DATA_DIR`.
- For releases, update all three version sources, package the self-contained app/MSI, smoke-test the packaged executable, and refresh changelog/checksums.

See `docs/DEVELOPMENT_AND_RELEASE.md` for exact commands and smoke-test conventions.

