# Log O'clock developer documentation

This folder is the starting point for a new development context. The documents describe the application as implemented in source at version **1.142.4** and SQLite schema **27**.

The root [AGENTS.md](../AGENTS.md) is the concise automatic coding-context entry point and links back to this set.

Integration-specific reference: [GOOGLE_SHEETS_SYNC.md](GOOGLE_SHEETS_SYNC.md) documents pairing, shared/device-local boundaries, hidden worksheets, revision ancestry, conflicts, legacy upgrade, and release verification for schema-28/protocol-2 profile synchronization.

Read in this order:

1. [NEW_CONTEXT.md](NEW_CONTEXT.md) — fast handoff, invariants, hotspots, and change checklist.
2. [ARCHITECTURE.md](ARCHITECTURE.md) — assembly boundaries, composition, runtime flows, and architecture diagrams.
3. [FEATURE_MAP.md](FEATURE_MAP.md) — current feature areas mapped to their implementation and tests.
4. [DATA_AND_STORAGE.md](DATA_AND_STORAGE.md) — profiles, SQLite schema, exports, backups, credentials, and integration direction.
5. [DEVELOPMENT_AND_RELEASE.md](DEVELOPMENT_AND_RELEASE.md) — build, tests, WPF smoke checks, packaging, and release checklist.
6. [CodexDarkDesignRules.md](CodexDarkDesignRules.md) — binding visual and interaction rules.

The root [README.md](../README.md) remains the detailed product-behaviour inventory. These developer documents explain how that behaviour is organized in code.

## Source-of-truth hierarchy

When documents disagree, use this order:

1. Current source and tests.
2. This developer documentation.
3. Root product README and Codex Dark design rules.
4. Historical release notes in `outputs`.

`outputs` contains generated installers, portable builds, previews, checksums, and historical changelogs. It is not an architecture source of truth.
