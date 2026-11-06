# YLFramework State Machine

Configured hierarchical state machines, parameters, conditions, layers and graph
authoring. The existing `YLFramework.StateMachine` runtime namespace belongs to
the `YLFramework.StateMachine` assembly. Authoring remains in the existing
`YLFramework.Editor.StateMachine` namespace, in `YLFramework.StateMachine.Editor`.
The independent lightweight `YLFramework.FSM` stays in the main package.

Install this package with its main-package dependency. Runtime consumers declare
`YLFramework.StateMachine` in their asmdef; Editor consumers also declare
`YLFramework.StateMachine.Editor` where they use authoring APIs. Foundation type
resolution and play-state checks come from the main package, not copied helpers.
Git consumers explicitly declare main and this package at the same accepted full
SHA; package-level SemVer dependencies do not fetch sibling Git packages.

The graph stylesheet and state script template retain their original GUIDs and
package-relative paths. Create And Add creates a new script without replacing an
occupied file, waits for actual import/compilation and binds its GUID/class name
to the selected configuration node. A generated state under a consumer asmdef
requires an explicit `YLFramework.StateMachine` reference. Assembly-CSharp uses
the runtime assembly's normal auto-reference. The generator does not edit a
consumer's asmdef or silently overwrite unknown generated output.

Existing configuration assets and components retain their script GUIDs, fields,
state scripts, parameters and sub-state connections. Existing state creation,
transition, trigger, reset and disable/destroy behavior is unchanged. Resource
catalogs that record the old owning assembly must be rebuilt through the actual
resource publishing flow; preserving a script GUID is not permission to accept
an old assembly tuple or a Bundle built by a different Unity version.

Removing this package does not delete user configurations or generated scripts.
Consumers must remove their own component/API references before uninstalling it.
