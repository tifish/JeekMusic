# Shared file-sync conflicts

Both clients now surface durable file conflicts from their main window. Clicking
the notice opens a comparison page with the phone and PC versions: exact byte
size, modification time and explicit deleted/not-present state. Lists are paged
in batches of 100. The PC page identifies the paired device by its display name.
Either end can choose the version to keep; keeping a deleted version propagates
the deletion. A confirmed choice remains visible as pending until the phone
reconnects/syncs, rechecks both files and completes the operation. Existing
transaction/recovery protection remains in place.

## What is a conflict?

The comparison policy is still exact size + actual mtime, with a separate
last-confirmed baseline for each side. Same size and mtime are deliberately
treated as unchanged (even if bytes happen to differ). It is **not** a rule that
conflicts only happen when timestamps match and sizes differ.

- One side changes, the other stays at its confirmed baseline: automatically
  propagate the change/deletion.
- Both sides change to different metadata states, or one changes and the other
  deletes: conflict. A later timestamp does not imply permission to discard the
  other independent edit.
- Initial sync without a baseline: one missing side is supplemented; two
  different same-path metadata states require a choice.
- Both sides converge to the same state: acknowledge automatically.
- Unknown/unavailable paths are deferred, never interpreted as deletions.

## Shared state and safety

The PC persists bounded, scope-bound conflict records in its existing private
file-sync SQLite database. Each immutable conflict ID binds mapping, path,
baseline revision and both observed metadata versions. Android reports conflicts
through the authenticated file protocol; a PC choice changes the revision hint
used by the existing phone auto-sync poll. Old clients without conflictProtocol=1
retain their previous phone-only handling; both clients need this update for the
shared experience.

The first confirmed choice wins. Repeating it is harmless; a competing opposite
choice is rejected. On the next scan, changed versions produce a new ID and
invalidate the old choice. PC metadata is checked before accepting a choice;
both sides are checked again before the existing conditional operation. Choosing
does not transfer/delete a file by itself. Confirmation/convergence removes the
shared record. Device revocation removes its PC conflict records. No new audio
hashing is introduced for reporting, listing or choosing.

Desktop MCP `lan_file_conflicts` lists, opens and chooses through the same service
as UI; see [MCP contract](../desktop/docs/mcp.md). Both builds expose 41 tools.

## Validation

- Desktop regression: 734 passed, two existing hardware-dependent skips; final
  targeted checks: 100 passed. Android: 122 JVM tests, debug build and lint passed.
- Real production TLS/MCP checks passed on managed and NativeAOT builds: shared
  report, comparison, choice, competing-choice rejection and convergence. Both
  runtime tool catalogs are identical (41 tools). Python workflow tests: 7 passed.
- Android real reconciler: first five integration cases passed; the sixth hit the
  fixture's mapping quota after adding a case. Reusing an existing fixture mapping
  fixed the harness, and that case passed separately. Production quotas were not
  weakened. Changed versions require a fresh decision; a server-confirmed choice
  wins over an older queued phone choice.
- Actual Windows main notice/comparison/choice UI was exercised in both builds.
  Android API35 emulator main notice and comparison page were exercised using two
  explicitly synthetic local records, including deletion and pending state.
  Screenshots: `output/conflict-ui-final/`, `output/conflict-ui-aot/`. Android UI
  hierarchy dumping returned a null root; screenshots were visually inspected.
  Synthetic visual records were removed afterwards. No real library conflict was
  chosen or overwritten for validation.
- Public SAF transaction primitives are unchanged; this task did not repeat the
  full public-storage matrix. The user's phone previously rejected ADB installation;
  no retry or permission bypass was attempted. Both installed clients must update.
- Local AOT initially required adding the installed VS Installer directory to PATH
  for vswhere; the corrected build passed. Existing DataGrid trim/AOT warnings and
  the SDK ILCompiler package advisory remain visible in the build log.

Final release and preserved-state restart details will be recorded after CI.
