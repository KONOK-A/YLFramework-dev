# YLFramework

UPM package `com.kano.ylframework`, version `0.1.0-preview.1`, maintained by Kano.
The original KONOK-A copyright and source attributions remain in `LICENSE.md`.

This directory contains the framework product, not its Unity development host.
It includes Runtime and Editor assemblies, production prefabs, BouncyCastle with
its license, documentation and opt-in samples. Tests, MCP, development settings
and optional integration SDKs are not included.

The Git URL format for installation through Package Manager is:

```text
https://github.com/KONOK-A/YLFramework-dev.git?path=/PackageSources/com.kano.ylframework#<full-commit-sha>
```

`<full-commit-sha>` is syntax notation, not an installable revision. The acceptance
handoff must supply the actual full 40-character SHA and ready-to-use project
manifest entries for the selected package closure. This README does not claim
that such a revision is already published or validated. Keeping the SHA in the
handoff rather than this same commit avoids a self-referencing commit hash.

UGUI and Newtonsoft.Json registry dependencies are resolved by UPM from the
package manifest. Git-based YL packages must each be explicitly listed in the
consuming project's `Packages/manifest.json`; package-level SemVer dependencies
do not fetch their sibling Git packages. See the installation guide for the
closure table. DOTween and HybridCLR SDKs are installed separately.

See [installation and navigation](Documentation~/README.md) and the
[UPM migration guide](Documentation~/Migration-UPM.md). Import Resource Startup
from this package's Samples section only when needed. Required Canvas,
EventSystem and ScreenTransition Resources are production assets, not samples.
`Samples~` does not compile until imported through Package Manager. The former
unitypackage export path is retired.

Installed package files are read-only inputs. Project-owned authoring data and
generated code belong under `Assets/YLFrameworkData`: Module code defaults to
`Generated/Modules`, and ScriptableObject helpers to `ScriptableObjects`.
Package upgrades and uninstalls do not migrate, overwrite or delete those outputs,
workbooks, SDKs, native runtimes, Resource releases or HybridCLR baselines.
Open hidden `Documentation~` files by their actual filesystem path, not through
AssetDatabase lookup. The source repository remains a development/validation
host, not a game project.
