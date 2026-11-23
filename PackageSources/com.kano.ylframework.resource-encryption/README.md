# YLFramework Encrypted Resource Delivery

`com.kano.ylframework.resource-encryption` is an optional UPM package for an explicitly enabled remote Addressables group. The YLFramework main package does not depend on it. Ordinary projects can install only the main package and keep their existing resource workflow without the encryption plugin.

## Install and enable

1. Install the matching `com.kano.ylframework` main package first. In Package Manager, add this package's `package.json` from disk, or install both `.tgz` files from the same build, main package first.
2. To enable encryption, configure one remote Addressables group and build its matching Player and content with `EncryptedBundleExporter`. Start `EncryptedDeliverySession` explicitly before the normal resource startup. Installation alone does not enable encryption, publish content, or supply keys.

See `Documentation~/Modules/Resource/Delivery.md` in the installed main package for group configuration, key ownership, Player startup and shutdown, and delivery requirements. Application assemblies that call the runtime API reference `YLFramework.Resource.Encryption`; build code in an Editor assembly references `YLFramework.Resource.Encryption.Editor`.

## Disable or remove

With the extension still installed, remove the encryption schema and restore the official Bundle Provider, normal build/load paths, and normal build and startup entry points. Remove application and Editor assembly references before uninstalling. Rebuild a matching ordinary Player and content in new destinations; do not serve plaintext at an existing encrypted release URL.

## Verified scope

Local Player validation covers Windows x64 Release on Unity `6000.3.27f1` (Addressables `2.10.3`, binary Catalog) and `2022.3.62f3` (Addressables `1.25.0`, JSON Catalog), with Mono / Disabled or IL2CPP / Medium. Both backends use the same v1 format. Old CNG content is not readable by this package. Rebuild the Player and its matching content when enabling, disabling or removing encryption. Large cached Bundles still require authentication before mounting; measure the actual project's load budget before enabling it.

The native bridge links Tink, BoringSSL, and Abseil. Their license texts and the frozen backend identity are under `Documentation~`. A replacement binary, even with the same name, is not an approved artifact.
