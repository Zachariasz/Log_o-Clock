# Log O'clock 1.142.3

## GitHub update checks

- Added automatic and manual update checking against official GitHub releases (`IGitHubReleaseClient`, `UpdateCheckService`).
- Added an **Updates** subtab in Settings allowing users to enable/disable automatic background update checks, trigger an immediate check, and view the last check timestamp.
- Displays an update-available banner/link in the shell directing the user to the published GitHub release page when a newer version is detected.

## Work break reminders

- Added configurable periodic break reminders based on accumulated continuous net work time.
- Added a **Break reminders** subtab in Settings to configure notification intervals (e.g. 30, 45, 60 minutes) and enable/disable reminders.
- Implemented `BreakReminderWindow` with dark styling, snooze capabilities, and smooth dismissed state management.

## Settings navigation structure

- Restructured Settings into clear category subtabs (General, Session & Idle, Break reminders, Updates, Trello, Google Sheets).

## History view global sorting

- Refined multi-day History column sorting across Client and Project columns with smooth reset back to default day grouping when cleared.

## Verification

- Added unit tests for GitHub release parsing, update check state transitions, and break reminder settings.
- Validated with full 281-test automated suite and WPF smoke tests across Settings, Break Reminders, History Sorting, and Reports.
