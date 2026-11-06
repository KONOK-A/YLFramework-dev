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
3D hit points remain the existing XY projection; this split adds no 3D features.

Runtime sources live under `Runtime/Utilities/Overlap`; Inspector/Gizmos sources
live under `Editor/Utilities/Overlap`. Runtime namespaces remain `YLFramework.Overlap`
and Editor namespaces remain `YLFramework.Editor.Overlap`. This is an equivalent
package move, not the later query/Cast, continuous detector or composite-area API.

Update package installation and consumer asmdef references together. GUID
preservation does not rewrite assembly-qualified type names in external DLLs,
JSON, resource catalogs or historical bundles. Rebuild such resource content
through the real publishing flow where it records the old assembly tuple. Package
removal does not delete user components, scripts or project data that reference
the removed types; adjust those references explicitly.
