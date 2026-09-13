# Primary-root research and adversarial arena

The scanner is a deterministic, read-only search and validation system. An optional
local AI review can rank candidates already present in its saved evidence. This does
not train a model or replace structural validation. Confidence scores never authorize
an unproven pointer.

## Run in TEHhub

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

## Optional local AI review

In OffsetHelper, click **AI-assisted root research (local)** to run the same
read-only three-scan research task, then ask a local Ollama model to review bounded
evidence from that report. Ollama must be running at `localhost:11434`; requests use
`/api/chat`. The default model is `qwen2.5-coder:7b-instruct-q4_K_M`, and the model
field can be changed to another installed model.

The model receives candidate IDs and their scanner evidence. It can recommend an
existing `candidateId` for inspection or abstain; it cannot invent an address or
install an offset. Its `decision` must be `inspect` or `abstain`, and `nextProbe`
must be one of `player-components`, `ui-backlinks`, `state-ownership`,
`rescan-in-world`, or `unsupported-layout`. Unknown candidate IDs, unknown enum
values and malformed responses are rejected. A suggested probe is a review
recommendation, not proof that the probe ran or passed.

Review evidence is saved in `configs/offset-recovery` as
`primary-root.ai.<timestamp>.<guid>.json` and `primary-root.ai.latest.json`. A
report SHA-256 binds the review to its input evidence. Keep that report with the
review when investigating a result; the review alone does not prove a live pointer
is still valid. Cancel, disabling TryFix or reattaching cancels the task.

The deterministic scanner remains the validator. An AI recommendation does not
change `Confirmed`, bypass ambiguity, repair missing ancestors or automatically
apply a primary root. This feature uses an existing local model for inference; it
does not train or fine-tune that model. Arena results and saved reviews must not be
interpreted as a measured live-game AI success rate.

## Evidence chain and ancestor failures

The source guide in `TEHhub.Offsets/StaticOffsetsPatterns.cs` explains how the diagnostic
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

Leaf offsets still use the current TEHhub.Offsets contract: area/player, UI host, strings,
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

The local AI transport can be checked separately with synthetic missing-descendant evidence (requires running Ollama and the default model):

```
dotnet run --project tests/OffsetRecovery.Tests/OffsetRecovery.Tests.csproj -c Release -- --ai-review-smoke
```

This invokes the production localhost client and strict decision parser, saves a synthetic review, and never attaches to the game. Its result is not a live recovery success rate. The ordinary suite tests adversarial AI decisions without network access.

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
are written under the test executable output, not the ordinary TEHhub output.
