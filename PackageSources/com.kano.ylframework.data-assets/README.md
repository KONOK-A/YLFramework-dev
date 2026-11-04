# YLFramework DataAssets

Optional ScriptableObject configuration roots, identity indices, explicit resource
ownership and Unity authoring tools. The package depends on the YLFramework main
package, but neither the main Runtime nor Editor depends on DataAssets.

Install `com.kano.ylframework` and this package from the same version. A Git
consumer must add both repository subpaths explicitly, because a package.json
registry dependency cannot discover an unpublished sibling Git package:

```text
https://github.com/KONOK-A/YLFramework-dev.git?path=PackageSources/com.kano.ylframework
https://github.com/KONOK-A/YLFramework-dev.git?path=PackageSources/com.kano.ylframework.data-assets
```

`YLFramework.DataAssets` owns the unchanged `YLFramework.DataAssets` namespace;
`YLFramework.DataAssets.Editor` owns the unchanged authoring namespace. Original
script GUIDs and serialized field aliases are retained. Runtime consumers must
declare their own asmdef reference when using assembly definitions.

`ScriptableObjectManager.Load` returns a registration that owns its root resource
lease. `LoadAsync` returns a disposable request/registration owner; dispose it on
consumer cancellation, disable or destruction. `Clear` closes DataAssets owners,
not the ResourceSystem. Multiple owners of one root retain independent leases.
Use these APIs on Unity's main thread and keep shared definition assets immutable
at runtime. Configuration asset creation and import remain explicit authoring
operations; installed package files are never used as project output.

Root lookup uses the asset object's name/hash, not its `displayName`; name/hash
collisions are rejected. The authoring window is `Window/YLFramework/Data Assets`.
It edits explicitly selected roots and creates project-owned rows under
`Assets/YLFrameworkData`, never inside the installed package.

The persistent field protocols remain `displayName/configs/typeScriptGuid` on
configuration roots and `parent/id/configName/icon/description` on rows. Existing
Unity aliases and explicit Excel column names are preserved, rather than adding
an Excel file path to runtime data.

Excel import/export is an optional consumer in `com.kano.ylframework.excel`.
DataAssets does not reference Excel or MiniExcel. During the package migration,
recent-workbook memory remains in this package's Editor assembly and retains its
asset-GUID-based EditorPrefs keys; it is not serialized into runtime roots.

Existing Resource catalogs persist the asset assembly name. After migrating a
DataAssets project from `YLFramework.Runtime`, rebuild content and its catalog
with the formal Resource builder/publisher; do not edit the old JSON or reuse its
catalog digest. An old assembly tuple is intentionally rejected by the new type
table. Removing this package does not delete project assets or workbooks.
