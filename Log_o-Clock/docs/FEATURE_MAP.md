# Feature-to-code map

This map points a new context to the smallest relevant code surface. The root README contains the fine-grained product behaviour.

| Feature area | Primary implementation | Supporting code and tests | Important contracts |
|---|---|---|---|
| Application startup and shutdown | `Windows/App.xaml.cs` | `SingleInstanceCoordinator`, `AutostartService`, `ProfileCatalog` | Explicit shutdown mode; second instance activates first; clean exit confirms/stops a timer. |
| Profiles | `ProfileCatalog.cs`, profile handlers in `App.xaml.cs` and `MainWindow.xaml.cs` | `ProfileCatalogTests.cs`, `WindowsCredentialStore.cs` | Default profile uses root directory; secondary removal archives its directory and removes credentials. |
| Timer bar | `MainWindow.xaml/.cs` timer handlers | `AppController` start/update/stop methods, `RunningStartTimeText`, task search helpers | Task → Project → Description → elapsed → action; Enter commits; project/task changes update a running entry. |
| Tray | `TrayIconService.cs`, tray handlers in `App.xaml.cs` | `EntryDetailsWindow`, WPF tray smoke flags | Idle/running icons are generated separately from the app icon; single/double click uses Windows double-click timing; the running-only `Cancel current timer` action discards the local entry immediately, unlike `Stop timer`. |
| Running-entry details / Rip | `EntryDetailsWindow.xaml/.cs` | `AppController.SaveRunningDetailsAsync`, `RipRunningEntryAsync`, `UpdateRunningStartAsync` | Autosave, click-away persistence, editable start, Stop, and atomic Rip boundary. |
| Manual History entries | `MainWindow` History handlers, `EntryEditorWindow.xaml/.cs` | `TimeOfDayText`, `DateRangePicker`, `SqliteTrackerStoreTests` | Typed task may be created; compact time formats are normalized; end date can roll to next day. |
| History presentation | `MainWindow` History refresh/filter/view state | `DisplayModels`, `HistoryGroupDurationConverter`, `TimeEntryOverlapDetector`, wrapping converter | Group by local day and show daily totals by default. Header sorting flattens and orders the full visible filtered set by its selected column, with newest-first ties; Clear sorting or leaving History restores grouping. Passive selection, description/tag filters, overlap icon, and saved columns/order/width/wrap remain supported. |
| Reports | `MainWindow` report filters, projections, charts, saved view | `SqliteTrackerStore.GetReportAsync`, `ReportRow`, `DisplayModels`, `CsvExporter` | Current-month default, project groups, latest-task sorting, one selected object, saved visibility/width, active, call-time, and time-plus-idle metrics. |
| Date ranges and calendar | `Controls/DateRangePicker.xaml/.cs` | `DateRangeText`, `CalendarDateRangePresets`, tests | First click applies one day; second/Shift click extends; `DD.MM.YYYY - DD.MM.YYYY`; today keeps blue outline. |
| Client/project management | `MainWindow` clients/projects handlers and context menus | `NewProjectWindow`, `ProjectSettingsWindow`, `ProjectColorWindow`, bulk editor | Double-click edit; blank-space RMB add; projects can be reversibly frozen from the Projects context menu. Frozen project-owned records move to dimmed, collapsed `Freezed Projects` sections; project/client removal is destructive. |
| Task management | `MainWindow` Tasks handlers, `SqliteTrackerStore` task-origin cleanup | `NewTaskWindow`, `BulkEditWindow`, Trello link records, `SqliteTrackerStoreTests` | Project filter resets on entry; linked Trello tasks cannot be locally renamed/moved; local Tasks-tab creations persist without logs, while unused notification-created tasks are removed. |
| Tags and rich descriptions | `TagDescriptionEditor`, `TagDescriptionDisplay`, `TagVisuals` | `TagParser`, `TagSettingsWindow`, tag store methods/tests | Managed tags hide `#` visually; tag rename is global; editing a description affects only that entry; tags can be project-scoped or global. |
| Software association | `MainWindow` Software handlers, `SoftwareSettingsWindow` | `AppController` software observation, project/software tables | Software is manual only; process key is immutable; label is global; exclusion and correlated tags are project-scoped or global. |
| Window rules | `MainWindow` Rules handlers, `RuleDialog` | `ForegroundActivityMonitor`, `RecognitionEngine` | Group/filter by project; double-click/RMB edit; delayed process/title capture. |
| Recognition reminders | `AppController` recognition methods | `NotificationService`, `ReminderWindow`, `ProjectChooserWindow`, `EntryDetailsWindow`, `TaskTitleMatcher`, recognition tests | 500 ms stability, longest rule, ambiguity chooser, matched-monitor placement, task suggestion, five-minute snooze; typed task names are notification-originated and are pruned if no entry retains them. |
| Idle detection | `UserIdleMonitor`, idle handlers in `AppController` | `ShortIdleReviewPolicy`, accumulated-away tests | Observation begins at 30 s; under-five-minute intervals can accumulate over rolling four hours; dialogs serialize. |
| Excluded-software time | `AppController` excluded-software visit/review methods | `ProjectSoftwareDefinition`, setting tests | Per process and running entry; configurable threshold; intervals remain separate so intervening work stays counted. |
| Call/video idle protection | `IdleProtectionMonitor` | `ForegroundAudioQualificationPolicy`, settings/tests | Output render only; communication, sustained foreground audio, and video reasons; no microphone access or metadata persistence. |
| Lock/sleep/sign-out | `SystemSessionMonitor`, session handlers in `AppController` | `SessionTrackingSettings`, recovery logic in store/App smoke | Either stop/review or keep running and offer exclusion; sign-out recovery state persists. |
| Time exclusions | Store `TimeExclusions` methods and AppController reviews | `EntryEditorWindow`, report query | Net time subtracts exclusions; History displays the aggregate; entry editing can replace the aggregate exclusion. |
| Recent-entry continuation | `SqliteTrackerStore.StartOrResumeTimerAsync` | `AppController.StartTimerAsync`, recent-gap settings tests | Same project/task/description/tags, unpaid, and gap strictly below setting; otherwise new entry. |
| Minimum entry duration | Store cleanup paths | `SqliteTrackerStoreTests`, WPF minimum-duration smoke | Completed net duration under 60 s is physically removed everywhere. |
| Paid state | History and Reports handlers in `MainWindow` | `TimeEntry.IsPaid`, report filters/store tests | Multi-entry History updates; Reports filter and project/task `Set as paid`; columns are not displayed in Reports. |
| Rates/value | Project settings and Reports projection | Store report SQL, CSV writers | PLN/USD/EUR per project; value derives from net duration and rate. |
| Targets | `MainWindow` target surfaces, target dialogs | `CustomTarget`, `TrackingPeriodCalculator`, `OneTimeTargetLifecycle` | Individual daily/weekly/monthly/one-time records, optional project, active or time-plus-idle metric, project ghost rows. |
| Target debt | Store debt calculation and cancellation ledger | `ProjectTargetDebtCalculator`, `TargetDebtText`, tests | Carry monthly deficit; repay only from surplus using daily → weekly → monthly basis; lower/cancel/restore are dated ledger events. |
| Scheduled target review | `AppController` target-review timer | `TargetReviewWindow`, `TargetReviewSettings`, tests | Once on configured weekday occurrence; weekly/monthly progress and debt. |
| Break reminders | `AppController` timer streak tracking | `NotificationService`, `BreakReminderWindow`, `BreakReminderSettings`, Settings UI | Repeats after configured net tracked-time intervals; survives project switches/rips, resets when the timer stops or an idle review occurs, and closes automatically after three seconds. Built-in messages can be enabled per profile; selection is randomized among the least-used eligible messages each local day, with fixed hour windows where defined. |
| Trello | `TrelloApiClient`, `TrelloSyncService` | Trello UI windows, store reconciliation, API/store tests | Per profile; one board mapping; selected lists; assigned-to-me open cards; 15-minute read-only sync. |
| Google Sheets | `GoogleSheetsApiClient`, `GoogleSheetsSyncService`, `SqliteTrackerStore.Sync.cs`, [protocol guide](GOOGLE_SHEETS_SYNC.md) | `GoogleAuthorizationBroker`, pairing/conflict Settings UI, tray/device status, API and multi-store tests | Per profile; hidden append-only journal; create/join; schema 27; one-minute heartbeat; background two-way reconciliation; durable conflicts/deletes; daily tabs are app-managed views. |
| CSV and safety archive | `MonthlyLogWriter`, `DailySafetyArchive`, `CsvExporter` | store synchronization and CSV tests | UTF-8 derived output; daily revisions; first/latest daily SQLite snapshots; manual Report export stays native Save dialog. |
| Responsive shell and styling | `MainWindow` responsive methods, `App.xaml`, `Themes/*` | `WindowBackdropService`, `SmoothScrollBehavior`, design rules | Minimum width 800; floating nav below 1040; neutral interactions; custom dark title bars and DWM working-area maximize. |
| English UI | `EnglishUiCulture`, `AppTextCulture` | culture tests and WPF smoke | App-owned text/dates are English UK; user-authored data is untouched. |
| Build and packaging | `Directory.Build.props`, `build-release.ps1`, `installer/*` | `global.json` | Warnings are errors; self-contained single-file win-x64; WiX MSI; optional certificate signing. |

