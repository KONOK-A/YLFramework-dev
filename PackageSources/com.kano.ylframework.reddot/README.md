# YLFramework Red Dots

The existing `YLFramework.RedDots` namespace now belongs to the independent
`YLFramework.RedDots` Runtime assembly. Install this package with its main-package
dependency and add `YLFramework.RedDots` to consumer asmdef references. The core
does not require Save, Gameplay, Excel or an additional font/UI package beyond
the main package's existing dependencies.

`ReddotManager` retains its original normalized, case-sensitive whole-segment
paths. `AddPath` is an idempotent set, while `RegisterPath` returns an independently
owned disposable registration. Reset invalidates old registrations and preserves
subscriptions; a new play session clears both sources and subscriptions. The
legacy `RegisterChanged` does not emit an initial state. Observer failures remain
visible and a failed registration rolls its own source back.

The original `Reddot` component and script GUIDs are preserved. It changes only
the explicitly assigned indicator, never its own host or an ancestor. Existing
serialized `path` / `indicator` fields and their former names remain unchanged.

The main package no longer contains these types. Update consumer assemblies and
package installation together; preserving a script GUID does not preserve the old
assembly-qualified resource tuple. Rebuild resource catalogs through the actual
publishing flow where they record that tuple. Removing this package does not
remove user components or scripts; adjust those consumer references explicitly.

## Values And Read State

Call the core on Unity's main thread. Numeric registrations are independently
owned by their normalized path and Ordinal `sourceId`. Values are nonnegative
`long`; invalid values and overflow are rejected before publishing a changed value.
The default aggregation is Sum. Any returns 0 or 1; Max compares the node's own
source sum with each direct child's aggregated value.

```csharp
using var source = ReddotManager.RegisterSource("Mail/Gift", "inbox", initialValue: 3);
using var subscription = ReddotManager.Subscribe("Mail", snapshot => Render(snapshot.Value));
using (ReddotManager.BeginBatch())
{
    source.SetValue(4);
    ReddotManager.SetAggregation("Mail", ReddotAggregation.Max);
}
```

Subscriptions can emit an initial snapshot; the legacy `RegisterChanged` still
does not. Unchanged values do not notify. Nested batches flush at their outermost
Dispose; observer errors remain visible without leaving the dispatcher wedged.
Reset invalidates old sources but preserves subscriptions and aggregation rules.
New play sessions clear sources, subscriptions and rules. Old tokens cannot
release a new generation's source.

`ReddotReadState` is an explicitly owned instance with an account/slot namespace.
It stores the set of confirmed `(key, string version)` pairs, not a maximum version.
`BindUnreadSource` contributes 1 only for the exact current unread pair;
`SetVersion` does not mark that version read. `Forget(key, version)` forgets only
that pair, while `PruneRemovedKeys` is an explicit cleanup operation. `Export()`
returns a deep-copy schema-1 snapshot; `Import()` validates its complete contents
and namespace before replacing the confirmed set. Neither operation performs IO.

Installing Save additionally enables the separate `YLFramework.RedDots.Save`
assembly by package version definition. Consumers of the optional composition
reference that assembly. Its `ReadReddotSnapshotAsync` and
`SaveReddotSnapshotAsync` extensions use an existing `SaveStore` and the caller's
explicit name/id/cancellation token. Reading never imports the snapshot or changes
live counters; Missing, backup recovery, corrupt data and IO failures retain the
Save contract. The core works without Save, and no global read-state store,
automatic slot switching, PlayerPrefs or background persistence is introduced.

## Read-Only Diagnostics

Open **YLFramework > Red Dots > Diagnostics**. The Editor-only window shows the
normalized path tree, value/rule, own value, collection/lease counts, numeric
source IDs and values, subscribers, generation, dirty nodes and batch state.
Source IDs are the actual business IDs; the core does not invent human labels or
retain callback/Unity object references for debugging.

Automatic capture is throttled to 4 Hz only while the window is focused or its
explicit **Auto Sample When Unfocused** toggle is enabled. **Capture** requests a
single sample. Disabling or destroying the window removes its Editor update
callback. No runtime host or global sampling service is created.

Runtime callers can explicitly request an immutable copy on the main thread:

```csharp
var diagnostic = ReddotManager.CaptureDiagnostics();
foreach (var node in diagnostic.Nodes)
{
    // node.Path, Value, Aggregation, OwnValue, Sources and SubscriberCount
}
```

`Nodes` and each node's own `Sources` are copied, Ordinal-sorted, read-only
collections. Zero-value nodes created by subscriptions/rules remain visible;
there is no synthetic empty-path root. Capture never flushes a batch or invokes
observers. `CaptureSequence` changes only for explicit captures; `Generation`
is a diagnostic number, not a credential. Normal `GetValue`, `GetSnapshot` and
source updates never capture the tree. Canonical paths such as `Mail/Gift/`
avoid normalization allocations without changing whitespace/segment rules or
the existing `ReddotSnapshot` value type.

## Source-Repository Validation

The `Reddot` Quick suite measures the real core, not a simulated old algorithm
or Unity native execution. Its fixed four-level workload has 64 leaves, three
sources per leaf, one reentrant echo source, eight warm-up iterations, 128
measured iterations (24,576 updates), and 100,000 warmed GetValue/GetSnapshot
pairs. Non-batch and per-iteration Batch runs must reach the same value with
exact notification counts; time and per-thread allocated bytes are reported
without a machine-speed threshold. Explicit diagnostic capture is measured
separately from ordinary reads/updates.

`Special / ReddotPlayer` is an opt-in, expensive source-repository entry. Its
NUnit Editor test uses the shared owned-host Player builder to build once and
run two independent Windows Mono Players with only main + reddot and the
acceptance probe. The probe exercises real aggregation, subscriptions, Batch,
serialized UI indicator lifecycle, read-state Export/Import/version changes,
old generations and the same representative performance workload. Resource is
not initialized and reports an empty catalog digest and actual absent-authority
lease/record counts. This entry is not part of Daily or Quick. Running Quick
does not establish Editor or Player acceptance.

Player allocation measurements first calibrate the runtime counter against a
known 64 KiB allocation. A missing or constant-zero Mono counter is reported as
`allocationCounterReliable=false` and all allocation deltas as `-1` (unavailable),
never as measured zero. Timing, exact values, notifications and lifecycle checks
still run. The source Quick allocation comparison is separate evidence and must
not be presented as a Unity Player allocation guarantee.
