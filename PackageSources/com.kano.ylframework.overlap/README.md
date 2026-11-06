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
APIs live under `Runtime/Queries`; continuous detectors and local Union configurations
live under `Runtime/Detection`.

## Caller-Buffer Queries

`OverlapQuery2D.Query/Cast` accept world-space `OverlapShape2D`, explicit
`OverlapFilter2D(ContactFilter2D, ...)`, and `Collider2D[]`/`RaycastHit2D[]`.
Shapes are `Point`, `Circle`, `Box` (full size and Z angle), `Capsule`
(full size, capsule direction and Z angle), and `Sector` (see below). Point casts are rays.
`OverlapQuery3D.Query/Cast` support `Sphere`, `Box` (full size and orientation),
and `Capsule` (cap centers and radius); `Raycast` has a separate origin argument.
3D `Query` also supports extruded `Sector`/annular sector/ring and finite `Cone`
with an explicit workspace and a narrower target support domain described below.
3D filters require a layer mask and default to `QueryTriggerInteraction.Ignore`;
explicit `Collide` is supported, while `UseGlobal` is rejected. Every entry has
an explicit `PhysicsScene2D`/`PhysicsScene` and a default-scene overload.

All calls are synchronous, main-thread operations. Arrays are caller-owned,
never enlarged or retained, and slots after `Count` are cleared. `RawCount`
records native capacity use before narrow geometry and ignore/root/body filters. Raw saturation
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
also supports nonconvex mesh/terrain colliders for the original primitive queries.
New curved 3D queries deliberately do not support those targets. Casts use native hit distance.
Both use signed collider instance ID for ties within a session.
Without grouping/sorting the native collider-unique order is retained. Optional
grouping/sorting uses in-place heapsort, O(n log n) comparisons and O(1) extra
storage, bounded by the supplied array; it does not promise cross-process order
or globally nearest results from a truncated buffer. No allocation/timing
measurement is implied.

### 2D Sector, Annular Sector And Ring

`OverlapShape2D.Sector(Vector2 center, float outerRadius, float angleDegrees,
float innerRadius = 0, float rotationDegrees = 0)` describes a **filled world XY**
region. Forward is +X rotated about Z by `rotationDegrees`; `angleDegrees` is its
aperture, not its rotation. `OuterRadius > 0`, `0 <= InnerRadius < OuterRadius`,
and `0 < AngleDegrees <= 360` are required. Zero inner radius includes the apex;
360 degrees has no radial boundaries and describes a disk or full ring. Existing
`Angle` still means Z rotation; the named `RotationDegrees` property makes that
meaning explicit for a sector. The original four enum values are unchanged.

Sector queries require an explicit, reusable `OverlapQueryWorkspace2D` as the
**final** argument to either existing scene/default-scene `Query` overload:

```csharp
using var workspace = new OverlapQueryWorkspace2D(
    maxShapesPerCollider: 256, maxVerticesPerShape: 4096, geometryTolerance: 0.00001f);
var region = OverlapShape2D.Sector(center, 5, 120, innerRadius: 1, rotationDegrees: 30);
var result = OverlapQuery2D.Query(scene, region, filter, callerResults, workspace);
```

The workspace is a plain caller-owned `sealed IDisposable`, not a component or
global cache. Construct it outside recurring queries, keep it private to an owner,
and dispose it when that owner stops. It retains only managed low-level geometry
storage and numeric vertices, never Colliders, bodies, scenes or cached geometry.
Disposed or reentrant use throws before touching output. The old overloads remain
unchanged for all original shapes; Sector without a workspace returns empty
`Invalidated / WorkspaceRequired`. Sector Cast returns empty
`Invalidated / UnsupportedCast` before any native cast. It is not a compound
sweep, an occlusion/visibility test, or a slash animation.

The broad phase is the native scene circle with outer radius expanded outward by
the workspace's world-unit tolerance (rounded outward to float). Raw saturation
remains `MayBeTruncated`, even when every candidate is in the hole, ignored, or
body-grouped. Every allowed candidate is re-read with
`Collider2D.GetShapes(group, shapeIndex, 1)` on **every query**. No hash cache,
scene scan, helper Collider or global Physics setting is used. Physics pose/sync
responsibility is the same as for the original native queries; the API does not
call project-wide `Physics2D.SyncTransforms`.

