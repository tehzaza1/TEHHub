# Primary-root research and adversarial arena

This first version is a deterministic, read-only search and validation system, not a
trained AI. It provides reproducible environments and labeled results for later model
experiments. Confidence scores never authorize an unproven pointer.

## Run in GameHelper

Open OffsetHelper, enable **Enable offset TryFix**, expand **Offset TryFix status /
saved recoveries**, and click **Research primary root (read-only)**. Stay in-world.
The task runs in the background; Cancel, disabling TryFix, closing the handle or
reattaching cancels it. A SafeHandle lease keeps the original read handle alive until
the task finishes. It never writes game memory or installs a primary root automatically.

Output goes beside the existing recovery evidence in `configs/offset-recovery` under
the running executable: `primary-root.latest.json`, review notes in
`primary-root.latest.cs.txt`, and unique historical event files. JSON includes module
SHA-256, session identity, per-observation read/byte counts, proposed globals, structural
hypotheses, rejection counts, and confirmed graph candidates. Only three complete
scans with the same unique graph and area identity produce `Confirmed=true`.

## Evidence chain and ancestor failures

The source guide in `GameOffsets/StaticOffsetsPatterns.cs` explains how the diagnostic
string "Unable to get InGameState" leads to a Game States global. This implementation
proposes globals from bounded x64 RIP-relative CMP/MOV byte motifs in readable
executable PE sections, then validates pointer graphs independently. Byte motifs are
not a full instruction decoder, and do not establish correctness by themselves.

The dependency order is:

```
module code reference -> global slot -> manager
  -> state ownership table + active-state vector
    -> active state -> area -> player path + component owner
    -> active state -> UI host -> UI self + child/parent backlink
```

The manager table is searched around its compiled location. State order may differ.
Current contracts support 13 slots with 12 or 13 instantiated, distinct state objects;
one lazy null slot is allowed. Both table and active entries must satisfy the embedded
shared-state ownership relation observed in the current live build. The most populated
valid table wins only if the resulting graph is unique. Conflicting ownership at a
table boundary cannot be hidden by sliding into padding.

If the manager cannot be read, descendants are **blocked**, not individually declared
broken. If the manager/table are plausible but player/UI evidence is unavailable,
the result retains **structural root hypotheses** and abstains from confirmation. A
menu/login scene may legitimately have no area or player. A structural hypothesis is
never exported as a confirmed offset.

Leaf offsets still use the current GameOffsets contract: area/player, UI host, strings,
component header and UI layout. Simultaneous unknown leaf shifts can prevent full
confirmation even if the structural head is discovered. This version does not solve
arbitrary relocation of every member, missing code motifs, separately allocated shared
state objects, alternate string encodings, or spoofed graphs indistinguishable under
the tested contracts. Those require additional adapters/evidence. Atlas node counts
and fixed controller child indices are not required to find a primary root.

Each scan has limits of 128 MiB executable bytes, 250,000 reads, 4,096 proposed global
slots and 15 seconds. Incomplete scans, unreadable executable chunks, cancellation,
multiple valid worlds and global aliases abstain. "Complete" means all supplied
supported executable sections/motifs were scanned; it is not proof of an exhaustive
search over every possible x64 instruction or the whole process heap.

## Arena and reproducible labels

```
dotnet run --project tests/OffsetRecovery.Tests/OffsetRecovery.Tests.csproj -c Debug
```

The arena uses the production searcher behind a sparse memory interface. No ground
truth, preferred address or expected answer is supplied to the searcher. Sixty fixed
seeds vary addresses, table positions, state ordering, input mode and random bytes.
Each positive scene contains a genuine graph, eight deep decoy worlds with one broken
invariant, and thirty shallow decoys. A second observation mutates the genuine graph
into a no-answer scene. Separate cases cover:

- A code reference split across the scan-chunk boundary and repeated references.
- Two complete valid worlds and aliases to the same manager: both remain ambiguous.
- Wrong owners, wrong parent links, invalid string extents/content, duplicate states,
  foreign active states, invalid UI self-pointers and oversized/reversed vectors.
- Area identity changes between observations, lazy states and corrupt ownership pairs.
- An intact parent with unavailable descendants, and an absent global head while all
  child allocations remain readable.
- Read/code budgets, cancellation, unreadable executable pages, malformed PE headers,
  and actual PE section parsing against the test process's own read-only memory.

`root-arena-results.json` under the test executable output records the 120 seeded
positive/no-answer trials with labels, exact root ground truth for positives and
production results. Seeds and scenario mutations regenerate the environments. The
additional policy/edge cases are assertions in the same executable; they are not
included in those 120 trial records. Passing the arena does not establish a real-game
success rate or a trained model's generalization performance.

For explicit read-only live research:

```
dotnet run --project tests/OffsetRecovery.Tests/OffsetRecovery.Tests.csproj -c Debug -- --research-live <PoE-process-id>
```

This separate mode checks the supplied process name, opens that PoE process for
reading, and runs the same research task. Configured-pattern diagnostics are printed
for comparison and never fed to the root searcher as an answer. Reports in this mode
are written under the test executable output, not the ordinary GameHelper output.
