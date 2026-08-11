# Log O'clock 1.142.2

## Reports saved view

- Resizing a Reports task-summary column now updates the matching column in every project group and the aligned `Project total` row.
- A width change reveals `Save Current View`, just like a visibility change.
- `Save Current View` now persists Reports column widths and restores them on later launches.
- `Restore default` restores the original Reports widths as an unsaved layout, so the saved view is not overwritten until explicitly saved.
- Existing saved Reports views remain compatible; views without stored widths use the default widths while retaining their saved visibility.

## Verification

- Added a focused WPF smoke contract covering resize synchronization, save, reload, and restore-default behavior.
