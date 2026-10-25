# Windows tag commit review corrections (policy 3, 2026-10-25)

This supersedes policy 2's unjournaled handoff. Scope is the four reproduced commit
issues in review of a12b73e; no library repair, Android integration or publication.
All media and filesystem experiments use synthetic fixtures and isolated data.

## Findings and verification

1. **Missing pathname after writer termination.** A flushed, write-through JSON
   journal records the source/candidate/reserved-original paths, both identities,
   expected content hashes and metadata digest before either source-name change.
   Production `Program.Main` recovers before library startup. Every `Scanner.Scan`
   also recovers before enumeration/deletion, serialized with live commits so it
   cannot observe their name gap. `TagWriter.Write` recovers before opening a missing
   source. Unknown/conflicting/changed objects are never overwritten; affected scans
   defer, keep DB records and expose pending diagnostics. Tests kill a real separate
   writer process after journal flush, source reservation, candidate rename and old
   object disposition, then invoke the production scanner in another fresh process.
   All preserve one DB record, ADS, security descriptor and original timestamps;
   pre-publication states restore old bytes, post-publication states retain validated
   output. Repeated recovery is idempotent. External source creation and reserved-
   original corruption retain evidence and defer scanning. Direct write retry also
   restores the name and succeeds.
2. **ADS loss.** Candidate preparation uses `CopyFile` plus native `ReplaceFile`
   on a private path while the public original still exists. No ignore-merge flags
   are used. Raw named-stream reads compare the complete source/candidate stream map
   under commit guards. Cover-only/lyrics-only/combined tests preserve two binary/
   Unicode named streams. The copied independent review harness now reports
   `ADS saved=True exact=True`; its previous `File.Replace` control also passes.
3. **DACL weakening.** Full readable owner/group/DACL/audit-SACL descriptors are
   cloned and independently compared, including protection/inheritance state.
   Windows' high-level setter can add AUTO_INHERITED; a narrowly applied
   `SetKernelObjectSecurity` DACL restore covers originals lacking that bit without
   normalizing their rules. Tests cover protected current-SID-only ACLs, ordinary
   inherited ACLs, the non-auto-inherited case and a nonempty audit SACL (exact
   preservation or safe refusal). The independent private-ACL reproduction now
   reports `saved=True preserved=True`, protected True→True, inherited rules **0**.
4. **Candidate changed after validation.** Expected hash is derived from validated
   serialized bytes. After metadata preparation and disk flush, capture its file ID.
   Commit locks and re-verifies both exact objects, content and metadata before the
   journal/name handoff. Independent actual-thread truncation to `NOT AUDIO` now
   reports `saved=False invalidPromoted=False`. Tests additionally replace the
   candidate pathname, replace it with equal bytes/timestamps at a different file ID,
   and attempt ordinary writes/renames while its guard is held. All refuse safely
   or deny the mutation; the original is not discarded.

The separate Android report's desktop-relevant boundaries also have focused tests:
`iloc`/UUID inside `moov/udta/meta` (full-box prefix parsed), and unknown XPOS with
v2.3 status 0x80 / v2.4 status 0x40. All three requested operation types refuse with
original bytes/mtime unchanged. No Android file or emulator was accessed.

## Results and evidence

Full Release App: **540 passed, 1 existing opt-in endpoint skip**. Core: **70 passed**.
Workflow constraints: **7 passed**. Existing format preservation tests remain
unchanged apart from the policy-version assertion. Actual NativeAOT 1.0.165 normal
import/`EmbedPrevious`/production-save path updates **39/39** isolated synthetic
files; separate raw parsers confirm encoded audio and complete bilingual/timed LRC.
The NativeAOT startup recovery experiment terminates the separate production writer
at source reservation, then starts the real client against its already indexed
isolated library: restoration occurs before scanning, retains the one DB record,
exact original SHA/ADS/mtime, and consumes the journal.

Ignored local evidence under `desktop/artifacts/tag-write-review2/`:

* `results/{app-complete,core-complete}.trx`
* `repro/{production-verified-evidence,additional-complete-evidence}.txt`
* `runtime-business/independent-verification.json`
* `runtime-startup-complete/{pending-journal,verification,logs}.json`
* `restart-final/verification.json` (fresh main-client state, no user audio hashes)

The reviewer's source directory was only read; its harness copies execute in our
own ignored directory. Earlier failing development runs are retained as diagnostic
history and are not reported as acceptance passes.

## Explicit bounds

The final publication is a no-replace name handoff with a short gap, not continuous
atomic visibility. The normal gap no longer contains full-file hashes or metadata
copying. Recovery covers actual process kill; sudden power loss/storage-firmware
durability and ancestor-junction races are not certified. Unmapped policy-2 backups
are not guessed at or deleted. Special filesystem flags, existing DOS aliases,
object-ID/EA/unknown backup records, oversized ADS or unreadable/non-copyable security
metadata refuse and use the existing external-file fallback. See the full
[policy](../../docs/tag-writing-policy.md#windows-no-clobber-commit-and-interruption-boundary).

No product UI feature changed. The previously documented native-pipe failure has no
evidence of an environment change; no repeated UI attempts or permission bypass is
made. Real UI/screenshots remain unaccepted. The daily candidate restart preserves
fresh playback/queue/source/user tables and saved settings/layout; live pixel-layout
equality is not claimed. No push/release or Android integration occurs in this stage.
