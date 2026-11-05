# YLFramework Gameplay

Install this package with its main-package and Newtonsoft dependencies. Skill,
Buff, Mission and Equipment remain independent Runtime assemblies:
`YLFramework.Skill`, `YLFramework.Buff`, `YLFramework.Mission` and
`YLFramework.Equipment`. Each references only the main Runtime; Buff and Mission
also explicitly reference Newtonsoft.Json. Consumer asmdefs declare the domains
they actually use, not a shared Runtime umbrella.

Authoring for all four domains and its internal shared helpers belongs to
`YLFramework.Gameplay.Editor`. It includes the existing common session cleanup
and the separate Equipment lifecycle hook. Namespaces, script GUIDs, asset
fields, JSON keys, Excel column mappings and manager behavior are unchanged.
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
