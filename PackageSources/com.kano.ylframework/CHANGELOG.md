# Changelog

## 0.1.0-preview.1 (Unreleased)

- Introduce the main UPM package and four independently installed integrations.
- Preserve runtime assembly names, namespaces, asset GUIDs and Resources keys.
- Separate project outputs, developer tooling and tests from product packages.
- Use explicit sample imports and UPM installation acceptance instead of unitypackage delivery.
- Declare sibling Git package closures explicitly; let UPM resolve registry dependencies.
- Write Module code and ScriptableObject helper output under project-owned `Assets/YLFrameworkData` roots.
- Enable Domain Excel on package installation without requiring a global `YL_DOMAIN_EXCEL` define.
- Define five-package OpenUPM release assets with explicit publish whitelists and frozen payload receipts.
- Keep Registry single-entry dependency resolution separate from explicit Git closures and LocalTarball preflight.

This entry describes the preview package changes, not a completed installation,
runtime acceptance or public release. The acceptance handoff supplies the actual
validated full commit SHA and usable manifest entries separately from the source
commit that defines this version.
