# YLFramework Movement 2D

Install this package with the main package and Unity's physics2d module.
Git consumers explicitly declare main and this package at the same accepted full
SHA; package-level SemVer dependencies do not fetch sibling Git packages.
Character movement belongs to `YLFramework.Character2D`; moving paths, platforms
and passenger detection belong to `YLFramework.Platform2D`. Their namespaces,
serialized fields, script GUIDs and UnityEvent types are unchanged.

The two Runtime assemblies each reference `YLFramework.Runtime` for existing
extensions and do not reference each other. Consumer asmdefs declare only the
runtime they actually use. Authoring for both belongs to
`YLFramework.Movement2D.Editor`, which references the two runtimes and the main
Editor common tools. Overlap and both FSM systems are not included in this package.

This is an ownership change, not a new movement algorithm. Contact checks,
queued movement, jumps, path modes, pause/stop, passenger snapshots and pooled
reuse retain their existing update timing. In particular, a moving platform
applies its world displacement to detected passenger rigidbodies; this does not
promise an additional character-style obstacle cast for the passenger.

Existing component Prefabs and persistent UnityEvent method bindings retain
their identities. Removing the package does not delete user Prefabs or scenes;
consumers must remove their component/API references before uninstalling it.
Reference guides remain in the main documentation's Behavior module.
