# Google Sheets profile synchronization

Google Sheets synchronization is an optional, profile-scoped way to share one Log O'clock profile between Windows computers. Each computer continues to use its own SQLite database for normal operation. The spreadsheet is a shared revision transport and a readable reporting surface; it is not queried for every app action.

## User workflow

Settings offers two connection modes:

- **Create new** creates a spreadsheet for the current profile, assigns the shared profile identity, pins the worksheet time zone, and seeds the synchronization journal from SQLite.
- **Use existing spreadsheet** accepts a Google Sheets URL or spreadsheet ID. It validates the Log O'clock metadata and can join only when the local profile contains no user-created data. The joining profile adopts the shared name, time zone, and synchronized records.

The app opens immediately with local data and reconciles in the background. A local mutation queues synchronization after a short debounce. The one-minute device loop also maintains presence and retries after authentication, connectivity, quota, or transient API failures. Tracking and local editing remain available while Google is unreachable.

Every computer sharing a spreadsheet must run a schema-27, synchronization-protocol-1 compatible release. An older export-only client must not write to an upgraded spreadsheet.

## Shared and device-local data

The shared profile includes:

- Completed time entries, including raw UTC start/end values, project/task identity, description, pending/source state, paid/call state, stable creation time, modification time, exact exclusions, and associated configured software.
- Clients, projects, saved tasks, tags, software definitions and assignments, and recognition rules.
- Targets, target-debt adjustments, Trello board/list mappings, and external task links.
- Stable profile preferences, History/Reports layouts, and the shared profile name.

The following remain device-local:

- OAuth secrets, refresh tokens, and Trello credentials.
- Connection/error status, update-check results, autostart, pending reviews, resume markers, and other runtime state.
- The local running entry and its 30-second checkpoints.
- Unknown observed window titles and media/audio observations.

Only configured recognition rules and software definitions are shared. The privacy boundary that prevents unknown observations from being persisted also prevents them from entering Google Sheets.

## Spreadsheet layout

Three hidden worksheets contain app-managed protocol data:

| Worksheet | Purpose | Important fields |
| --- | --- | --- |
| `__LogOClockProfile` | Shared identity and protocol metadata | `ProtocolVersion`, `ProfileId`, `ProfileName`, `PinnedTimeZoneId`, `UpdatedUtc` |
| `__LogOClockChanges` | Append-only entity revisions | revision ID, entity type/ID, parent revision IDs, operation, device ID/name, UTC change time, content hash, canonical JSON payload |
| `__LogOClockDevices` | Device presence and remote running status | device ID/name, heartbeat, entry/client/project/task, UTC start, running flag |

Visible `yyyy-MM-dd` worksheets are derived daily views. Their current columns are:

`EntryId`, `Date`, `Start`, `End`, `Duration`, `Client`, `Project`, `Task`, `Description`, `Tags`, `Software`, `Paid`, `PendingDetails`, `HourlyRate`, `Currency`, `Amount`, `Source`, `Call`, `Created at`, and `Last modified`.

Daily grouping uses the profile's explicitly pinned worksheet time zone. Changing it in Settings requires confirmation and rebuilds all daily views without changing the stored UTC timestamps. Materialization is global by `EntryId`, so moving an entry to another start date removes its stale copy. Manual edits to visible worksheets are unsupported and are overwritten from reconciled SQLite state.

## Revision and reconciliation rules

Schema 27 adds local `ProfileSync*` tables for runtime suppression, metadata/cursor state, dirty-table tracking, revision heads, a durable outbox, identity aliases, and unresolved conflicts.

1. A meaningful local mutation and its dirty marker commit in the same SQLite transaction.
2. Capture converts changed entity state into a canonical JSON revision. A revision contains its immutable ID and parent revision IDs.
3. Upload appends revisions to `__LogOClockChanges`. Retrying the same revision is safe because revision IDs are deduplicated.
4. Download reads only rows after the stored cursor. The cursor advances only after the SQLite reconciliation transaction commits.
5. Applying a remote revision enables an internal suppression flag so the imported mutation is not re-enqueued as a new local edit.

A single descendant of the known head applies automatically. Independent heads descended from the same earlier revision form a conflict, regardless of device clock values. `CreatedUtc`, `ModifiedUtc`, and the revision change time are provenance; they do not replace ancestry-based conflict detection.

Independently created identities are coalesced with normalized keys: client name, project path, task path, tag name, and software process name. Local aliases rewrite dependent foreign keys so logs are retained rather than duplicated or dropped.

Deletion revisions remain in the cloud journal indefinitely. They are not time-limited tombstones. Confirming a client/project deletion publishes deletion revisions for the affected dependent records, preventing a computer that was offline for a long time from resurrecting them.

## Entry timestamps and running timers

`CreatedUtc` is assigned when an entry is created and is preserved through ordinary edits. `ModifiedUtc` advances for every meaningful change, including exact exclusion and software-association changes. A checkpoint-only write updates `LastCheckpointUtc` but does not change `ModifiedUtc` and does not publish a profile revision.

Running entries are never imported into another device's SQLite database. A connected computer publishes its running state through `__LogOClockDevices`; other computers show it as read-only composer and tray presence. Presence is stale after two missed one-minute heartbeat windows. When the local timer stops, the completed entry and its exact associations enter normal revision synchronization.

## Conflict review

Conflicts persist in SQLite and appear as a non-modal count/status in Settings. Synchronization continues for unrelated records.

- An entry edit conflict offers **Keep local**, **Keep selected cloud**, or **Keep both**. Keep both assigns the duplicate a new entry ID and uses the resolution time for its creation and modification timestamps while preserving the selected version's exact exclusions and software links.
- Delete-versus-edit offers **Delete** or **Restore edited**.
- A concurrent client/project deletion with offline dependent work is grouped. Deletion lists the affected records and requires destructive confirmation; restore keeps the parent and work.
- Legacy visible-only worksheet rows offer **Import** or **Ignore** because they do not have trustworthy revision ancestry.

A resolution appends a new revision that references all conflicting heads so participating devices can converge on the decision.

## Legacy spreadsheet upgrade

The owning SQLite profile upgrades an export-only spreadsheet by creating the hidden protocol worksheets and seeding the journal. Existing visible daily rows are copied to `Legacy Review`. Rows already represented in SQLite remain derived output; remote-only legacy rows become one-time review candidates. Malformed rows remain reviewable with their validation error instead of being silently discarded.

Loading an older saved Google connection also upgrades its local metadata automatically. Missing shared-profile ID, device ID/name, and pinned time zone are generated and persisted before synchronization begins; the protocol remains at version 0 until the first upgraded sync completes successfully.

## Verification expectations

Automated coverage should include schema migration, blank-device join, offline additions, reconnect, descendant updates, true forks, conflict actions, durable and grouped deletion, identity coalescing, moved-date cleanup, exact entry associations, shared/local privacy boundaries, Google API request formation, retry deduplication, and credential redaction.

Relevant WPF smoke views are `SettingsIntegrations`, `GoogleSheetsConnection`, and `SyncConflicts`. Run Settings at both 1440 px and the supported 800 px narrow width with an isolated `PROJECT_TIME_TRACKER_DATA_DIR`.

Before a release, also perform a real two-computer Google OAuth regression: create and seed on the first computer, join an empty profile on the second, edit while one device is offline, reconnect, exercise an edit and delete conflict, confirm remote running presence/staleness, and verify convergence on both computers. Automated fakes do not replace this credential/network/device check.
