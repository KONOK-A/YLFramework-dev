# YLFramework Excel Gameplay Adapter

This adapter is included in `com.kano.ylframework.excel`, version
`0.1.0-preview.1`. Installing `com.kano.ylframework.gameplay` alongside Excel
enables the original `YLFramework.Domain.Excel.Editor` assembly through its
assembly-local `YL_GAMEPLAY_PACKAGE` Version Define. No global scripting
symbol is required or changed. The unpublished standalone
`com.kano.ylframework.domain-excel` package identity has been retired.

For Git installation, explicitly declare main, DataAssets, Excel and Gameplay
at the same accepted full SHA. Package-level SemVer declarations cannot fetch
unpublished sibling Git packages. Excel URL format (`<full-commit-sha>` is notation):

```text
https://github.com/KONOK-A/YLFramework-dev.git?path=/PackageSources/com.kano.ylframework.excel#<full-commit-sha>
```

For the intended Registry channel, Excel and Gameplay are two direct entries;
UPM resolves their main/DataAssets dependencies. Use Registry only after the
frozen release handoff confirms availability. This refactor does not publish
any package. See the [installation guide](../../../com.kano.ylframework/Documentation~/README.md#git-url-与依赖闭包).

The original `DomainExcelCommands` namespace, public overloads, context menus
and script GUID remain. Generic Excel has no Gameplay product dependency;
Gameplay without Excel has no MiniExcel backend or adapter assembly.
Workbook selections and generated assets belong to the consuming project and
are not deleted when either package is removed.

MiniExcel exists only once in the [Excel Editor package](../../README.md).
The inherited [license](LICENSE.md), [backend provenance](Dependencies.json)
and Apache-2.0 [license](../Licenses/MiniExcel-LICENSE.txt) and
[attribution](../Licenses/MiniExcel-ATTRIBUTION.txt) remain with their owners.
Maintained by Kano; original copyrights are retained.