The narrow phase analytically computes distance to the real exported shapes:
Circle = point plus native radius, Capsule = segment plus radius, convex Polygon
= filled convex polygon plus radius, Edges = individual segments plus radius
(never a filled hull). Vertices use only the returned `localToWorldMatrix`:
body-local geometry receives its body pose; static bodyless geometry is already
world-space. Collider transform, offset and scale are **not applied twice**.
All shapes of Polygon/Composite colliders are visited. Real inner/outer arcs,
finite radial edges, segment/arc crossings and tangencies, and both containment
directions are handled without polygonizing arcs. Calculations use double
intermediates over Unity's float-exported geometry and accept distance no greater
than `shape.radius + geometryTolerance`. This is a floating-point analytic
policy, **not** bit-for-bit equality with Unity's native narrow phase. Native
broad-phase/filter policy still gates candidate admission; tolerance does not
recover geometry omitted by the engine or unsynchronized transforms.

The body translation minus sector center is evaluated before the rotated local
vertex, preserving a small native body-local offset under a common large world
translation. Circle intersections and point-to-segment distances use normalized
line coordinates/normal distances, not a rounded huge-segment parameter multiplied
back into its endpoints. Residual sum errors and conservative line/root/interval
roundoff bounds are checked against a fraction (1/16 per checked operation) of the
configured world-unit tolerance. Uncertified severe cancellation explicitly gives
`InvalidGeometry`, not `Complete` with an invented hit or miss; it does not enlarge
the geometric tolerance. This finite arithmetic policy is not a claim that every
finite configuration or every engine extreme-coordinate query is certified.

Supported high-level colliders are Circle, Capsule, Box, Polygon, Edge, and
Composite (Polygons or Outlines), including the engine's generated nonuniformly
scaled target geometry. The workspace preallocates one native shape slot and its
configured vertex storage. **Before extraction**, `Collider2D.shapeCount` is checked
against `maxShapesPerCollider`; conservative per-shape vertex bounds are 1 for
Circle, 2 for Capsule, `Physics2D.MaxPolygonShapeVertices` for Box/Polygon/Composite
Polygons, `EdgeCollider2D.pointCount` for Edge, and
`CompositeCollider2D.pointCount + 1` for Outlines (closed chains repeat a point).
Exceeding these bounds returns empty `Invalidated / GeometryBudgetExceeded`, even
if an individual convex shape would have fit. A list's capacity is not treated
as a limit. The default 4096-vertex budget is finite, not unlimited precision or
an allocation measurement. CPU work is bounded by native shape and vertex limits
per candidate; steady-state zero allocation is not claimed without measurement.

Tilemap, CustomCollider2D and unknown types are deliberately
`Invalidated / UnsupportedGeometry` because a trusted pre-extraction vertex
bound has not been established here. Missing/malformed/nonfinite exported
geometry or failed native reads return `InvalidGeometry`; no unknown geometry
silently becomes a complete miss. Ignore/root filters run first, so an explicitly
ignored unsupported collider need not be extracted. Failure clears the entire
output, preserves the observed `RawCount`, and cannot commit detector contacts.

### 3D Extruded Sector, Ring And Finite Cone

`OverlapShape3D.Sector(center, rotation, outerRadius, angleDegrees, height,
innerRadius = 0)` is a filled local **XZ** region: +Z is the middle direction,
+Y is its centered positive thickness. Outer radius and height must be positive,
`0 <= InnerRadius < Radius`, and `0 < AngleDegrees <= 360`. At 360 degrees it is
a disk or ring with no radial seam closing the hole. `AnnularSector` and `Ring`
are named factories for the same Sector kind. Identity-rotation Sector/Ring
overloads are available. Existing enum values and primitive signatures are unchanged.

`OverlapShape3D.Cone(apex, rotation, height, baseRadius)` is a finite +Z cone with
a closed base. Its `Center` is the apex and `Radius` is the base radius.
`ConeFromAngle(apex, rotation, height, angleDegrees)` only converts a full opening
angle in `(0,180)` through `baseRadius = height * tan(angleDegrees/2)`.
It is not an infinite cone or a spherical sector. These are pure world values,
not Transforms, meshes, colliders, business FOV or occlusion tests.

Both new kinds require an explicit reusable **final** workspace argument:

