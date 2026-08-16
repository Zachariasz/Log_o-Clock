# New-context handoff

## Project identity

- Product: **Log O'clock**.
- Author: **Zachariasz Jędrzejczyk**.
- Current source version: **1.142.2**.
- Platform: Windows x64, WPF, .NET 10 (`net10.0-windows10.0.19041.0`).
- SDK pinned by `global.json`: **10.0.301**, rolling to the latest compatible feature band.
- Persistence: per-profile SQLite, current schema **24**.
- Installer: self-contained single-file app plus a WiX 5 per-machine **MSI**. It is not MSIX.
- Product name changed after the original implementation. Namespaces, project folders, the single-instance mutex, registry compatibility paths, and the `PROJECT_TIME_TRACKER_*` smoke variables intentionally retain the old `ProjectTimeTracker` name.

## What the app does

Log O'clock is a local-first time tracker organized as:

`Client → Project → Saved task → Time entry`

It has one running timer, tray controls, manual history editing, project recognition from foreground-window titles, software-use association, idle/session review, tags, targets and target debt, paid state, rates and reports, profiles, CSV/safety archives, read-only Trello task sync, and optional Google Sheets export synchronization.

The detailed behaviour list is in the root [README.md](../README.md). Do not reconstruct requirements from old conversation history when the current source, tests, and README already answer the question.

## Read these files first

| Goal | Start here |
|---|---|
| Understand startup and ownership | `src/ProjectTimeTracker.Windows/App.xaml.cs` |
| Change timer, recognition, idle, or session behaviour | `src/ProjectTimeTracker.Windows/AppController.cs` |
| Change main-window workflows | `src/ProjectTimeTracker.Windows/MainWindow.xaml` and `.xaml.cs` |
| Change domain records or platform ports | `src/ProjectTimeTracker.Core/Models.cs` and `Interfaces.cs` |
| Change persistence, reports, deletion, or migrations | `src/ProjectTimeTracker.Infrastructure/SqliteTrackerStore.cs` |
| Change profiles and paths | `src/ProjectTimeTracker.Infrastructure/ProfileCatalog.cs` |
| Change local safety files | `MonthlyLogWriter.cs` and `DailySafetyArchive.cs` |
| Change Trello | `TrelloApiClient.cs`, `TrelloSyncService.cs`, and Trello store methods |
| Change Google Sheets | `GoogleSheetsApiClient.cs`, `GoogleSheetsSyncService.cs`, and Settings UI |
| Change styling | `Themes/CodexDark.*.xaml`, `App.xaml`, and `CodexDarkDesignRules.md` |
| Find a feature's complete code surface | [FEATURE_MAP.md](FEATURE_MAP.md) |

## Architectural reality

The assembly split is clean, but the Windows host uses code-behind and manual composition rather than MVVM plus a DI container.

Three files are high-risk concentration points:

- `MainWindow.xaml.cs` — roughly 9,000 lines; data loading, filters, CRUD commands, Reports, Settings, responsive layout, and embedded UI smoke assertions.
- `SqliteTrackerStore.cs` — roughly 5,500 lines; schema, migrations, queries, transactions, target debt, integration reconciliation, and derived-file refresh.
- `App.xaml.cs` — roughly 4,900 lines; composition root, tray and popup ownership, profiles, shutdown, and the WPF smoke harness.

`AppController.cs` is the runtime state machine for timer, recognition, software observation, idle reviews, session transitions, checkpoints, target-review notifications, and repeating net-work break reminders.

Make narrow edits in these files. Preserve named XAML controls and handler signatures unless the change explicitly includes their consumers and smoke checks.

## Non-negotiable invariants

