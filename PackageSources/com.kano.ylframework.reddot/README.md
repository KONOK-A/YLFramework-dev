# YLFramework Red Dots

The existing `YLFramework.RedDots` namespace now belongs to the independent
`YLFramework.RedDots` Runtime assembly. Install this package with its main-package
dependency and add `YLFramework.RedDots` to consumer asmdef references. The core
does not require Save, Gameplay, Excel or a font/UI package.

`ReddotManager` retains its original normalized, case-sensitive whole-segment
paths. `AddPath` is an idempotent set, while `RegisterPath` returns an independently
owned disposable registration. Reset invalidates old registrations and preserves
subscriptions; a new play session clears both sources and subscriptions. The
legacy `RegisterChanged` does not emit an initial state. Observer failures remain
visible and a failed registration rolls its own source back.

The original `Reddot` component and script GUIDs are preserved. It changes only
the explicitly assigned indicator, never its own host or an ancestor. Existing
serialized `path` / `indicator` fields and their former names remain unchanged.

The main package no longer contains these types. Update consumer assemblies and
package installation together; preserving a script GUID does not preserve the old
assembly-qualified resource tuple. Rebuild resource catalogs through the actual
publishing flow where they record that tuple. Removing this package does not
remove user components or scripts; adjust those consumer references explicitly.
