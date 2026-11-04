# YLFramework Excel Authoring

Editor-only package `com.kano.ylframework.excel`, version `0.1.0-preview.1`.
Declare the main package, `com.kano.ylframework.data-assets`, and this package
explicitly at the same validated revision when installing from Git. DataAssets
owns the configuration roots and their Editor API without depending on Excel.
UPM resolves the declared Newtonsoft.Json registry dependency.

For the intended OpenUPM release channel, declare only
`"com.kano.ylframework.excel": "0.1.0-preview.1"` under the narrow
`com.kano.ylframework` scope at `https://package.openupm.com`. UPM resolves the
main and DataAssets packages automatically. Use the [Registry installation configuration](../com.kano.ylframework/README.md#registry-installation)
only after the frozen Release and acceptance handoff confirm version availability;
this preview is not claimed published by this README. The explicit main-plus-DataAssets-plus-Excel
closure below applies to the Git fallback, not Registry installation.

Excel URL format (`<full-commit-sha>` is notation, not a usable revision):

```text
https://github.com/KONOK-A/YLFramework-dev.git?path=/PackageSources/com.kano.ylframework.excel#<full-commit-sha>
```

Use the actual full SHA and main-plus-DataAssets-plus-Excel manifest entries supplied by the
acceptance handoff. Package-level SemVer dependencies do not fetch sibling Git
packages. See the [installation guide](../com.kano.ylframework/Documentation~/README.md#git-url-与依赖闭包).

MiniExcel 1.31.2 is bundled in `Editor/Plugins/MiniExcel.dll` under Apache-2.0;
retain its LICENSE and ATTRIBUTION. It is not a developer download cache and is
not included in Player builds. No NuGet downloader runs on package installation.
The [license](Editor/Licenses/MiniExcel-LICENSE.txt),
[attribution](Editor/Licenses/MiniExcel-ATTRIBUTION.txt) and
[dependency provenance](Editor/Dependencies.json) remain with this package.

The assembly remains `YLFramework.Excel.Editor`. Runtime field attributes remain
part of `YLFramework.Runtime`. Generated configurations belong to the consuming
project, never to this installed package.

Use the lightweight `YLFramework.Excel.ExcelColumnNameAttribute` from the main
package when a CLR field must keep an existing workbook column across renames
or assembly moves:

```csharp
[ExcelColumnName("existing_column")]
public string renamedField;
```

The backend uses this exact name or, when unmarked, `field.Name`. Unity's
`FormerlySerializedAs` remains a Unity serialization contract, not an implicit
Excel alias. Column names are not inferred from namespaces or assembly names.

Package upgrades and removal do not overwrite/delete existing assets, workbook
files or workbook selections. Maintained by Kano; original copyrights are retained.