1. **Only one running entry.** SQLite enforces this with `UX_TimeEntries_OneRunning`; timer switching/splitting must remain transactional.
2. **SQLite is the local source of truth.** CSV, daily logs, backups, and Google worksheets are derived safety/export surfaces.
3. **UTC in storage, local time in UI.** Persist ISO round-trip UTC timestamps; use `TimeZoneInfo.Local` and core period helpers for display and calendar boundaries.
4. **Net duration is end minus start minus exclusions.** Do not overwrite an entry's timestamps to represent removed idle time.
5. **Completed entries under 60 seconds are deleted.** Exactly 60 seconds remains valid.
6. **Unknown raw window titles are memory-only.** Recognition rules are stored; observed unknown titles and media/audio observations are not.
7. **Project/client removal is destructive.** Project entries and related metadata are removed. Task-only removal remains archival when history needs the task identity.
8. **Active objects feed filters and choosers.** Removed/archived objects must not return to current UI options.
9. **Google Sheets is not local restore or two-way device sync.** It writes/merges exported rows in worksheets but never imports remote rows into SQLite.
10. **Trello is one-way and read-only.** It may control linked task identity/name; it never writes cards.
11. **Every profile is isolated.** Database, settings, mappings, credentials, exports, and background sync services are profile-scoped.
12. **Use semantic theme resources.** Project and tag colors are data; they must not recolor shell selection or standard actions.

## Event and refresh contract

Most successful mutations follow this pattern:

1. UI or popup calls `AppController` or `ITrackerStore`.
2. The store commits the SQLite transaction and refreshes derived local files.
3. The controller raises `DataChanged` and/or `RunningEntryChanged`.
4. `MainWindow` refreshes visible collections and filters.
5. `App` refreshes the tray and queues Google Sheets sync after a five-second debounce.

If code writes directly through `ITrackerStore` from the UI, it usually must call `_controller.NotifyDataChanged()` afterward. Missing this step commonly causes stale main-window, tray, or cloud-export state.

## UI index contracts

Some code uses numeric tab indices. Reordering tabs requires updating these consumers and smoke tests.

- Main tabs: `0 History`, `1 Clients & projects`, `2 Reports`, `3 Settings`.
- Management tabs: `0 Clients`, `1 Projects`, `2 Targets`, `3 Tasks`, `4 Tags`, `5 Software`, `6 Window rules`.

History and Reports intentionally reset their normal filters whenever the user re-enters the tab. Report-to-History drill-down uses a one-shot preservation flag so its intentional range and filter survive that transition.

## Safe change checklist

Before editing:

- Locate the feature row in [FEATURE_MAP.md](FEATURE_MAP.md).
- Read the domain/interface contract and the concrete store/service path.
- Search for the relevant named XAML control, event handler, preview verifier, and tests.
- Decide whether the change affects schema, derived exports, profile isolation, tray state, or popup state.

While editing:

- Keep platform calls inside the Windows host or behind a Core interface.
- Use `apply_patch` for source edits.
- Preserve user data and unrelated workspace changes.
- For schema changes, bump `SchemaVersion`, create an idempotent migration, and rely on the existing pre-upgrade database backup.
- For store mutations, keep database changes transactional and invoke derived-file synchronization only after the transaction is closed.
- For UI changes, use shared resources and maintain keyboard, context-menu, double-click, click-away, and smooth-scroll contracts.

Before handing off:

- Build with zero warnings.
- Run all automated tests.
- Run the relevant WPF smoke flag against an isolated `PROJECT_TIME_TRACKER_DATA_DIR`.
- If releasing, update all version touchpoints, package, run the packaged smoke check, and refresh hashes and release notes.

## Known limitations and intentional trade-offs

- Windows/WPF is the only host. Core interfaces make a later host possible, but there is no macOS implementation.
- Profiles separate data but are not password-protected OS accounts.
- Google Sheets export preserves remote worksheet-only rows during merge but does not create local entries from them.
- Direct entry deletion is synchronized through Google tombstones, but project/client bulk deletion currently does not enqueue the deleted entry IDs; existing worksheet rows can therefore survive that bulk removal.
- Daily database snapshots exist, but there is no in-app restore UI.
- The UI smoke suite is embedded in `App.xaml.cs`/`MainWindow.xaml.cs`, not a separate UI-test project.
- The MSI is unsigned unless a certificate is supplied to the release script.
- Call/video idle protection is heuristic and output-only. It deliberately never accesses microphone capture.
