# Shared file-sync conflicts

Both clients now surface durable file conflicts from their main window. Clicking
the notice opens a comparison page with the phone and PC versions: exact byte
size, modification time and explicit deleted/not-present state. Lists are paged
in batches of 100. The PC page identifies the paired device by its display name.
Either end can choose the version to keep; keeping a deleted version propagates
the deletion. A confirmed choice remains visible as pending until the phone
reconnects/syncs, rechecks both files and completes the operation. Existing
transaction/recovery protection remains in place.

### Choosing while Android is synchronizing

An active sync no longer disables every conflict action. A phone choice is first
saved atomically in the private issue database as `queuedChoice` with the immutable
conflict ID. Only that row is disabled and explicitly shows that it is queued;
other conflicts remain selectable. The existing reconciler submits the queued
choice during a safe reconciliation step and rechecks both metadata versions.
The sync scheduler requests a follow-up round if the current round has already
passed the chosen file. No current transfer is cancelled or transport replaced by
the UI action. Queued choices survive process restarts.

A stale server response without a confirmed choice preserves concurrently queued
intent. A server-confirmed choice still wins, including an opposing PC choice;
changed file identities replace the issue and invalidate the old queued choice.
No new Windows MCP or wire operation is needed: this reuses `file_conflict_choose`
and the existing first-confirmed-choice rules. Queuing itself neither writes nor
deletes an audio file. Both the dedicated conflict page and settings summary use
the same row behavior.

#### Queue delivery 1.0.243 (2026-11-05)

