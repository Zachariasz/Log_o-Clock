# Log O'clock data audit — 7 August 2026

The audit was read-only. The running application and the production files were not stopped or modified.

## Active storage

- Running executable: `C:\Users\zacha\Downloads\LogOClock.exe`, version 1.140.2.
- Active profile: `Work` (the root/default profile).
- Active database: `C:\Users\zacha\OneDrive\Documents\TimeTracker\TimeTracker.db`.
- Database schema: 21.
- The `PROJECT_TIME_TRACKER_DATA_DIR` override is not set at process, user, or machine level.
- No second current production database was found. The other database under `%LocalAppData%\ProjectTimeTracker` is the retained July 14 legacy source.

## What is present

- Current database: 150 time entries and 50 saved tasks.
- Entries from 1–7 August: six completed entries on 4 August and one completed manual entry on 7 August.
- There are no database entries dated 5 or 6 August.
- Saved tasks: 34 active local tasks and 16 archived local tasks.
- The August monthly CSV agrees with the completed database rows. It also contains one stale running-entry export from 7 August that is no longer in the database, consistent with the under-one-minute cleanup occurring after that CSV write.

## Backup comparison

The current database was compared with `TimeTracker.db.backup-v19-20260801170618`:

- August 1 backup: 143 entries and 49 tasks.
- Current: 150 entries and 50 tasks.
- One task was added after the backup.
- No task found in the backup is missing from the current database.
- No task changed archive state between the two files.

## Ruled-out causes

- The app rename did not redirect this profile to a new empty database.
- Trello synchronization did not remove the tasks: there are no Trello board mappings or external task links in this profile.
- No client or project disappeared between the August 1 backup and the current database.

## Limits and recovery lead

The current schema has no mutation audit ledger, so it cannot prove why expected August 5–6 records were never committed or were later removed. Those records are not recoverable from the current database, monthly CSVs, the August 1 backup, or an alternate local database found during the audit.

Because the active `TimeTracker` folder is inside OneDrive Documents, OneDrive version history is the remaining plausible recovery source. Inspect older versions of `TimeTracker.db` and the August CSV from 5–7 August before replacing anything. Work only on copies; restoring the production database directly could overwrite newer entries.
