# YLFramework Localization

Optional explicit localization roots, deterministic language fallback, UGUI and
TextMesh/string-property bindings, resource-owned images and Unity authoring.
Install this package and `com.kano.ylframework` at the same validated revision.
Git consumers explicitly declare both repository subpaths; this package does not
depend on DataAssets, Excel, Save, TMP or an external localization SDK.

The public namespaces remain `YLFramework.Localization` and
`YLFramework.Editor.Localization`. Their owners are now `YLFramework.Localization`
and `YLFramework.Localization.Editor`. Original script GUIDs, class names,
serialized fields, enum values and language preference keys are preserved. Add
an explicit asmdef reference when your own code uses assembly definitions.

Register already loaded roots with `LocalizationManager.AddConfig`, or use
`LoadConfigAsync` with the caller's `ResourceSystem`. Dispose the returned handle
when the consumer ends. The configuration owner, text subscription, and image
request/lease remain separate responsibilities. Stop, disable, destroy and scene
changes must return each consumer's own responsibility, not another owner's.
Use these APIs on Unity's main thread; they do not introduce a background loader.

Fallback remains requested language, applicable Chinese variants, English, then
explicit registration order. `LocalizeString.Other` still supports a public
string field/property (including a consumer's preserved TMP binding) without a
TMP compile dependency. It is not an arbitrary component factory.

`LocalizeImage` still requires the main package's `ResourceImage`. The latter
owns all image requests, leases, generation/cancellation checks and original
display restoration. Localization does not copy it or create another image
service. A late image result cannot republish after the owner is disabled.

The authoring window edits explicitly selected roots and creates languages under
`Assets/YLFrameworkData/Localization/<root-guid>`. Removing a language reference
does not delete its asset. Save is explicit and limited to the target root and
language assets, with Undo retained. Removing/upgrading the installed package
does not delete the consumer's configurations or preferences.

Resource catalogs persist `(FullName, AssemblyName)`. Existing Localization
catalogs built while these types were in `YLFramework.Runtime` must be rebuilt
together with their bundles using the formal Resource builder/publisher. Do not
rewrite the old catalog JSON/digest or use reflective alias fallback. There is
no fabricated managed-reference migration or legacy wrapper in this package.
