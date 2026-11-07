# YLFramework

UPM package `com.kano.ylframework`, version `0.1.0-preview.1`, maintained by Kano.
The original KONOK-A copyright and source attributions remain in `LICENSE.md`.

This directory contains the framework product, not its Unity development host.
It includes Runtime and Editor assemblies, production prefabs, BouncyCastle with
its license, documentation and opt-in samples. Tests, MCP, development settings
and optional integration SDKs are not included.

Overlap is an optional sibling package, `com.kano.ylframework.overlap`, not part
of the main Runtime or Editor. Its minimal YL closure is main + overlap; Unity's
built-in physics/physics2d modules are declared by the package. Consumer asmdefs
reference `YLFramework.Overlap`, and direct Inspector/Gizmos consumers additionally
reference `YLFramework.Overlap.Editor` from Editor-only code. Original namespaces,
script GUIDs and serialized fields are preserved. Legacy Enter/Exit now use complete
observations and run after collection; see the
[Overlap package](../com.kano.ylframework.overlap/README.md) for the intentional
complete-only notification correction and migration boundaries.

## Registry Installation

The intended release channel is OpenUPM. This preview is still unreleased until
all eleven package versions, frozen Release assets and their acceptance receipt are
available; the configuration below is not a claim that publication has occurred.
After that handoff, use this minimal `Packages/manifest.json` for the main package:

```json
{
  "scopedRegistries": [
    { "name": "OpenUPM", "url": "https://package.openupm.com", "scopes": ["com.kano.ylframework"] }
  ],
  "dependencies": { "com.kano.ylframework": "0.1.0-preview.1" }
}
```

For one optional package, declare its package ID at the same version and let UPM
resolve its actual manifest dependencies. Save requires Json.NET but not the main
package. Excel requires main and DataAssets; its built-in Gameplay adapter also
requires an explicit Gameplay entry. RedDots core does not require Save; install
Save separately to enable `YLFramework.RedDots.Save`. Do not copy development-host
dependencies or route `com.unity` through OpenUPM.
HybridCLR additionally needs the narrow `com.code-philosophy.hybridclr` scope so
UPM can resolve the separately distributed official SDK `9.0.3`; DOTween still
requires a separately installed approved SDK and its `DOTWEEN` define.

## Git Fallback

The fallback Git URL format for installation through Package Manager is:

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
closure table. The Registry single-entry rule must not be applied to Git URLs.
DOTween and HybridCLR SDKs are distributed separately from the YL packages.

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
