# YLFramework Overlap

Install `com.kano.ylframework.overlap` with its main-package dependency. Consumer
assemblies explicitly reference `YLFramework.Overlap`; custom Editor consumers
that directly use its Inspector/Gizmos types also reference `YLFramework.Overlap.Editor`.
The package directly declares Unity's built-in physics and physics2d modules.
For Git installation, explicitly declare main and overlap at the same verified
full revision; package-level SemVer dependencies do not fetch sibling Git packages.
Namespaces, original component script GUIDs and
serialized fields are retained. The main package no longer owns these types.

This package does not install movement2d, Gameplay, RedDots or Save. Original
2D/3D components retain their fixed 100-entry per-instance query buffer, reentry
guard and NonAlloc shape behavior. Reaching capacity reports possible truncation
at most once per enabled session in Editor/Development, not proven completeness.
In particular,
disabling an original component clears its local state without synthesizing Exit.
Legacy 3D hit points remain the existing XY projection; new casts use native `RaycastHit`/`Vector3`.

Runtime sources live under `Runtime/Utilities/Overlap`; Inspector/Gizmos sources
live under `Editor/Utilities/Overlap`. Runtime namespaces remain `YLFramework.Overlap`
and Editor namespaces remain `YLFramework.Editor.Overlap`. New stateless query/Cast
APIs live under `Runtime/Queries`; continuous detectors and composite areas are separate work.

## Caller-Buffer Queries

`OverlapQuery2D.Query/Cast` accept world-space `OverlapShape2D`, explicit
`OverlapFilter2D(ContactFilter2D, ...)`, and `Collider2D[]`/`RaycastHit2D[]`.
Shapes are `Point`, `Circle`, `Box` (full size and Z angle), and `Capsule`
(full size, capsule direction and Z angle). Point casts are rays.
`OverlapQuery3D.Query/Cast` support `Sphere`, `Box` (full size and orientation),
and `Capsule` (cap centers and radius); `Raycast` has a separate origin argument.
3D filters require a layer mask and default to `QueryTriggerInteraction.Ignore`;
explicit `Collide` is supported, while `UseGlobal` is rejected. Every entry has
an explicit `PhysicsScene2D`/`PhysicsScene` and a default-scene overload.

All calls are synchronous, main-thread operations. Arrays are caller-owned,
never enlarged or retained, and slots after `Count` are cleared. `RawCount`
records native capacity use before ignore/root/body filters. Raw saturation
always means `MayBeTruncated`, even if visible `Count` becomes zero; otherwise
the native snapshot is `Complete`. Unloaded/invalid scenes return empty
`Invalidated`. Unity also suppresses 2D triggers globally when
`Physics2D.queriesHitTriggers` is false, even with `ContactFilter2D.useTriggers=true`:
an unsatisfiable trigger-including request returns empty `Invalidated`, without
changing that setting or claiming a complete miss. This is a known limitation,
not support for overriding the global setting per query. `InvalidationReason`
distinguishes `InvalidScene` from `TriggerQueriesDisabled`; a successful query
has `None`. Enabling the project's Trigger-query setting is an explicit consumer
decision, never an automatic side effect of this API. 2D rays also retain
Unity's `queriesStartInColliders` behavior. 3D starting-overlap contacts depend
on the native shape query; no contacts or normals are manufactured.

Null/empty buffers, nonfinite geometry, nonpositive volume dimensions, unusable
directions and negative/nonfinite cast distances are rejected before output
mutation. Zero cast distance is legal. Point has no extent, equal 2D capsule
axis/diameter is legal, and coincident 3D capsule cap centers form a sphere;
other zero volume dimensions are rejected. Default 2D shape is the origin Point;
default 3D shape is invalid. Depth filter infinities are allowed as open bounds.

Optional `ignoredCollider`/`ignoredRoot` exclude the root itself and descendants.
`groupByRigidbody` uses actual attached bodies and keeps bodyless colliders
independent, selecting the nearest representative under the same distance metric.
`sortByDistance` orders overlaps by squared distance from shape center to the
collider's world AABB (XY for 2D, XYZ for 3D), not exact surface distance; this
also supports nonconvex mesh/terrain colliders. Casts use native hit distance.
Both use signed collider instance ID for ties within a session.
Without grouping/sorting the native collider-unique order is retained. Optional
grouping/sorting uses in-place heapsort, O(n log n) comparisons and O(1) extra
storage, bounded by the supplied array; it does not promise cross-process order
or globally nearest results from a truncated buffer. No allocation/timing
measurement is implied.

Update package installation and consumer asmdef references together. GUID
preservation does not rewrite assembly-qualified type names in external DLLs,
JSON, resource catalogs or historical bundles. Rebuild such resource content
through the real publishing flow where it records the old assembly tuple. Package
removal does not delete user components, scripts or project data that reference
the removed types; adjust those references explicitly.
