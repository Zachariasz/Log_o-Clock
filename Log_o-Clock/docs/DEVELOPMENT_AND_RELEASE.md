# Development, testing, and release

## Toolchain

- Windows PowerShell.
- .NET SDK 10.0.301 or compatible feature-band roll-forward.
- WPF/WinForms Windows desktop workload.
- WiX Toolset SDK 5.0.2 for MSI packaging.

Repository-wide build settings enable nullable reference types, deterministic output, latest C# language features, and `TreatWarningsAsErrors=true`.

The release script prefers `work\dotnet10\dotnet.exe` when it exists; otherwise it uses `dotnet` from `PATH`. Use the same local SDK for reproducible checks in this workspace.

## Restore, build, and tests

```powershell
dotnet restore ProjectTimeTracker.slnx
dotnet build ProjectTimeTracker.slnx -c Release --no-restore -m:1 --disable-build-servers
dotnet test tests\ProjectTimeTracker.Tests\ProjectTimeTracker.Tests.csproj `
  -c Release --no-build --no-restore --disable-build-servers
```

With the bundled SDK:

```powershell
.\work\dotnet10\dotnet.exe build ProjectTimeTracker.slnx `
  -c Release --no-restore -m:1 --disable-build-servers
```

At this documentation snapshot, the complete suite resolves to **294 passing tests**.

## Automated test map

`SqliteTrackerStoreTests.cs` is the largest suite and owns transactional/persistence behaviour, migrations, timer switching/recovery, reports, targets, integrations, deletion, minimum duration, and export synchronization.

Focused Core tests cover:

- Recognition engine and prompt policy.
- Task title matching.
- Date ranges, time parsing, and English text culture.
- Tag parsing and invisible wrapping opportunities.
- Target periods, one-time lifecycle, debt calculation/text, and review schedule.
- Idle/audio qualification, short-idle rolling policy, and setting bounds.
- Overlap detection and recent-entry resume thresholds.

API-client tests validate Trello and Google request formation, response parsing, hidden worksheet creation, append and targeted-range writes, incremental reads, retry deduplication, and error/secret handling. Multi-store tests cover Google profile joins, offline work, ancestry/forks, conflict actions, durable deletion, identity coalescing, exact entry associations, and shared-versus-local privacy boundaries.

## WPF smoke harness

The executable contains a purpose-built smoke mode:

```text
LogOClock.exe --smoke-test
```

`App.xaml.cs` seeds optional isolated scenarios based on environment variables, opens the WPF shell, invokes assertions, optionally writes screenshots, and exits with code 0/1. Many view assertions live as `Verify...ForPreview` methods in `MainWindow.xaml.cs` or dialog code-behind.

Always isolate smoke data:

```powershell
$env:PROJECT_TIME_TRACKER_DATA_DIR = "$PWD\work\smoke-example"
$env:PROJECT_TIME_TRACKER_SMOKE_VIEW = "Reports"
$env:PROJECT_TIME_TRACKER_SMOKE_VERIFY_REPORT_VIEW = "true"
$env:PROJECT_TIME_TRACKER_SMOKE_WIDTH = "1440"
$env:PROJECT_TIME_TRACKER_SMOKE_HEIGHT = "900"

& .\work\dotnet10\dotnet.exe `
  .\src\ProjectTimeTracker.Windows\bin\Release\net10.0-windows10.0.19041.0\LogOClock.dll `
  --smoke-test
```

Never run smoke mode without an isolated data directory on a machine containing real Log O'clock data.

Useful `PROJECT_TIME_TRACKER_SMOKE_VIEW` values include `Clients`, `ClientsExpanded`, `Projects`, `Targets`, `Tasks`, `Tags`, `Software`, `Rules`, `Reports`, `Settings`, `SettingsIntegrations`, `GoogleSheetsConnection`, and `SyncConflicts`.

`PROJECT_TIME_TRACKER_SMOKE_VERIFY_AUTOMATIC_RECOGNITION=true` uses a fixed foreground monitor to verify silent startup, same-project software continuation, deferred atomic switching, filename task inference, delayed stopping, and the title-bar automatic-mode control without waiting for the real grace period.

