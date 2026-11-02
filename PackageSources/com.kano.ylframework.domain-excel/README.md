# YLFramework Domain Excel

Editor-only package `com.kano.ylframework.domain-excel`, version
`0.1.0-preview.1`. It reuses `com.kano.ylframework.excel`; no second MiniExcel
backend is copied here. Git consumers must explicitly declare the main, Excel
and Domain Excel packages at the same validated revision.

Domain Excel URL format (`<full-commit-sha>` is notation, not a usable revision):

```text
https://github.com/KONOK-A/YLFramework-dev.git?path=/PackageSources/com.kano.ylframework.domain-excel#<full-commit-sha>
```

Use the actual full SHA and the three-package manifest closure supplied by the
acceptance handoff. Package-level SemVer dependencies do not fetch sibling Git
packages. See the [installation guide](../com.kano.ylframework/Documentation~/README.md#git-url-与依赖闭包).

Installing this package enables `YLFramework.Domain.Excel.Editor`; the old
global `YL_DOMAIN_EXCEL` define is no longer required. Existing consumer-side
conditional compilation using that symbol is project-owned and must be reviewed
when migrating. This package never changes global scripting symbols.

Workbook selections and generated domain assets remain in the consuming
project. Uninstalling this adapter does not remove those assets or workbooks.
The MiniExcel binary and Apache-2.0 notices remain in the separate
[Excel package](../com.kano.ylframework.excel/README.md); see this package's
[license](LICENSE.md) and [dependency provenance](Editor/Dependencies.json).
Maintained by Kano; original copyrights are retained.