```csharp
using var workspace = new OverlapQueryWorkspace3D(
    requestedBoundaryError: 0.01f, maxSegments: 128, maxParts: 128,
    maxHierarchyDepth: 64);
var region = OverlapShape3D.Sector(center, rotation, 5, 120, 2, innerRadius: 1);
var result = OverlapQuery3D.Query(physicsScene, region, filter, callerResults, workspace);
```

Construct/retain it per owner, outside recurring queries, on the main thread in
Play Mode. The old entry without a workspace gives `WorkspaceRequired` for new
kinds. New curved queries outside Play Mode give `RequiresPlayMode`; constructing
the runtime workspace outside Play Mode throws, without creating an Editor scene.
Sector/Cone Cast returns empty `UnsupportedCast`, never a fabricated compound sweep.
Original primitive queries/Casts retain their existing native target support.

#### Approximation And Finite Budgets

This is a **conservative generated convex approximation**, not analytic round
intersection, a native bit-exact result, or a closed-contact/tangency guarantee.
Each angular cell is at most 90 degrees. Sector cells use triangular prisms when
inner radius is zero and quadrilateral prisms otherwise; a ring is never cooked
as a single convex hull. For cell angle `d`, outer endpoints have radius
`R*sec(d/2)`, inner endpoints radius `r`. Ideal tessellation outer excess is
`R*(sec(d/2)-1)` and hole intrusion is `r*(1-cos(d/2))`.

Cone uses a closed circumscribed regular N-sided pyramid with ideal radial excess
`R*(sec(pi/N)-1)`. Up to N=128 its triangle count is at most 254. Larger N is split
into N four-triangle tetrahedra. Every generated part is convex, readable and at
most 255 triangles. `requestedBoundaryError` is a positive **world-unit generated
tessellation** budget, not a collider contact allowance. It does not bound float
rounding, Unity cooking, native penetration, broad-phase or total engine error.
Exact zero-distance contact is not manufactured by adding spheres or inflating a
native tolerance. Targets within the approximation band can hit outside the ideal
round region. Consumers needing a smaller band must request it and fund its budget.

Segment and part limits are explicit; insufficient precision/part/hierarchy budgets
give `GeometryBudgetExceeded`, never silently coarser geometry. Float-collapsed or
uncookable generated parts give `CookingFailed`. `BakeMesh` is void: the workspace
also verifies each generated part using a same-world interior penetration positive
control; that is not a proof of every cooking/boundary error. Broad-phase boxes
cover expanded parts and full thickness/length. Cone uses an **apex-centered world
AABB**, containing the rotated symmetric local envelope `[-R,R] x [-R,R] x [-H,H]`.
Double row-sum bounds are outward-rounded to float full sizes, then the exact
float half sizes passed to the native query are certified against those bounds;
unrepresentable bounds give `InvalidGeometry` before cooking. Keeping the exact
apex avoids float midpoint translation shrinking the candidate domain. This box
is deliberately not tight: it admits more candidates behind the apex and for
rotated cones, so it can increase copy/CP work, raw saturation or admitted unsupported
targets. Filters/narrow phase and precision budgets are unchanged. Certification
is for the generated pose/envelope, not total native cooking/float contact error.
Distance sorting still uses the **apex**.

`SegmentCount`, `RequiredPartCount`, `PartCount`, `GeneratedBoundaryError` and
`GeometryReason` describe generation. `CookCount` counts bake attempts and
`GeometryBuildCount` counts builds; `CreatedObjectCount` is cumulative creation,
not instant native memory use. Query `InvalidationReason` separately describes
unsupported or malformed candidates. Part budgets bound the current region, not
Destroy's end-of-frame native-memory backlog.
`CookingError` names the failing part and float-degeneration, native interior-control
or native cook-exception/argument diagnostic; native log messages are not suppressed.

#### Targets And Private Physics World

The new narrow phase supports only **BoxCollider, SphereCollider, CapsuleCollider**
with finite, nondegenerate geometry and a faithfully copied Transform ancestry.
No-body, kinematic and dynamic bodies with `RigidbodyInterpolation.None` are
supported. Interpolated/extrapolated body poses are deliberately `UnsupportedGeometry`
because render interpolation is not a certified native physics-snapshot pose.
Every MeshCollider (convex or nonconvex), TerrainCollider, CharacterController and
unknown type is `UnsupportedGeometry`. Render/shared mesh faces are not used as
an invented cooked hull. Choose a business target/hurtbox `LayerMask`; do not query
everything and expect unsupported environment colliders to be silently ignored.
An explicitly ignored candidate need not be copied. An allowed unsupported candidate
invalidates even if its center or bounds would seem outside the ideal region/hole.

