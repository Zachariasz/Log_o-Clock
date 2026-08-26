# Log O'clock 1.142.4

## Two-way Google Sheets whole-profile synchronization

- Upgraded Google Sheets integration to support bidirectional synchronization of the full profile state using a structured cloud revision journal (`_time_tracker_sync_journal`).
- Synchronizes completed entries, clients, projects, tasks, tags, software associations, window rules, targets, target debt adjustments, and profile settings.
- Added device presence heartbeat and remote running timer status indication.
- Added `SyncConflictReviewWindow` for explicit interactive resolution of concurrent edits.
- Maintains strict local-first resilience: local tracking, SQLite persistence, and monthly CSV safety logs remain operational when offline or on network failure.

## Reports & Calls metric

- Added a dedicated **Calls** duration column in Reports to track time spent in call entries.
- Synchronized column visibility and widths across project groups and total rows.

## Project freezing

- Added support for freezing projects from the Projects context menu to temporarily pause active use without deleting data or breaking history.

## Verification

- Extended test suite to 293 passing automated unit and store tests.
- Validated SQLite schema 27 migrations, reconciliation flows, and conflict review dialogs.
