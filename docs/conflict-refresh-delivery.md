# Conflict refresh, 1.0.245

The reported failure was stale presentation, not evidence that a user choice had
been lost: the production PC's last queued row later disappeared after its upload
acknowledgement. No production conflict was selected or deleted during diagnosis.

## Changes

- Android issue mutations publish the durable rows/count/revision immediately,
  including same-count choices and per-file removals. Progress no longer writes
  historical `LanFileReport.conflicts` back over the live list.
- Each completed operation drains its durable library refresh receipt. Song list
  refresh no longer waits for 200 files or the entire mapping. Conflict removal
  follows successful acknowledgement/convergence; clicking alone is not completion.
- PC conflict windows subscribe to service changes and coalesce changes received
  during a refresh. Empty last pages move back. Read-only MCP listing no longer
  emits unchanged notifications and cannot cause a refresh loop.
- Foreground Android exchanges up to 100 current conflict IDs every 2 seconds and
  attempts an immediate independent control connection after a choice. It never
  replaces the transport used by an active file transfer. Busy/offline intent is
  durable; old peers retain the existing reconciliation path.
- `conflictStatusProtocol=1` advertises a scoped, bounded, authenticated read-only
  `file_conflict_status` operation. It does not resurrect absent rows. Absence is
  only a reconciliation hint, not permission to discard local conflicts. Stale
  replies cannot overwrite a confirmed first-writer choice or resurrect an ID.
- MCP descriptions/docs and diagnostics updated; 41 tools and input schemas remain
  compatible, and managed/NativeAOT catalogs are identical.

## Validation before release

- Windows LAN tests: 59 passed. Android JVM suite and lint passed.
- Android device queue/publication tests: 3 passed on API35 x86_64.
- Real isolated desktop TLS plus Android production service/reconciler test passed:
  PC choice beats queued opposing phone choice; independent status exchange works
  while the sync gate and job are occupied; phone choice reaches PC before file
  reconciliation; removal publishes before the library refresh callback.
- Managed and NativeAOT actual EXE/MCP and real TLS file contracts passed.
- Windows actual UI: open comparison window and main notice refresh after a peer
  clear without clicking Refresh. Screenshot `output/conflict-refresh-ui/desktop-cleared.png`.
- Android actual UI: banner navigation and choice-to-queued text checked on-screen;
  queued screenshot `output/conflict-refresh-ui/android-queued.png`.
  An attempted in-process accessibility script timed out locating the main banner
  before entering the conflict page; it is not accepted as UI evidence and was
  removed. Same-screen confirmed/removal rendering remains visually unverified;
  production StateFlow publication/removal is covered by device integration tests.

Cross-device status is near-real-time when foreground/online, not an unconditional
zero-latency guarantee: busy server, network, pagination and Android background
restrictions can delay it. Actual file commits remain serialized and validated.
Physical phone is disconnected; no physical-phone installation or conflict choice
was attempted. No changes to conflict policy, baselines, or user music were made.

Final CI, artifacts, restart preservation and further UI evidence are recorded
after the source commit below.
