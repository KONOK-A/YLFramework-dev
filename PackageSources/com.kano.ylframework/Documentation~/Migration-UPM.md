# Migrating to UPM

The first UPM version is `0.1.0-preview.1`. It is not a stable release of the old
unitypackage version line. Back up or commit the consuming project before
migration, including custom code and authoring data.
The old framework and integration unitypackage exporters are retired; UPM is
the only framework installation route. Resource snapshots and HybridCLR base
Players remain separate application deliverables.

1. Inventory the old `Assets/YLFramework` directory. Separate project-owned
   code, settings, generated assemblies and unknown files from framework inputs.
2. Remove only the old framework-owned copy. Do not let its asmdef or scripts
   compile alongside the UPM package. Do not delete an unknown directory merely
   because it has the old framework name.
3. After the frozen Release and Registry acceptance handoff confirm version
   availability, prefer the [Registry single-entry configuration](README.md#registry-与自动依赖).
   Only the selected leaf is direct; Domain Excel resolves the main and Excel
   packages, and HybridCLR also resolves the separately distributed official
   SDK `9.0.3` through its additional narrow scope. Do not copy development-host
   dependencies. For the [Git fallback](README.md#git-url-与依赖闭包), obtain the
   actual full SHA and explicit manifest closure from the acceptance handoff.
   All sibling Git packages and the pinned HybridCLR SDK need direct entries.
   No placeholder SHA, unverified tag or LocalTarball preflight is evidence of
   public Registry readiness. DOTween remains a separately installed SDK for
   either route; SDK/native and platform gates are unchanged.
4. If existing scenes or code reference Resource Startup or HybridCLR samples,
   explicitly import the matching sample before restoring those references.
   Do not save a scene with missing scripts during the transition.
5. Check actual script/prefab GUIDs, serialized values, Resources loading,
   generated assembly identity and the consuming project's compilation.

Do not recreate existing generated code in a second directory with the same
asmdef name. `Assets/YLFrameworkData`, workbooks, StreamingAssets output and
HybridCLR baselines remain project-owned. No package install, update or removal
automatically migrates or deletes them.
Do not replace vendor SDKs, local native trees or frozen baselines as part of a
package upgrade. A changed SDK/native, AOT API or stripping input may require a
new explicitly built and validated base Player, never an in-place rewrite of
an existing baseline.

Domain Excel now enables its Editor assembly on installation and no longer
requires the global `YL_DOMAIN_EXCEL` define. Review project-owned `#if` code
that used it; the package does not edit global scripting symbols for you.

Package-local templates, style sheets and documentation are located by the
installed package identity. External scripts hardcoding `Assets/YLFramework`
must be updated explicitly; serialized field rename attributes do not migrate
filesystem paths. Documentation is in `Documentation~` and must be opened by
its actual file path, not by AssetDatabase lookup.

Module generation now writes to `Assets/YLFrameworkData/Generated/Modules` and
ScriptableObject helpers to `Assets/YLFrameworkData/ScriptableObjects`. New Resource
reference generation defaults to the separate leaf directory
`Assets/YLFrameworkData/Generated/ResourceReferences`. Explicit output paths and
saved authoring settings keep their original meaning; they are not redirected.
The reference directory remains exclusive to `YLResourceReferences.cs`,
`YLFramework.Generated.ResourceNames.asmdef` and their `.meta` files. Unknown
files, edited generated content and conflicting assembly definitions still fail
before writing. It must not contain Module or ScriptableObject output.

An existing `YLFramework.Generated.ResourceNames` assembly, including one at the
legacy `Assets/YLFrameworkData` root, blocks generation at a new location. The
error identifies the original asmdef. Nothing is automatically moved, replaced
or adopted, and a second asmdef with the same assembly name is never created.
An explicit, valid legacy directory can still be regenerated, but adding other
generators' output beneath that exclusive directory makes it invalid.

To migrate that legacy output explicitly:

1. Back up the project and locate the asmdef named
   `YLFramework.Generated.ResourceNames`, its generated source and their `.meta`
   files. Confirm that they are the intended unmodified generator output; do not
   delete or merge unknown project data to make the directory pass validation.
2. Before running Module or ScriptableObject helpers beneath the old root, move
   the existing source and asmdef together with their original `.meta` files to
   the unused `Assets/YLFrameworkData/Generated/ResourceReferences` leaf. Use the
   Unity Project view, or close the Editor for a filesystem move that preserves
   both `.meta` files. Move, do not copy; keep exactly one assembly definition.
   If another ancestor asmdef/asmref still applies, choose a separate valid leaf
   outside that boundary and configure that explicit path instead.
3. Keep the assembly name `YLFramework.Generated.ResourceNames`, source identity
   and existing asmdef/GUID references unchanged. Update explicit generator
   calls and saved reference-directory settings, then import and verify actual
   Unity compilation and consumers before regenerating. The old data root and
   its own `.meta` remain project-owned and are not removed by this migration.

Module names retain namespace and assembly identity
`YLFramework.Core.Examples.<ModuleName>`. A matching imported asmdef at the old
`Assets/YLFramework/Examples/Core/Modules` path or any relocated path, or a matching
loaded assembly/type, rejects generation before creating the new output. The
diagnostic identifies the existing asmdef or loaded assembly location. Reuse or
explicitly migrate the original module and its `.meta` files; changing the
output root is not permission to generate a duplicate identity.

Production prefabs are shipped assets, not regenerated on Editor startup. Product-asset
modification commands belong only to the framework development repository.
The package's `UIPanelAssetMigration.ValidateAssets()` is read-only. Old
`MigrateAssets()` / test-fixture mutation commands are not consumer upgrade
steps. Keep customized Prefabs in project-owned assets and migrate their real
references explicitly rather than editing an installed package.

Sample imports create project-owned copies. Before importing another version,
check for modified or duplicate sample assemblies and migrate explicitly; do
not leave multiple copies with identical assembly names.