Source `c14393a9fe671cdaa043a8e513eaacddbd940901` passed
[Mobile CI](https://github.com/tifish/JeekMusic/actions/runs/41867259916). Release
tag and target_commitish both match that SHA. The downloaded
[signed APK](https://github.com/tifish/JeekMusic/releases/download/v1.0.243/JeekMusic-1.0.243.apk)
has SHA-256 `da42c6ecd79458eb575a3fb4b5d08b74c826d1df076042274f7f94d5f67ffd6b`
and the unchanged release certificate. Local debug/release/lint and 122 JVM tests
passed. Two device tests passed for durable intent, concurrent report preservation,
server precedence, stale IDs and the actual service accepting two choices while
its sync job and mutex are busy. The busy-service fixture initially stopped at its
isolation check because old emulator mappings existed; it was corrected to save,
temporarily isolate and restore those configurations. No production gate was weakened.

The real Android reconciler plus Windows TLS/MCP test passed with the new queue
method: an opposing queued choice cannot override the PC's confirmed choice;
changed versions require a fresh ID, and a fresh phone choice uploads correctly.
The run took 163.688 seconds. Evidence: `output/conflict-queue-tls.log`.

**Actual click/visual acceptance remains blocked.** The emulator displayed an
input ANR during playback-service startup (main-thread trace in Media3
`MediaSession.getBitmapDimensionLimit`). After restarting that emulator, Android
System UI itself also displayed an ANR. These observations do not establish an
app-level root cause or a successful conflict-button interaction. Screenshots and
trace are in `output/conflict-queue-ui/`; they are not counted as passing UI tests.
Synthetic visual rows were removed by the explicit fixture cleanup (passed).
The downloaded release was installed without clearing data and package metadata
confirms 1.0.243/versionCode 243; launch was requested, but visual success cannot be
claimed in the affected environment. Physical-phone installation was not attempted.
Windows code/MCP were unchanged and the user's desktop was not restarted.

Each present version now labels its size as larger/smaller/same size and its
modification time as newer/older/same time, relative to the other version. These
use exact integer bytes and nanoseconds, not rounded text or inferred content
quality. If either file is absent, neither size nor time is ranked. The existing
choice and three-way reconciliation rules are unchanged. MCP exposes the same
comparisons in `sizeComparison` and `modifiedComparison` (see its contract).

Comparison validation: actual desktop UI and loopback TLS/MCP cover opposite
size/time directions; store tests cover equal and unavailable values. Android
debug build/lint and emulator screenshots cover differences within one second,
deleted versions and equality. Evidence: `output/conflict-comparison-ui/`.

### Comparison delivery 1.0.241 (2026-11-05)

Source `a81d139635c930d937cf603e43b1e9353a0e640a` is pushed. Local managed and
NativeAOT production TLS/MCP checks passed, including the comparison fields;
their 41-tool catalogs match. Focused store assertions cover equality and missing
versions. Android debug/lint and minified release builds passed; actual emulator
screenshots show subsecond differences, equality and missing files. Synthetic
rows were removed. No live conflict choice or music file was changed.

GitHub [Desktop](https://github.com/tifish/JeekMusic/actions/runs/41861853349) and
[Mobile](https://github.com/tifish/JeekMusic/actions/runs/41861853485) remained queued
at handoff. This delivery uses verified **local** artifacts, not claimed CI assets:
`desktop/artifacts/delivery-1.0.241/JeekMusic-1.0.241.apk` and `windows-final/`.
The APK matches the existing release certificate and was installed over the
emulator release without clearing data; versionCode/name confirmed 241/1.0.241.
Physical phone installation was not retried.

The first local Windows publish retained the project's default version 1.0.0;
it was rebuilt with explicit JeekVersion=1.0.241 and replaced before handoff.
Final running NativeAOT version is 1.0.241, source suffix a81d139, PID 4136.
Fresh checkpoints verified 1,585 queue entries/order, source and anchor, selection
index, pause/position/volume/output, filters, columns/layout, pairing credentials,
learning events and all 1,710 baseline rows unchanged through the restarts.
The same scoped firewall script configured the final executable's TCP rule after
an initial failed invocation; final MCP reports configured. No router/global
firewall settings were changed. Local hashes and preservation evidence are in
`local-provenance.json` and `restart-preservation.json` beside the artifacts.

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

## Release 1.0.239

Source `b92b2078309bc97b4903d2043857673c394f65f4` passed both
[Desktop CI](https://github.com/tifish/JeekMusic/actions/runs/41857334041) and
[Mobile CI](https://github.com/tifish/JeekMusic/actions/runs/41857334121).
Both release tags and target_commitish match that exact SHA. Downloaded assets:

- [Windows ZIP](https://github.com/tifish/JeekMusic/releases/download/desktop-v1.0.239/JeekMusic-desktop-1.0.239-win-x64.zip):
  SHA-256 `54f882efa5fa4726e80205a964f81bd3149108b72336dd326b07dfb1251a335d`.
- [Android APK](https://github.com/tifish/JeekMusic/releases/download/v1.0.239/JeekMusic-1.0.239.apk):
  SHA-256 `17294201fa179d0d177fc204cba7e6f9ef986978e711ef5137cd2f29e4f995cb`.
  Signing certificate remains `28a86a1affb4a31ed0a2a14e602b2ebed23d1c4385b366e9ccfac3eaf5a972fe`.
  API35 emulator retained-data installation and launch succeeded, versionCode 239;
  firstInstallTime remained 2026-10-31 10:44:42. No physical-phone install attempted.

Windows now runs the downloaded NativeAOT 1.0.239, PID 8908 at verification.
Fresh MCP checkpoint and SQLite backups preceded shutdown. Verified all 1,585 queue
entries/order/manual flags, index, position, pause, volume, endpoint, columns,
filter, settings/layout, pairing credentials and learning events. All 1,710 file
baseline rows and their contents match. The strict whole source-state comparison
initially failed: transient revision changed 3→0 and status committed→none. The
actual source context, pending context and anchor remained identical; no historical
queue was imported. Both explicit/default MCP data paths connect to the same process.
Production `lan_file_conflicts(action=list)` succeeds (zero shared records before
the old phone updates/reports them); no production conflict choice was made.

Initial production launch did not establish MCP and exited without a new application
exception in its log; a subsequent launch completed and passed the above checks.
This is recorded as an observed startup limitation, not a diagnosed cause. The
firewall helper initially returned 1; executing the repository's same scoped script
configured the exact new EXE/TCP39843 rule. MCP confirms one active exact rule;
no global firewall/UAC policy changed and external reachability was not tested.

Local provenance, private checkpoints and preservation results are under
`desktop/artifacts/delivery-1.0.239/` and are not committed. A supplementary emulator
small-screen attempt did not produce usable changed-size screenshots, so it is not
counted as passed; screen size/density/font were restored. One fixture cleanup
instrumentation invocation reported process-crashed; retry passed and removed both
synthetic records. Normal-size screenshots and the actual conflict interaction
checks above remain the accepted visual evidence.