## Main UI surfaces

### History

- Date range and shortcuts.
- Client/project/task/tag/description filters.
- Day-grouped editable entries with paid actions, idle subtraction, overlap marks, saved view, and CSV-independent history source.

### Clients & projects

- Clients: collapsed client rows with nested projects.
- Projects: project-first totals, dates, colors, settings, and targets.
- Targets: individual target records and project filter.
- Tasks: task-first totals and project filter.
- Tags: colored project/global definitions and usage.
- Software: manual process definitions, labels, scope, exclusions, and correlated tags.
- Window rules: project-grouped recognition rules and project filter.

### Reports

- Date/filter toolbars.
- Project groups with nested task summaries and aligned totals.
- Project/client share switchable donut and second time-plus-idle donut.
- Current monthly targets and debt.
- Paid actions, drill-down to History, and CSV export.

### Settings

- The page is divided into independently scrollable `Tracking`, `Idle & sessions`, `Targets`, `Integrations`, and `Application` subtabs while preserving the shared Codex-dark segmented navigation and existing control contracts.
- Recognition, recent-entry continuation, and break reminders (net active-time interval, bottom-right/centre placement, and per-message enablement).
- Windows session behaviour, call/video idle protection and live state.
- Excluded-software, accumulated short-idle, recent-entry resume, and short-idle reporting thresholds.
- Scheduled target review and launch at sign-in.
- Trello connection/mappings and Google Sheets create/join, device, pinned-time-zone, status, and conflict review controls.
- Storage/profile information.

## Test ownership

- Pure Core policies have focused test files named after the policy.
- `SqliteTrackerStoreTests.cs` is the main persistence/integration contract suite and covers most destructive or transactional behaviour.
- API clients have request/response/redaction tests.
- WPF interaction contracts live behind `PROJECT_TIME_TRACKER_SMOKE_VERIFY_*` flags in `App.xaml.cs`, with view-specific assertions in `MainWindow.xaml.cs` and dialog classes.
