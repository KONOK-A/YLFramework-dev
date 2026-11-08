# YLFramework Save

Standalone slot metadata, save objects/models/settings, atomic single-file JSON
writes and explicit backup recovery. This package references Newtonsoft.Json
directly and does not require `com.kano.ylframework` or another optional package.
`YLFramework.Save` owns the unchanged `YLFramework.Save` namespace and the
Save-specific vector/color serialization types. General SerializedDictionary
contracts remain in the main package and are not duplicated here.

Install this package and let UPM resolve `com.unity.nuget.newtonsoft-json`.
Declare `YLFramework.Save` in a consumer asmdef. The package does not bundle a
second Newtonsoft DLL or rely on another package's precompiled-reference list.
Consumers using `overrideReferences` also explicitly list `Newtonsoft.Json.dll`
when calling APIs whose signatures expose its settings, even for optional args.

`SaveStore` is the storage owner. Dispose it before another owner opens the same
root. `SaveStore` is the sole business API. `SaveManager.Store` publicly borrows
the default owner; callers must not dispose that borrowed instance. The manager
only captures the Unity main-thread path, exposes default configuration and
handles root/session lifetime. Set an explicit root before worker use.
Changing the default root closes/cancels/drains its previous owner; it does not
copy or migrate files. Async writes serialize snapshots on the calling thread
before asynchronous IO; custom converters must not call Unity APIs on workers.

Slots, backups and serialized JSON field names are unchanged. A malformed primary
can be read from a valid backup without automatic repair. Recovery must be
explicit before another write; corruption without a valid backup and unsupported
index versions remain errors. No multi-file transaction, cloud service, automatic
old-format migration or new encryption layer is added by this ownership move.

Slot-requiring synchronous and asynchronous SaveStore reads/writes consistently
throw for an unknown slot. No logging-and-return facade remains. Use explicit
file keys and slot IDs, not inferred type names or SaveItem overloads. Queries and deletes keep their false/empty or
idempotent outcomes. They do not silently create a slot.

SaveItem is now a top-level type in the same namespace. Its instances are borrowed mutable metadata. Direct mutation changes only
the current in-memory object, not the disk immediately; cold reopen before any
index write discards it. A later index write snapshots current in-memory
metadata, including such mutations. Explicit rename/note APIs persist at once.
An already open owner does not merge external edits to the index. These existing
boundaries are regression-tested rather than normalized into a new format.

DefaultSerializerSettings is the shared configuration instance, set before
concurrent use. DefaultSerializationFormat applies only when the default Store
is created; Store.SerializationFormat controls the current instance. Getter,
Close and root changes do not overwrite instance formatting or reset defaults.

The included link.xml preserves SaveIndex and the top-level SaveItem metadata;
consumers still preserve their own AOT DTOs/converters. Removing/upgrading this
package never deletes project save data or resolves recovery on the user's behalf.
