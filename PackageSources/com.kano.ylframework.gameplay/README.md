# YLFramework Gameplay

Install this package with its main-package and Newtonsoft dependencies. Skill,
Buff, Mission and Equipment remain independent Runtime assemblies:
`YLFramework.Skill`, `YLFramework.Buff`, `YLFramework.Mission` and
`YLFramework.Equipment`. Each references only the main Runtime; Buff and Mission
also explicitly reference Newtonsoft.Json. Consumer asmdefs declare the domains
they actually use, not a shared Runtime umbrella.
Git consumers explicitly declare main and this package at the same accepted full
SHA; package-level SemVer dependencies do not fetch sibling Git packages.

Authoring for all four domains and its internal shared helpers belongs to
`YLFramework.Gameplay.Editor`. It includes the existing common session cleanup
and the separate Equipment lifecycle hook. Namespaces, script GUIDs, asset
current fields, JSON keys, Excel column mappings and manager behavior retain
their identities. Retired Unity field-name aliases are not a second reader;
save owned old-field inputs through Unity while their original definitions can
read them before upgrading. Do not rewrite frozen bundles or guess a new type
or project identity from old strings.
Gameplay does not depend on Excel, DataAssets, Localization or Save. Excel
contains a conditional domain adapter enabled only when Gameplay is installed;
there is no separate DomainExcel package or reverse product dependency.

Registering direct configuration assets does not transfer their ownership.
Manager LoadConfig calls hold Resource leases until RemoveConfig or Shutdown;
runtime skill/buff/mission/equipment instances do not mutate source definitions.
Keep the existing close/disable/destroy and reentrancy rules in each domain's
main documentation. This package does not grant user state persistence or
change rewards, stack rules, cooldowns or equipment callback ordering.

Resource catalogs record the real asset assembly. Rebuild and publish catalogs
and Bundles through the normal Resource authoring tools after this ownership
change; retaining a script GUID does not make an old `YLFramework.Runtime`
asset tuple valid or guarantee cross-Unity Bundle compatibility.

User configurations, Buff settings and generated scripts keep their existing
project locations. Uninstalling this package does not delete them. Consumers
must remove their own type/component references before uninstalling it.