Each workspace owns exactly one additive **local PhysicsScene**, private convex
parts, three primitive proxy components and a lazily built pure-Transform chain.
Both colliders in `ComputePenetration` are active/enabled in that SAME private world.
No helper is ever enabled in a target world; the original target GameObject is
never instantiated, moved or destroyed, and scripts/Rigidbody are not cloned.
Inactive helpers and direct cross-world penetration are not relied on.

On EVERY allowed candidate, the workspace rereads primitive geometry parameters
and the complete source ancestor local TRS, up to `MaxHierarchyDepth` including
the collider Transform. It recreates that chain using only private Transforms,
and rejects a nonfinite/singular or nonidentical resulting world matrix. This
preserves tested rotated, mirrored, nonuniform and compound-body target geometry;
it does not substitute `Abs(lossyScale)` or cache a target's old geometry/pose.
Source broad-phase pose/sync responsibility remains Unity's normal caller contract.
The query does not call global SyncTransforms, Simulate, SetActiveScene or physics
setting setters. The private world is never simulated and has no Rigidbody/scripts.

There is no global manager/pool, scene scan or hidden per-call workspace. The
one-entry cooked geometry cache keys only kind/dimensions and immutable precision
configuration, not pose. Consecutive pose-only queries reuse parts. Changing or
alternating dimensions rebuilds owned geometry; a failed replacement invalidates
the cache, so stale parts cannot be paired with a new broad radius. This is not
a promise of free alternating-Union recooking, per-candidate enable, or zero GC.

`Begin` rejects reentry and disposed use; in-query Dispose is rejected. End clears
borrowed ancestor references and disables proxies. Dispose is idempotent, immediately
rejects later queries, disables owned colliders, queues destruction of OWNED objects
and meshes, then queues `UnloadSceneAsync` on that exact owned scene. Its
`ReleaseOperation` exposes asynchronous completion: native memory/scene release is
not instantaneous. No borrowed mesh is destroyed. Each workspace adds a scene and
Unity scene-manager/resource activity; zero SceneManager side effects are not claimed.

Raw native saturation remains `MayBeTruncated` after geometry rejects everything.
Allowed unknown/budget/cooking failures clear output, preserve all observed raw
count, invalidate the snapshot and cannot commit detector contacts. Body grouping,
sorting and returned identities use original candidates AFTER the narrow phase.

## Continuous Detection

`OverlapDetector2D` / `OverlapDetector3D` query their owner's actual physics scene.
Set `Composite` to `new OverlapComposite2D(localShapes)` or
`new OverlapComposite3D(localShapes)`, using the corresponding shape factories
with **local** coordinates. A single child uses the same Union/diff path. Configurations
copy shape values and contain no Unity object references or runtime state; sharing
one configuration does not share buffers, contacts or observers. Empty Union is legal.
2D Sector children use the same round-shape uniform XY/no-shear conversion as
Circle/Capsule, including mirrored forward directions. Their outer/inner radii,
aperture and rotation are serialized separately. A Detector lazily creates its
**own** workspace only when its current Union contains a Sector; primitive-only
Unions never allocate a shape group. `ConfigureGeometryWorkspace(256, 4096,
0.00001f)` sets the finite limits/tolerance (also serialized in its Inspector).
Budget reconfiguration, Stop, disable, destroy and scene retirement dispose owned
storage; the next running Sector sample creates a fresh workspace. Invalid or
over-budget geometry preserves `LastComplete` and emits no false separation.
3D Sector/Cone local children require uniform, no-shear XYZ owner mapping. Uniform
mirrors retain transformed forward/up through an equivalent proper rotation using
the region's X-reflection symmetry; nonuniform owners are not elliptic cones/sectors.
This restriction is for the local QUERY configuration, not the supported target's
copied hierarchy. Existing Box nonuniform and capsule cap-center protocols remain.
3D detectors lazily own their runtime workspace and expose geometry diagnostics.
`ConfigureGeometryWorkspace(0.01f, 128, 128, 64)` configures its error/segment/part/
hierarchy budgets. Stop, disable, destroy, scene retirement and budget replacement
dispose it; pool restart creates a fresh owned scene. Current geometry counters
describe the last cached child, not a summed historical Union. An invalid child
retains the final attempt's accumulated raw count INCLUDING the failed child,
original reason, Count=0, previous baseline and no contact notifications.
`Filter` accepts the existing dimension-specific query filter. Grouping uses actual
attached bodies across all children; bodyless colliders remain independent. The first
child's representative is retained for each identity, not a global nearest collider.
Child order/native query order determines contact order, not cross-process ordering.