Feature flags follow `PROJECT_TIME_TRACKER_SMOKE_VERIFY_<AREA>`. Current source contains checks for profiles, branding, English UI, tab-filter reset, timer/tray/task search, recognition start/switch/click-away, History filters/grouping/global column sorting/continue/overlap/view, Reports charts/selection/sorting/view, targets/debt/sidebar, software, idle/session/recovery, Trello UI, and entry-editor behaviours. `PROJECT_TIME_TRACKER_SMOKE_VERIFY_HISTORY_GLOBAL_SORT=true` exercises the Client and Project header handlers across multiple days and clients, then verifies that clearing sorting restores day grouping.

Discover the authoritative list with:

```powershell
rg -o 'PROJECT_TIME_TRACKER_[A-Z0-9_]+' src\ProjectTimeTracker.Windows -g '*.cs'
```

Screenshot variables:

- `PROJECT_TIME_TRACKER_SMOKE_SCREENSHOT`: absolute PNG output path.
- `PROJECT_TIME_TRACKER_SMOKE_SCREENSHOT_ALL=true`: capture every main tab with indexed suffixes.
- Width/height variables validate the 800, 1440, and 1920 px layouts.

Because this is WPF, smoke execution launches a GUI process and may require explicit tool approval in sandboxed development environments.

## Manual regression areas

Automated checks do not fully replace manual Windows validation for:

- Tray single-click versus double-click timing and tooltip truncation.
- Foreground WinEvent hook behaviour across real applications and monitors.
- Audio render, listen-only calls, browser video, and microphone privacy indicator.
- Lock/unlock, suspend/resume, real sign-out, and crash/power recovery.
- Touchpad horizontal scrolling and nested smooth scrolling.
- DWM rounding, custom maximize bounds, DPI scaling, and taskbar placement.
- Windows Credential Manager and real Trello/Google OAuth/rate limits.
- Google Sheets two-computer create/join, offline edits, reconnect, edit/delete conflicts, remote running presence/staleness, and final convergence. Follow [GOOGLE_SHEETS_SYNC.md](GOOGLE_SHEETS_SYNC.md).
- Autostart registry and installer upgrade/uninstall.

## Packaging

```powershell
.\scripts\build-release.ps1
```

The script:

1. Restores the Windows x64 runtime pack.
2. Publishes a compressed, self-contained, single-file x64 executable.
3. Builds a WiX per-machine MSI containing the executable and Start Menu shortcut.
4. Optionally signs/timestamps the MSI when certificate arguments are supplied.

Outputs:

```text
outputs\LogOClock-<version>-win-x64\LogOClock.exe
outputs\installer\LogOClock-Setup-<version>.msi
```

Without a certificate, the MSI is intentionally reported as unsigned.

## Version touchpoints

For a release, update together:

- `Directory.Build.props`: package, assembly, and file versions.
- `scripts/build-release.ps1`: default app version.
- `installer/ProjectTimeTracker.Installer.wixproj`: default product version.
- `outputs/INSTALL.txt`, release changelog, artifact names, and `SHA256SUMS.txt`.

Then verify the published executable's `FileVersion`/`ProductVersion`, run the packaged smoke scenario, and check MSI signature status.

Publish a non-draft, non-prerelease GitHub Release tagged `v<version>` with the matching MSI attached. Installed copies use that published release as their update-discovery source and open its GitHub release page for the user to download the installer.

## Source archive convention

Release source archives exclude generated or environment-owned directories:

```powershell
tar.exe -a -c -f outputs\LogOClock-source-<version>.zip `
  --exclude=outputs --exclude=work --exclude=bin --exclude=obj `
  --exclude=.git --exclude=.agents --exclude=.codex *
```

## Change patterns

### Domain-only change

- Add/update a Core policy or record.
- Add focused unit tests.
- Keep Windows and infrastructure references out of Core.

### Persistence change

- Update `ITrackerStore` if the operation is part of the application contract.
- Implement it transactionally in `SqliteTrackerStore`.
- If schema changes, bump `SchemaVersion` and add guarded migration logic.
- Test fresh schema and upgrade path.
- Verify derived local files and durable Google journal/deletion/conflict implications.

### WPF feature change

- Reuse semantic styles/resources.
- Preserve control names and context/double-click/keyboard workflows.
- Update the relevant `Verify...ForPreview` smoke assertion rather than weakening it.
- Test normal and narrow responsive layouts plus popup click-away behaviour.

### Integration change

- Keep secrets in `ICredentialStore`.
- Keep API access behind the Core client/service interfaces.
- Use cancellation and single-flight synchronization.
- Preserve offline local data on any network, authentication, rate-limit, or parsing failure.
- Never log or display raw credentials.
