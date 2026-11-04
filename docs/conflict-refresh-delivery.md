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

- Windows LAN tests: 59 passed. Android JVM 122 tests and lint passed.
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

## Final delivery

Source `e8e05212300a92b3a8e60edf2d1767971da2d1fa` passed both
[Desktop CI](https://github.com/tifish/JeekMusic/actions/runs/41874967260) and
[Mobile CI](https://github.com/tifish/JeekMusic/actions/runs/41874967345).
Release target_commitish for both `desktop-v1.0.245` and `v1.0.245` matches it.

- [Android APK](https://github.com/tifish/JeekMusic/releases/download/v1.0.245/JeekMusic-1.0.245.apk)
  downloaded SHA-256: `f6eb2af589ffdd172d82af58da0f6946e7d59a8eb547866b724030cfb66284c0`,
  equal to the release asset digest. Signing certificate remains
  `28a86a1affb4a31ed0a2a14e602b2ebed23d1c4385b366e9ccfac3eaf5a972fe`.
  API35 retained-data installation and visible release launch passed, versionCode
  245; firstInstallTime stayed 2026-10-31 10:44:42. Actual release screenshot and
  UI hierarchy are in `output/conflict-refresh-delivery-245/`.
- [Windows ZIP](https://github.com/tifish/JeekMusic/releases/download/desktop-v1.0.245/JeekMusic-desktop-1.0.245-win-x64.zip)
  published asset digest (not a local download verification):
  `3e4d08f4bb6ad39073410e9fef1115bf5fe6c336adcefec879e0e2eccdbe0040`.
- Production Windows uses the locally verified NativeAOT built from the same final
  application sources with explicit version 1.0.245, not the downloaded CI ZIP.
  PID 4136 → 38596; 1,585 queue entries/order, source, index, position, paused state,
  volume/endpoint, filters/columns, settings/layout, pairing credentials and learning
  events match the fresh pre-shutdown checkpoint. All 1,714 baseline rows and their
  contents match. Single-instance default/explicit MCP confirmed; pending conflicts
  zero, `conflictStatusProtocol=1`. Daily framework-dependent output was rebuilt too.
- Exact new EXE/TCP39843 firewall rule verified configured. Its first helper call
  failed before shutdown; the same scoped script retry succeeded. No global policy,
  router or user media was changed. Local preservation evidence is under
  `desktop/artifacts/conflict-refresh/` (private checkpoints not committed).

Only this evidence document changed after the source release; no extra client
restart or new software release is required for that documentation commit.