The default mode is `OverlapSamplingMode.Manual`, with `runOnEnable` enabled.
`Sample()` explicitly samples while running and enabled; `StartDetection()` restarts,
and `Stop()` stops both scheduling and explicit sampling until restarted. `SamplingMode`
also supports `Update` / `FixedUpdate`, at finite positive `Frequency` in scaled time,
at most once per engine callback. `ConfigureCapacity(initialCapacity, maxCapacity,
growBuffer: true)` opts into bounded growth and a full Union rerun. Default capacity
is 32, maximum 256, growth off. Raw saturation, even exact-full or subsequently
filtered/grouped output, never commits a diff. All children and the unique Union
must fit completely. `LastComplete` stays at the last complete baseline on truncation
or invalidation; `LastQuery` reports unique Count, summed final-attempt RawCount,
status and the original invalidation reason, including 2D `TriggerQueriesDisabled`.
Invalid serialized settings or unsupported transforms give `Invalidated` plus
`ConfigurationError` rather than entering native code; property arguments are validated.
`LastComplete` is a borrowed read-only view, not a historical copy. Reacquire it after
state changes. An in-progress callback enumeration remains safe across nested Stop.

`Entered` / `Stayed` carry `OverlapContact<Collider2D>` or `OverlapContact<Collider>`;
`Exited` also carries `OverlapExitReason`. Contacts contain Collider, Target (collider
or grouped body), cached Identity and ColliderInstanceId. A complete baseline is
committed **before** notifications: old departures first, new Enter next, existing
Stay last; a new contact does not also receive Stay in its Enter sample. Known
destroyed/disabled targets use `TargetDestroyed` / `TargetDisabled`; other complete
departures use `FilterChanged`, `Reconfigured` or `Separated`. Stop uses `Stopped`,
disable/pool return uses `Disabled`. Unity normally disables before owner destruction
and when moving between local physics scenes, so these paths do not invent a second
owner-destroyed Exit. A scene change without disable uses `SceneChanged`.
Callback Stop/disable/destroy/reconfiguration/nested Sample cancels remaining old-cycle
notifications, including remaining observers. Lifecycle clearing releases all committed
contacts, even when a prior notification was interrupted. Observer exceptions propagate
fail-fast; committed state remains consistent and dispatch flags/buffers are finalized.

Offsets and cap centers use the full local-to-world matrix, including mirrors.
Boxes accept nonuniform scaling only when transformed axes remain orthogonal;
round shapes require uniform XY (2D) / XYZ (3D) scale. 2D volume axes must remain
in the XY plane. Singular transforms, rotated nonuniform scale producing shear and
elliptical/ellipsoidal round shapes are explicitly invalid, not approximate OBBs or
conservative bounds. Orthogonality/equal-scale checks use relative float tolerance
1e-5. These configurations do not provide compound Cast, polygon overlap or booleans.

## Selected Authoring And Preview

Both Detector Inspectors edit local Union geometry, sampling mode/frequency,
initial/maximum capacity and bounded growth through `SerializedObject`, including
Undo and prefab instance overrides. Filter policy is now serialized on the detector:
native `ContactFilter2D` (layer, trigger, depth/normal policy), or explicit 3D
layer/Ignore-or-Collide, plus ignored collider/root, grouping and distance sorting.
The public `Filter` signatures are unchanged. Programmatic assignment immediately
updates that same backing state; a later sample does not restore an older Inspector
copy. Invalid serialized values remain visible and are rejected before native queries.
Ignored references belong to the component/scene or prefab instance, never to the
shared geometry configuration. Shared prefab asset Inspectors are read-only; use
Prefab Mode or an instance, and save only when intentionally requested.

