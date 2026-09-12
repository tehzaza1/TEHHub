# Offset TryFix

Toggle **Enable offset TryFix** in OffsetHelper. The option is persisted in the core
settings. Disabling removes session projections and component quarantines immediately;
it preserves saved evidence. Ordinary OffsetHelper diagnostic sweeps remain available.

Background sweeps run every ten seconds, including while OffsetHelper is closed,
after a three-second area settling period. A repair requires three observations at
least five seconds apart. A candidate is projected over raw bytes for validation before
it is installed. No target-process writes or source-code modifications occur.

## Evidence and scope

The common reader projects recovered top-level blocks into their compiled scalar
struct layout. This runs before memory caches, so consumers use the same recovered
values. ComponentBase owner reads follow recovered component headers too. Changing
recovery state invalidates cached component instances through a generation counter.

Auto-application currently requires:

- A registered component layout, unambiguous candidates, at least two independent
  component owners, and all sampled roots passing full validation. Each moved block
  needs exact owner evidence or a supplied exact vital total. A readable pointer or
  well-shaped vector alone cannot prove the identity of the data.
- For the controller map parent: both child UI self-pointers pass, the children differ,
  and both addresses match the independently resolved runtime map addresses.

Generic component headers used by multiple unrelated marker layouts, native array
element layouts, arbitrary numeric fields and root layouts without a dedicated repair
contract are not automatically repaired. Existing semantic scanners can still propose
candidates. Character hints are session-only; this first policy deliberately rejects
mixed-root vital candidates without enough exact evidence rather than applying a
player's health total to monsters. **This is not a guarantee that every offset can be
recovered or even diagnosed from plausible values.**

`coverage.latest.json` inventories every explicit FieldOffset struct in TEHhub.Offsets,
including layouts without live probes or semantic repair contracts. Hardcoded numeric
constants and static-address signatures are not scalar struct projections; existing
signature rescan and domain fallbacks remain responsible for them.

## Failure handling

Recoveries are rechecked on background sweeps, not on every field read. A failed
recheck immediately revokes the projection. Losing live roots, changing areas or
reattaching also suspends it and requires fresh evidence. A native array read of a
patched scalar type fails, revokes the projection and prevents reinstalling that type:
recovering member offsets does not establish the native element stride.

Three spaced observations of decisive failures across all sampled independent
component owners suspend access through `Entity.TryGetComponent`. Healthy original
anchors or a successful repair restore access. Root objects without a safe repair
contract retain their existing behavior/fallback; they are not globally suppressed.
Disabling TryFix restores ordinary compiled-offset behavior, including any existing
limitations when those offsets are broken.

## Saved output

Under the running TEHhub folder:

```
configs/offset-recovery/
  coverage.latest.json
  <Struct>.latest.json
  <Struct>.latest.cs.txt
  <Struct>.<UTC>.<action>.<unique-id>.json
  <Struct>.<UTC>.<action>.<unique-id>.cs.txt
```

JSON records the original/recovered offsets, native types, original and recovered
anchor evidence, game module path/version/length/mtime/SHA-256, action and UTC time.
Historical events are immutable; `latest` files are replaced atomically per file.
The snippets are commented FieldOffset suggestions, to review against the matching
game build before changing the existing declarations. Use the latest JSON action to
distinguish `activated` from candidates, suspensions, disable and revocation events.
The two latest files are not a cross-file transaction.

Evidence must be saved before activation and activation status must also save
successfully; otherwise the projection is withdrawn. Dump errors appear in the UI.
Saved offsets are never automatically trusted or loaded into a later game session.

## Validation

```
dotnet run --project tests/OffsetRecovery.Tests/OffsetRecovery.Tests.csproj -c Debug
dotnet build TEHhub.sln -c Debug --no-restore
dotnet build TEHhub.sln -c Release --no-restore
```

The dependency-free test executable uses the real read-only native memory reader on
allocations in its own process. It exercises overlapping source moves, independent
owners, ambiguity, spaced confirmations, rollback, toggle, loss of roots, area changes,
native-array rejection, component quarantine and saved evidence. It does not attach to
or modify a game. Tests write their own reports beneath their output folder.
