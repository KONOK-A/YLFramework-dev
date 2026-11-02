# YLFramework Excel Authoring

Editor-only package `com.kano.ylframework.excel`, version `0.1.0-preview.1`.
Declare the main package and this package explicitly when installing from Git.
UPM resolves the declared Newtonsoft.Json registry dependency.

Excel URL format (`<full-commit-sha>` is notation, not a usable revision):

```text
https://github.com/KONOK-A/YLFramework-dev.git?path=/PackageSources/com.kano.ylframework.excel#<full-commit-sha>
```

Use the actual full SHA and main-plus-Excel manifest entries supplied by the
acceptance handoff. Package-level SemVer dependencies do not fetch sibling Git
packages. See the [installation guide](../com.kano.ylframework/Documentation~/README.md#git-url-与依赖闭包).

MiniExcel 1.31.2 is bundled in `Editor/Plugins/MiniExcel` under Apache-2.0;
retain its LICENSE and ATTRIBUTION. It is not a developer download cache and is
not included in Player builds. No NuGet downloader runs on package installation.
The [license](Editor/Licenses/MiniExcel-LICENSE.txt),
[attribution](Editor/Licenses/MiniExcel-ATTRIBUTION.txt) and
[dependency provenance](Editor/Dependencies.json) remain with this package.

The assembly remains `YLFramework.Excel.Editor`. Runtime field attributes remain
part of `YLFramework.Runtime`. Generated configurations belong to the consuming
project, never to this installed package.
Package upgrades and removal do not overwrite/delete existing assets, workbook
files or workbook selections. Maintained by Kano; original copyrights are retained.