Selected-only Gizmos use the runtime Composite world conversion, not approximate
transform math. The selected child center handle honors the full parent matrix;
capsule endpoint centers translate together. Unsupported shear/elliptical geometry
has an explicit diagnostic rather than a misleading wire shape. Multi-edit supports
serialized settings only; handles and preview require one selected instance.

Runtime diagnostics display actual buffer capacity, unique Count/summed RawCount,
status/reason, configuration error and separately labeled borrowed last-complete
contacts. Before the first sample the query struct still has its default value.
Partial or invalid output never relabels that baseline as a current complete result.
RawCount minus Count is not a rejected-collider count: it includes Union duplicates
and body grouping. The Inspector does not construct full contact snapshots.

Editor preview is independent of runtime detector sampling and is not persisted.
Choose a Union child, caller capacity and explicit **world** Cast direction/distance.
The trajectory is that child's Cast, never a compound Cast. Only the explicit sample
button or opt-in, rate-limited selected auto sampling performs physics queries.
Inspector creation, OnGUI and Gizmo repaint do not query. Preview uses two native
observations with the same native layer/trigger/depth policy, before/after ignore and
grouping; green/orange borrowed results do not purport to enumerate colliders excluded
by native filtering. Closing the Inspector releases these per-inspector references.
No global debug cache, Resource authority or update service is created. 2D global
trigger suppression remains a visible prerequisite; no tool toggles it for preview.
3D curved authoring persists inner/outer radius, aperture, thickness, cone apex/
length/base radius and orientation. Each dimension Slider reports its own change;
unedited dimensions preserve exact serialized local float values and overrides,
including thin annuli, sub-.001 apertures and 360-degree rings. Invalid edited
outer/inner conflicts are rejected instead of clamping unrelated fields. Selected
dimensions/center handles use the same
world conversion and SerializedObject Undo/prefab override path. Wire boundaries
are labeled ideal plus requested tessellation envelope, not exact cooked surfaces.
Curved preview buttons are disabled outside Play Mode (`RequiresPlayMode`), while
Gizmos/handles remain editable without allocating a physics scene. In Editor Play
Mode, explicit previews own a separate workspace released on unselection, Inspector
close and Play exit. Runtime sources do not reference UnityEditor.
For 2D Sector preview, the raw list is the native outer-circle candidate list,
and orange can also mean outside the aperture or inside its hole. A separate
per-Inspector workspace applies the same budgets/tolerance and is disposed when
the Inspector closes. Wire geometry uses real inner/outer arcs and finite radial
edges; a disk omits its inner arc, a full ring omits radial edges. Sector Cast
is visibly unsupported and no translated/swept sector wire shape is fabricated.

The source repository declares `Daily / Overlap` for original and new native physics
contracts, `Daily / OverlapEditor` for authoring, `Special / OverlapPlayer` for the
minimal main+overlap dual-process Mono Player and measured native workloads, and
`Daily / OverlapMovement` for the explicit main+movement2d+overlap coexistence case.
The Player measurement contract calibrates allocation counting with a real 64 KiB
allocation; a constant-zero Unity Mono counter is **unsupported**, with bytes `-1`,
not measured zero allocation. Current unique and repeated/grouped workloads are
different workloads, not an old-product speedup comparison. Complexity evidence for
the retired nested `Contains` path is separate from engine timing.

Legacy components now use the same raw native dispatch, retaining default physics
scene, raw Trigger counts before exclusion, native treatment of old zero/malformed
dimensions, fixed **per-call** 100 capacity, public mutable results, original GUIDs
and XY 3D hits. Area may still collect more than 100 unique colliders from several
individually complete calls. The intentional reliability correction is that incomplete
observations no longer emit Enter/Exit or replace the complete baseline, including
the first saturated sample after re-enable. Partial `results` remain visible and
`LastQueryStatus` exposes completeness; the next complete sample resumes normal
diffs. Complete legacy notifications retain Enter-before-Exit order, now after all
children have been collected. Public list edits are re-indexed at the next query
boundary, not assumed immutable. Legacy OnDisable still clears without Exit.

Update package installation and consumer asmdef references together. GUID
preservation does not rewrite assembly-qualified type names in external DLLs,
JSON, resource catalogs or historical bundles. Rebuild such resource content
through the real publishing flow where it records the old assembly tuple. Package
removal does not delete user components, scripts or project data that reference
the removed types; adjust those references explicitly.
