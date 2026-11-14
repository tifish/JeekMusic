# Android selected conflicts remaining in the notice

The reported sequence was three conflicts selected on the phone, followed by two
remaining entries after leaving/reopening the page, with disabled choice buttons.
The old notice counted every durable issue as a conflict requiring attention,
including saved choices waiting for file reconciliation. A completed scan could
also enter idle while selected work remained.

The Android notice now distinguishes unselected conflicts from saved choices
waiting for synchronization. The comparison page retains the saved decision,
hides redundant version buttons, and offers Continue sync. Queued, confirmed and
legacy column-only phone choices are counted, including after a process restart.
Enabled mappings with pending choices retain a dirty retry and use the existing
exponential backoff. Only verified reconciliation removes a durable issue.

Windows MCP and wire contracts are unchanged. Existing `lan_file_conflicts`
list/choose and `lan_sync_status` already expose the relevant pending decisions
and phone schedule; the real isolated MCP/TLS regression was used to verify them.

## Verification, 2026-11-14

- Android debug app and instrumentation APK build; 128 JVM tests passed.
- Android lint: zero errors, 99 warnings.
- Three durable choice/publication/busy-service device tests and two new pending
  retry/UI tests on emulator-5584, API 35 x86_64. The pending retry test uses an
  isolated offline mapping and verifies retained intent, dirty state and backoff.
- Real MainActivity interaction: click the main notice, select the PC version
  for three synthetic rows, show the mixed count, remove one row, confirm another
  choice, return to the main screen and reopen the two waiting rows. Verify the
  enabled Continue sync action and that removing the remaining rows updates both
  the already-open page and the main notice. Screenshots are under
  `output/conflict-pending-ui/`, including `main-two-waiting.png`,
  `reopened-two-waiting.png` and `completed-open-page.png`.
- UI completion/removal is simulated through the production issue database;
  real file application is tested separately by
  `desktop/tools/test-lan-files.py --exe bin/JeekMusic.exe --android emulator-5584
  --case sharedChoiceWinsAndChangedVersionsNeedAnotherDecision`. This passed the
  production Android reconciler against an isolated Windows TLS/MCP peer,
  including first-confirmed-choice precedence, changed identity invalidation,
  upload convergence, removal publication and backup restore/cleanup. No audio
  hashing occurred. No daily pairing, baseline or user music was changed.

The first UI run failed because an offscreen accessibility button was not
clickable. The test now scrolls and reacquires its node; the corrected run passed.
The physical phone was not connected, so installation and acceptance on that
phone remain unverified. The available emulator receives a retained-data install
and a force-stop/relaunch of the new debug client.
