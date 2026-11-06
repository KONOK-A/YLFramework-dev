# Changelog

## 0.1.0-preview.1 (Unreleased)

- Introduce the main UPM package and ten optional feature packages, with explicit dependency closures.
- Preserve namespaces, asset GUIDs, serialized field identities and Resources keys; document moved assembly owners and rebuild resource catalogs where required.
- Separate project outputs, developer tooling and tests from product packages.
- Use explicit sample imports and UPM installation acceptance instead of unitypackage delivery.
- Declare sibling Git package closures explicitly; let UPM resolve registry dependencies.
- Write Module code and ScriptableObject helper output under project-owned `Assets/YLFrameworkData` roots.
- Include Domain Excel inside Excel, enabled by Gameplay installation without requiring a global define; enable the optional RedDots Save assembly only when Save is installed.
- Define the complete eleven-package OpenUPM candidate assets with explicit publish whitelists and frozen payload receipts, without rewriting historical candidates.
- Keep Registry single-entry dependency resolution separate from explicit Git closures and LocalTarball preflight.

This entry describes the preview package changes, not a completed installation,
runtime acceptance or public release. The acceptance handoff supplies the actual
validated full commit SHA and usable manifest entries separately from the source
commit that defines this version.
