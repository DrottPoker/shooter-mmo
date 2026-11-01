# Realtime Protocol

The realtime protocol is the binary contract between SimulationWorker and the
Unity client over LiteNetLib. Its single source is the local Unity package
`src/Shared/GameProtocol`: Unity compiles it as the `Mmorpg.GameProtocol`
assembly, and `src/Shared/DotNet/GameProtocol` compiles the same files for the
backend, the tools, and the tests.

The current protocol version is `protocol.version` in
`src/Shared/GameProtocol/Schema~/realtime-protocol.json`, compiled into
`RealtimeProtocol.Version`. The generator prints it, and SimulationWorker logs it
when its transport starts listening. Client and worker must run the same
version, as [Versioning](#versioning) describes.

## Wire Format

Every packet starts with a seven-byte frame header, followed by the payload of
its message type. All numbers are little-endian.

| Bytes | Field | Rule |
| ---: | --- | --- |
| 4 | Magic | `0x4F4D4D53` |
| 2 | Version | Must equal `RealtimeProtocol.Version` |
| 1 | Message type | A defined `RealtimeMessageType` |

A packet is at most 1,200 bytes, and an unreliable packet at most 1,023 bytes,
LiteNetLib's single-packet limit. Decoders reject wrong headers, truncated
payloads, and trailing bytes. Encoders throw `ArgumentException` for invalid
values and `InvalidOperationException` for a packet above the limit.

| Wire type | Encoding |
| --- | --- |
| `u8`, `u16`, `u32`, `u64`, `i32`, `i64` | Fixed-size integers |
| `f32` | IEEE single; NaN and infinities are rejected in both directions |
| `bool` | One byte written as 0 or 1; any non-zero byte reads as true |
| `guid` | 16 bytes in the order of .NET `Guid.ToByteArray` |
| Text | A `u16` UTF-8 byte length, then the bytes. Text has 1 to the limit of UTF-16 characters, or 0 where blank text is allowed, and at most four bytes per allowed character; invalid UTF-8 is rejected |
| Enum | One byte; undefined values are rejected |
| Array | A `u8` count, then the elements |

## Schema And Generator

`src/Shared/GameProtocol/Schema~/realtime-protocol.json` describes the protocol
once. Unity ignores folders that end in a tilde, so the schema needs no `.meta`
files and stays out of client builds. `realtime-protocol.schema.json` next to it
gives editors validation and completion. `tools/ProtocolGenerator` reads the
schema strictly, rejects unknown properties, duplicate names or ids, and invalid
values with the JSON path of the problem, and writes
`src/Shared/GameProtocol/Runtime/Generated`.

The schema holds:

- `protocol`: the namespace, version, magic, connection key, packet limits,
  channel count, and channels.
- `limits`: named length and count limits. `public` limits become
  `RealtimeProtocolLimits` constants, which content compilers must fit;
  `protocol` limits become public `RealtimeProtocol` constants; `private`
  limits stay inside `RealtimeProtocol`.
- `enums`: every protocol enum with its family and byte values.
- `structs`: value types that messages and other structs contain, such as
  `PlayerState`.
- `messages`: every message type with its id, family, direction, channel,
  delivery, and fields. The generator writes its model class and its codec.

The content compilers fit the public limits: item catalog tags and equipment
slot ids stay within `MaximumSlotTagLength` and `MaximumEquipmentSlotIdLength`
(64), a Bag slot accepts at most `MaximumSlotTags` (16) tags, and actor examine
texts and menu options stay within their limits, as
[World Actor Spawns And Menu Options](#world-actor-spawns-and-menu-options)
describes. A test keeps the `ItemCatalogFormat` limits within
`RealtimeProtocolLimits`.

The generator owns `Runtime/Generated`. `RealtimeProtocol.Core.g.cs` holds
`RealtimeMessageType`, the protocol constants, `RealtimeProtocolLimits`, and the
message descriptors. One file per family holds that family's enums, structs,
generated messages, their `Encode` and `TryDecode` methods, and their
validation. The generator writes each `.meta` file itself with a GUID derived
from the package path, so no Unity Editor step is needed and every machine gets
the same GUID. Generated C# stays within C# 8 and .NET Standard 2.1, because the
.NET project compiles it with C# 8, which Unity's C# 9 compiler also accepts.

Encoding validates the value, computes the exact packet size with generated
`SizeOf` methods, allocates one array of that size, and writes it through the
`PacketWriter` in `RealtimeProtocol.cs`. Decoding reads through `PacketReader`, a
view over the packet bytes, so it allocates only the decoded values. Both use
`BinaryPrimitives` for explicit little-endian access, so the bytes do not depend
on the platform.

Every family is generated from the schema. A family folder holds only what the
schema cannot say:

| Family | Hand-written part |
| --- | --- |
| `Session` | None |
| `Movement` | `Movement/RealtimeProtocol.Movement.cs`: cross-field rules |
| `Replication` | `Replication/RealtimeProtocol.Replication.cs`: the player archetype ids |
| `Items` | `Items/RealtimeProtocol.Items.cs`: cross-field rules |
| `Corpses` | `Corpses/RealtimeProtocol.Corpses.cs`: cross-field rules, the shorter `RealtimeCorpseSlot` constructors, and the corpse view closure codes |
| `WorldActors` | `WorldActors/RealtimeProtocol.WorldActors.cs`: cross-field rules |
| `Combat` | `Combat/RealtimeProtocol.Combat.cs`: cross-field rules and the combat intent error codes |
| `Admin` | None |

`RealtimeError` is the generated `Error` struct of the `Session` family.

### Generate And Verify

After a schema change, regenerate and verify from any repository directory:

```powershell
dotnet run --project tools/ProtocolGenerator --configuration Release
dotnet run --project tools/ProtocolGenerator --configuration Release --no-build -- --verify
```

Expected result: the first command prints
`Realtime protocol version <version> generated: <messages> messages, <files> generated files.`
and the second prints the same line with `verified`. The first command rewrites
only changed files and removes files that the schema no longer produces. The
second fails and names the file when a generated file is stale, missing, or not
produced by the schema. `--repository-root <directory>` points the generator at
another checkout.

Commit the schema, `realtime-protocol.lock.json`, the generated `.cs` files, and
their `.meta` files together. When generation reports that the wire contract of
the current protocol version changed, the schema change alters bytes or
acceptance: increase `protocol.version` in the schema and generate again, and the
lock records the new version next to the old ones. No Unity Editor step is
required, because the generator writes the `.meta` files with stable GUIDs and
Unity compiles the new code when it next refreshes.

The quality gate, CI, and `ProtocolGeneratorTests` in `Mmorpg.Tools.Tests` all
fail when a generated file is stale, missing, or not produced by the schema.

## Fields, Structs, And Value Rules

A generated message and a struct list their fields in wire order. The
generated class takes them in the same order in its constructor and exposes
each as a get-only property with the field's description.

| `type` | C# type | Encoding |
| --- | --- | --- |
| `u8`, `u16`, `u32`, `u64` | `byte`, `ushort`, `uint`, `ulong` | Little-endian |
| `i32`, `i64` | `int`, `long` | Little-endian |
| `f32` | `float` | Always finite |
| `bool` | `bool` | One byte |
| `guid` | `Guid` | 16 bytes |
| `string` | `string` | Text as described above; `maxLength` caps it in UTF-16 characters, and it may not be empty or whitespace when encoded unless `blank` allows it |
| An enum name | The enum | One byte; the value must be defined, and a flags value may only combine defined bits |
| A struct name | The struct class | The struct's fields |
| `array` | An array of `items` | A `u8` count from `minCount` to `maxCount`, then the elements |
| `bits8` | One `bool` property per entry in `bits` | One byte, lowest bit first; unknown bits are rejected |

Value rules sit next to the type and apply both when encoding and when
decoding:

- `minimum` and `maximum` for integers and floats, inclusive. Integer bounds
  may name a limit.
- `exclusiveMinimum` and `exclusiveMaximum` for floats.
- `nonEmpty` for a `guid` that must not be `Guid.Empty`.
- `"format": "guid"` for a string that `Guid.TryParse` must accept, and
  `oneOf` for a string that must be one of the listed values.
- `lessThan` for an integer that must be below another integer field.
- `distinct` for an array of primitives or enums whose elements must differ,
  and `distinctBy` for an array of structs whose elements must differ in one
  field.

Rules that compare fields in other ways, or need arithmetic, set
`"rules": true` on the struct or message. Its generated validation then also
calls a hand-written `SatisfiesRules` overload in the family folder, for
example `Movement/RealtimeProtocol.Movement.cs`. A missing overload is a
compile error. `SatisfiesRules` runs after every field rule passed, so it may
rely on them.

A message uses `"api": "unwrap"` when it has a single field and its encode and
decode methods should take that value directly, as `CarryStateChanged` takes
`RealtimeCarryState`. `parameter` names the encode and decode parameter.
An unwrapped struct field may add `"encodeParameters": "expanded"`, so the
encode method takes the struct's fields instead, as
`EncodeJoinRejected(code, message)` builds the `RealtimeError` itself. A
message with `"api": "empty"` has no fields: `EncodeLeaveAccepted()` takes
nothing, and `TryDecodeLeaveAccepted` only reports whether the packet is valid.

Text must not be empty or whitespace unless its field sets `blank`:

- `"blank": "allow"` makes empty and whitespace text valid in both directions,
  so the length prefix may be 0. `WorldInteractionResult.Message` uses it.
- `"blank": "acceptOnDecode"` keeps the encoder strict but lets the decoder
  accept blank text, so the receiving code decides what blank text means, as
  the Session messages do. On a struct field, such as the `Error` of
  `JoinRejected`, it applies to every text field of that struct, which may then
  contain no struct or array fields. It belongs to message fields only, not to
  struct fields or variant fields.

`"optional": true` writes a struct or string field after its own `bool`, which
is true when the value is present: a struct that is not null, or text that is
not empty or whitespace. An absent struct reads as null and absent text as an
empty string. `RealtimeCorpseSlot` carries its equipment slot and its item this
way.

`"nullAsEmpty": true` makes the constructor turn a null argument into an empty
string or array, as `RealtimeWorldInteractionOpened` does for its capabilities.
It needs an optional string or `"blank": "allow"` on a string, and no
`minCount` on an array.

`sizeConstant` on a fixed-size struct or message, and `emptySizeConstant` on a
message with arrays, become public `RealtimeProtocol` constants such as
`OwnerSimulationSnapshotPacketSize`, so code that budgets packets never repeats
a size by hand.

## Variants And Conditional Fields

A field with `"when": "!Succeeded"` is a struct that is written only while the
named earlier `bool` field is false, or true without the `!`. Its property is
null otherwise, and validation requires exactly that. The item, corpse, and
world interaction results carry their `Error` this way.

A message whose fields depend on a kind, such as `ItemOperationIntent`, lists
`variants`:

- `discriminator` names the enum field among the message fields that selects a
  case. Every enum value must belong to exactly one case.
- `fields` lists every variant field once, with its type and description. A
  field gets its rules in each case, and `absent` sets the integer value it has
  in cases that do not carry it. Other types use `Guid.Empty`, `0`, an empty
  string, an empty array, or null.
- Each case lists its `kinds`, each with the name of its factory method, and
  the variant fields it writes after the common fields, in wire order, with
  their rules. `parameter` renames a factory parameter and `parameterDefault`
  gives it a default.
- `common` entries apply rules to the message fields in one case. A `fixed`
  entry is written as the model value, which must equal it, and decoded as the
  fixed value whatever the wire carries; factories leave it out.
- A `group` entry is written as a `bool` that is true when its `presence`
  field differs from its absent value, followed by the group's fields only
  when it is true. While the presence field is absent, the other group fields
  must hold their absent values too, because encoding would drop them.

The generated class has an internal constructor for the decoder and one public
factory per kind, so a variant can only be built with its own fields.

## Size Limits

The generator computes the smallest and largest encoded size of every
generated message from its schema. A message with unreliable delivery whose
largest size exceeds 1,023 bytes fails generation, so snapshots provably fit.
A reliable message whose largest size can exceed 1,200 bytes must set
`"runtimeSizeCheck": true`, which makes the risk a visible decision; encoding
such a packet then throws `InvalidOperationException` when it is too large. A
message that cannot exceed the limit must not set it. Text counts as up to
three UTF-8 bytes per allowed UTF-16 character, the most any character encodes
to.

## Message Descriptors

`RealtimeProtocol.TryGetDescriptor` returns the direction, channel, and delivery
of every message type, generated from the schema:

| Id | Message | Family | Direction | Channel | Delivery |
| ---: | --- | --- | --- | --- | --- |
| 1 | `JoinRequest` | Session | Client to server | Control | Reliable ordered |
| 2 | `JoinAccepted` | Session | Server to client | Control | Reliable ordered |
| 3 | `JoinRejected` | Session | Server to client | Control | Reliable ordered |
| 4 | `LeaveRequest` | Session | Client to server | Control | Reliable ordered |
| 5 | `LeaveAccepted` | Session | Server to client | Control | Reliable ordered |
| 6 | `LeaveRejected` | Session | Server to client | Control | Reliable ordered |
| 7 | `ServerDisconnect` | Session | Server to client | Control | Reliable ordered |
| 8 | `MovementInputBatch` | Movement | Client to server | Movement input | Sequenced |
| 9 | `SimulationSnapshot` | Replication | Server to client | Unreliable receive | Unreliable |
| 10 | `EntitySpawn` | Replication | Server to client | Control | Reliable ordered |
| 11 | `EntityDespawn` | Replication | Server to client | Control | Reliable ordered |
| 12 | `CarryStateChanged` | Items | Server to client | Control | Reliable ordered |
| 13 | `ItemOperationIntent` | Items | Client to server | Control | Reliable ordered |
| 14 | `ItemOperationResult` | Items | Server to client | Control | Reliable ordered |
| 15 | `CorpsePresenceSnapshotChunk` | Corpses | Server to client | Control | Reliable ordered |
| 16 | `CorpseInteractionIntent` | Corpses | Client to server | Control | Reliable ordered |
| 17 | `CorpseInteractionResult` | Corpses | Server to client | Control | Reliable ordered |
| 18 | `CorpseViewStateChunk` | Corpses | Server to client | Control | Reliable ordered |
| 19 | `CorpseViewClosed` | Corpses | Server to client | Control | Reliable ordered |
| 20 | `WorldActorSpawn` | WorldActors | Server to client | Control | Reliable ordered |
| 21 | `WorldActorState` | WorldActors | Server to client | Control | Reliable ordered |
| 22 | `WorldActorDespawn` | WorldActors | Server to client | Control | Reliable ordered |
| 23 | `WorldInteractionIntent` | WorldActors | Client to server | Control | Reliable ordered |
| 24 | `WorldInteractionOpened` | WorldActors | Server to client | Control | Reliable ordered |
| 25 | `WorldInteractionResult` | WorldActors | Server to client | Control | Reliable ordered |
| 26 | `WorldInteractionClosed` | WorldActors | Server to client | Control | Reliable ordered |
| 27 | `OwnerSimulationSnapshot` | Movement | Server to client | Unreliable receive | Unreliable |
| 28 | `GatheringState` | WorldActors | Server to client | Control | Reliable ordered |
| 29 | `CombatIntent` | Combat | Client to server | Control | Reliable ordered |
| 30 | `CombatIntentResult` | Combat | Server to client | Control | Reliable ordered |
| 31 | `CombatantState` | Combat | Server to client | Control | Reliable ordered |
| 32 | `CombatEventBatch` | Combat | Server to client | Control | Reliable ordered |
| 33 | `OwnerCombatState` | Combat | Server to client | Control | Reliable ordered |
| 34 | `OwnerItemQuantityChanged` | Items | Server to client | Control | Reliable ordered |
| 35 | `OwnerItemStateChanged` | Items | Server to client | Control | Reliable ordered |
| 36 | `EntityAppearanceChanged` | Replication | Server to client | Control | Reliable ordered |
| 37 | `AppearanceIntent` | Replication | Client to server | Control | Reliable ordered |
| 38 | `AppearanceIntentResult` | Replication | Server to client | Control | Reliable ordered |
| 39 | `AdminIntent` | Admin | Client to server | Control | Reliable ordered |
| 40 | `AdminIntentResult` | Admin | Server to client | Control | Reliable ordered |
| 41 | `AdminStateChanged` | Admin | Server to client | Control | Reliable ordered |

The control channel is 0, the movement input channel is 1, and the client
receives unreliable packets on channel 0. SimulationWorker builds its route
table from the descriptors, the Unity client recognizes snapshots by their
unreliable delivery, and `RealtimeReplicationContractTests` checks that every
packet the worker sends travels as its descriptor says. The owner snapshot is
small enough to stay prioritized at the snapshot rate, while remote snapshots
carry application-level tick and chunk metadata.

## Session Messages

- `JoinRequest` carries the join ticket. `JoinAccepted` carries the simulation
  session, account, and character identity, the Shard and World identity, the
  controlled entity id, the simulation and collision revisions, the join and
  session expiry times, whether the join was a reconnect, the carry state, the
  movement settings, the initial player state, and `ShardRules`, so the client
  knows whether a click on another character attacks.
- `JoinRejected`, `LeaveRejected`, and `ServerDisconnect` carry an `Error`: a
  code of up to `MaximumErrorCodeLength` (64) characters and a message of up to
  `MaximumErrorMessageLength` (512). Decoders accept blank text, and the
  receiving code decides what it means.
- Error codes, closure codes, and despawn reasons are ordinary text up to the
  error-code limit, so a new code needs no new protocol version. The stable
  codes are listed in [Realtime Codes](SERVICE_FEATURES.md#realtime-codes).
  `EntityDespawn.Reason` and `WorldActorDespawn.Reason` are informational, such
  as `out_of_interest`, `left_shard`, `teleported`, or `died`.

## Movement Input And Paths

Every `MovementInput` names the path the player follows with `PathId`, or 0 to
stand. `MovementInputBatch` carries up to `MaximumInputBatchSize` (4) inputs and
an optional `Path` with its id and 1 to `MaximumMovementPathWaypoints` (32)
waypoints. The hand-written rule requires the path id to equal the path id of
the newest input in the batch, so a path always travels with the inputs that
name it and can never arrive after them. The client keeps sending the path until
an owner snapshot reports that path id. A batch whose newest input names a path
the worker never received is a protocol error, `unknown_movement_path`.

`OwnerSimulationSnapshot.LastProcessedInputSequence` names the newest input the
worker processed, and the state in the same snapshot is the state right after
it. The worker may take a few steps ahead of a late input along the current
path; those steps never appear in the owner snapshot until their input arrived,
so the client replays exactly the inputs after the acknowledged one. Remote
`SimulationSnapshot` records carry the current state, including such steps.

`OwnerSimulationSnapshot` ends with `MovementPathProgress`: the followed path id,
the next waypoint index, at most 32, which equals the waypoint count once the
path ended, and the consecutive ticks in which collision blocked the path.
Without a path, the index and the blocked ticks must be 0. Remote `PlayerState`
records carry position, velocity, facing, and one flag, `IsGrounded`, and no
path data. `MovementSettings` carries the tick and snapshot rates, the walk
speed, turn rate, falling, the World bounds and ground height, and the character
capsule and motor settings that the client predicts with.

## Replication Messages

`EntitySpawn` announces a player entity with its kind, persistent id, display
name, archetype, server tick, initial state, and look. A remote simulation
snapshot chunk carries the snapshot sequence, server tick, chunk index and
count, and up to `MaximumSnapshotEntitiesPerChunk` (24) `EntitySnapshot`
records, each an entity id, its last processed input sequence, and its
`PlayerState`.

A moving Mob travels in `SimulationSnapshot` like a remote player: an
`EntitySnapshot` with the Mob's world actor entity id, input sequence 0, and its
`PlayerState`, while its pose changes. The first tick it stands still sends one
reliable `WorldActorState` with its rest pose and a newer state revision.
Clients skip snapshot entities they do not know.

### Player Archetypes

`EntitySpawn.ArchetypeId` names how the client presents a player entity. It is
free text on the wire, and the hand-written `RealtimePlayerArchetypeIds` in the
Replication family holds the values both sides use, so adding one changes
neither the wire format nor the protocol version:

| Archetype | Meaning |
| --- | --- |
| `player.default` | A character that a person plays. |
| `player.development_bot` | A synthetic development bot from `tools/ActiveSimulationBots`. |

The Unity client presents a spawn with any other archetype as nothing and logs
a warning.

### Character Appearance

`CharacterAppearance` sends every client how a player looks, as
[The Look On The Server](EQUIPMENT_VISUALS_DESIGN.md#the-look-on-the-server)
describes. It holds the `BodyModel`, such as `Human_Female`, up to
`MaximumAppearanceChoices` (16) `AppearanceChoice` structs, each a `Kind`, an
optional `Slot` for a kind worn per slot such as underwear in `legs`, and an
`Option`, up to `MaximumVisibleEquipment` (8) `VisibleEquipment` structs, each an
equipment slot and an item definition with distinct slots, `HeadwearLowered`,
and an optional `ToolInUse`. Kinds and slots are at most
`MaximumAppearanceKindLength` and `MaximumAppearanceSlotLength` (32)
characters, body models, options, and definitions at most
`MaximumAppearanceNameLength` (64), and the tool definition at most
`MaximumDefinitionIdLength` (128).

- `EntitySpawn` ends with the player's `Appearance`. The worker fills it from the
  look GameService sent with the join ticket and the items of the visible
  equipment slots of `content/characters/appearance.json` in their content
  order, and for a development bot from the look and outfit it picks from the
  bot's character id.
- `EntityAppearanceChanged` (36) sends a player's new look, by its entity id, to
  every connection that sees the player and to its owner. The owner gets its own
  look after the join, since it never receives a spawn of its own player. The
  worker sends it when the look, the visible equipment, the headwear, or the
  tool in use changed, and the client drops a change for a player it does not
  see.
- `ToolInUse` names the item of the player's Tool slot while it gathers, and is
  empty otherwise and for every corpse; see
  [Tools In Use](EQUIPMENT_VISUALS_DESIGN.md#tools-in-use).
- `AppearanceIntent` (37) asks to lower the headwear (`LowerHeadwear` = 1), raise
  it (`RaiseHeadwear` = 2), or in Development set another body model and look
  (`SetDevelopmentLook` = 3, with `BodyModel` and `Choices`). The worker answers
  with `AppearanceIntentResult` (38), which echoes the operation id and kind;
  the new look follows as `EntityAppearanceChanged`.
- `CorpsePresence` ends with an optional `Appearance`: a player corpse shows the
  dead character's body model, look, and headwear, with the items still in the
  corpse's equipment section, and a Mob corpse has none. A corpse whose look
  would not fit one presence packet is sent without it and shows the default
  body.

## Item Messages

- `CarryStateChanged` (12) carries a newer committed carry state: the item-state
  revision, carried weight, and carry capacity, all unitless integers. The worker
  sends it before the newer state affects movement.
- `ItemOperationIntent` (13) carries an operation id, an `ItemOperationKind`
  (`Relocate`, `Equip`, `Unequip`, `SplitStack`, `MergeStacks`, `Destroy`,
  `ClaimRecoveryDelivery`, or `SwapContainerItems`), the expected character
  revision, and the variant fields of its kind. `ItemOperationResult` (14)
  correlates it by operation id and carries success, whether the client must
  refresh its inventory, the carry state, the `Error` on failure, and the item,
  container, and Recovery delivery revisions it changed.
- `OwnerItemQuantityChanged` (34) tells the owner that a committed operation it
  did not ask for changed an item's quantity, today only spent arrows: the
  reason, the item-state revision it applies to, the new carry state, the item,
  and its new quantity and revision, both 0 when the stack was used up. The
  client patches its inventory when its known revision matches and refreshes it
  otherwise.

### Owner Item State Changes

`OwnerItemStateChanged` (35) lets the client patch its inventory after a
committed transaction instead of reloading it over HTTP. It carries:

- `OperationId`: the client operation that committed the change, or empty when
  no client intent caused it, such as a gathering yield.
- `PreviousItemStateRevision` and `CarryState`: the revision the change applies
  to and the carry state after it. The hand-written rule requires that the
  carry state's revision is exactly one higher, because every transaction
  advances the item-state revision by one.
- `Currency`: the balance after the change, at least 0.
- `ContainerRevisions`: up to `MaximumItemResultContainerRevisions` (8)
  distinct containers and their new revisions, including containers the
  client does not show, such as a corpse.
- `Items`: up to `MaximumItemStateChanges` (16) distinct `ItemStateChange`
  structs, each an item id, an `ItemStateLocationKind`, and the item's final
  state. `Removed` (1) carries only the id; `Container` (2) carries the
  definition, a quantity of at least 1, the revision, the container, and a slot
  of at least 0; `Equipment` (3) carries the definition, quantity, revision, and
  the equipment slot. The definition and the equipment slot are optional text,
  and each item lists up to `MaximumItemPolicies` (8) active `ItemPolicySummary`
  structs with a kind, a status, and a protection source of up to
  `MaximumItemPolicyTextLength` (64) characters. A Bag in the Bag equipment slot
  also names its `BagContentsContainerId` and `BagContentsRevision`, and the
  change lists every item inside a Bag that the transaction put into the Bag
  slot. A hand-written rule rejects any field that does not belong to the
  location, and contents on anything but equipment.
- `ClaimedRecoveryDeliveryIds`: up to `MaximumItemResultRecoveryDeliveries` (8)
  distinct deliveries that a Recovery claim took out of Recovery Storage, whose
  items the change places elsewhere.

The message can exceed the packet limit, so it sets `runtimeSizeCheck`, and
`RealtimeProtocol.CanEncodeOwnerItemStateChanged` lets the worker check a change
before it encodes it. The worker sends the change only when the transaction
reports every changed item, touches the owner alone, adds no Recovery delivery
other than by a claim, changes no Secure Container tier, and fits one packet;
the client reloads its inventory in every other case. It always goes out before
the carry state and the result of the operation that caused it.

## Corpse Messages

- `CorpsePresenceSnapshotChunk` (15) carries the nearby corpses in chunks of up
  to `MaximumCorpsePresencePerChunk` (6) `CorpsePresence` structs, each with its
  id, position, facing, absolute expiry, whether it is empty, source name,
  presentation key, and optional look.
- `CorpseInteractionIntent` (16) carries an operation id, a
  `CorpseInteractionKind`, the corpse, its expected revision, and the variant
  fields of the kind; `CorpseInteractionResult` (17) correlates it and says
  whether the client must refresh the corpse or its inventory.
- `CorpseViewStateChunk` (18) carries a complete snapshot or a targeted delta of
  one corpse view, one section at a time in chunks of up to
  `MaximumCorpseViewSlotsPerChunk` (4) slots, with the base and new revision.
  Every slot carries the tags it accepts, and every corpse equipment slot its
  canonical equipment-slot id.
- Corpse chunks are packed by encoded UTF-8 size under the 1,200 byte limit, and
  one chunked update has at most `MaximumCorpseChunkCount` (256) chunks.

### Corpse Sections

A corpse view section may have zero slots: `SectionSlotCapacity` may be 0,
because a live Mob corpse carries only general loot and its equipment and Bag
sections are empty, and a fully looted live Mob corpse has no general slots
left. Carried weight and carry capacity are unitless integers.

### Corpse Presence Facing

Every `CorpsePresence` carries `YawDegrees`, the facing of whoever left the
corpse around the vertical axis. The worker takes it from the rotation it stores
with a player corpse, a turn around Y that the death request builds from the
character's yaw, so the client lays the body of a player corpse down as it fell.
A live and a durable Mob corpse both carry the facing its Mob had when it died.

The presentation key tells the client what to show. A player corpse sends
`corpse.remains`, which the client shows as the body. A Mob corpse sends
`corpse.` followed by its Mob's presentation archetype, such as
`corpse.mob.feral_wolf` for `mob.feral_wolf`, which the client shows as the
model of that archetype's prefab lying dead; `WorldActorCorpseRules` builds the
key, and a Mob whose archetype does not start with `mob.` sends `corpse.mob`. A
key whose archetype has no model, and `corpse.mob`, keep the generic capsule.
The keys are content, not protocol, so a new Mob model needs no new protocol
version. The keys that GameService accepts are listed in
[Corpse Interaction](SERVICE_FEATURES.md#corpse-interaction).

### Corpse Loot Into Equipment

`CorpseInteractionKind.LootToEquipment` (11) carries the whole corpse item
(`ItemInstanceId`, `ExpectedItemRevision`), the variant field `EquipmentSlotId`,
and, as the `TargetItemInstanceId` group, the item the character had equipped in
that slot when the client looked, or no target for an empty slot. The
hand-written rule rejects a target equal to the looted item. How GameService
places the item is in
[Equipment](INVENTORY_AND_DEATH_LOOT_DESIGN.md#equipment).

A corpse Bag with contents also loots into equipment: with no target it takes
the empty Bag slot together with whatever it holds. An occupied Bag slot needs
`SwapBag`, and an empty corpse Bag loots into an empty container slot through
`LootItem`.

### Corpse Loot All

`CorpseInteractionKind.LootAll` (12) carries only the operation, the corpse, and
`ExpectedCorpseRevision`, which must equal the corpse's revision when the worker
handles it, so a corpse that changed answers `item_state_conflict` with a
refresh. GameService plans every destination as
[Loot All](INVENTORY_AND_DEATH_LOOT_DESIGN.md#loot-all) defines and takes
everything that fits in one transaction. The result, the committed change, and
the corpse view follow as for any corpse mutation; a Loot all that changes more
than one `OwnerItemStateChanged` can describe makes the client reload its
inventory.

### Corpse View Closures

`CorpseViewClosed` (19) names why a view ended with a stable code and a message.
The hand-written `RealtimeCorpseViewClosureCodes` holds the codes the worker
sends itself: `corpse_view_closed`, `corpse_emptied`, `corpse_expired`,
`corpse_invalidated`, and `corpse_not_found`. A view also closes with
`combat_character_dead` when its viewer dies, and with the code of a rejection
that ends it, such as `wrong_simulation_worker`. The meanings are in
[Realtime Codes](SERVICE_FEATURES.md#realtime-codes).

## World Actor Messages

The world actor messages keep actor visibility apart from interaction state:

- `WorldActorSpawn` (20), `WorldActorState` (21), and `WorldActorDespawn` (22)
  carry the actor's runtime and network identity, its definition and spawn ids,
  display name, kind, faction, disposition, level, aggression, presentation
  archetype, state and interaction revisions, active flag, position, facing,
  activity tier, and interaction bounds. A state update goes out only when replicated state
  changes, and a resource node's available or depleted state reuses `IsActive`.
- `WorldInteractionIntent` (23) opens, closes, or acts on an interaction. Every
  intent carries an operation id, the simulation session, the interaction
  session, the target kind, entity id, and runtime id, and the expected target
  revision. A `CapabilityAction` also carries the capability id, kind, and
  expected revision, the action, and the action's fields.
- `WorldInteractionOpened` (24) carries the interaction session, the target, the
  capability summary revision, and the player-specific capability summary: every
  `WorldActorCapability` with its id, kind, display name, `ProfileId`,
  availability, and revision. `ProfileId` names the item catalog vendor profile
  of a `Vendor` capability, the resource profile of a `Gathering` capability,
  and is empty for every other kind.
- `WorldInteractionResult` (25) correlates an operation by id and carries the
  target and item-state revisions, a message that may be blank, and an `Error`
  on failure. `WorldInteractionClosed` (26) names a server-initiated closure
  with a stable code and a message.

Strings and lists are bounded. Large future capability state must use explicit
chunking or a focused service read rather than an oversized actor packet.

### World Actor Spawns And Menu Options

`WorldActorSpawn` ends with two fields for the client's right-click world menu:
`ExamineText`, up to `MaximumWorldActorExamineTextLength` (200) characters, and
`MenuOptions`, up to `MaximumWorldActorCapabilities` (16) `WorldActorMenuOption`
structs with distinct `CapabilityId` values. Each option names a capability, its
`WorldActorCapabilityKind`, and its `Label`, the menu verb of up to
`MaximumWorldActorMenuOptionLength` (32) characters, in the actor's authored
capability order. The spawn carries no availability, revision, or profile: those
stay in the player-specific capability summary of `WorldInteractionOpened`. Both
text limits are public, and `WorldActorTextRules` in WorldData must fit them.

### World Actor Level And Aggression

`WorldActorSpawn` carries what a Mob's nameplate shows besides its name and
health. `Level` is a `u16` of at most `MaximumWorldActorLevel` (10,000), the
Mob's authored level, and 0 for an NPC or resource node.
`WorldActorAggression` is `Aggressive` for a Mob that attacks the players it
detects, `Defensive` for one that fights only once damaged, and `None` for an
actor without a brain. Both are static for the actor's lifetime, so a changed
value without a despawn is an identity conflict. The level limit is public, and
`WorldActorCombatRules.MaximumLevel` in WorldData must fit it.

### Capability Actions

A `WorldInteractionIntent` of kind `CapabilityAction` carries a
`CapabilityActionKind`: `None` (0) opens the capability, `ApplyInsurance` (1),
`RemoveInsurance` (2), `AcceptQuest` (3), and `AbandonQuest` (4) are the
insurance and quest actions, and `VendorPurchase` (5), `VendorSale` (6), and
`StartGathering` (7) trade and gather. The action also carries the expected
character revision, an item, a destination, `OfferDefinitionId`, `Quantity`,
and `ExpectedPrice`. The hand-written rules accept only the fields each action
uses:

- `VendorPurchase` targets a `Vendor` capability and needs an offer definition,
  a quantity of at least 1, and an expected price of 0 or more, with no item and
  no destination.
- `VendorSale` targets a `Vendor` capability and needs an item, a quantity of
  at least 1, and an expected price, with no offer definition and no
  destination.
- `StartGathering` targets a `Gathering` capability and carries no character
  revision, item, destination, or vendor terms.

The expected price is the total the player was shown; GameService rejects a
different current price with `vendor_price_changed`.

### Gathering State

`GatheringState` (28) reports gathering at a resource node to the gathering
player, keyed by the interaction session and the node's entity and runtime id.
Its `Kind` is `Started` with the cycle time in milliseconds, `Yielded` with the
cycle time, the yield definition and quantity, and the item-state revision
after the yield, or `Stopped` with a stable code and a message. A `Yielded` or
`Stopped` can arrive after the interaction closed, and a `Yielded` even after
`Stopped`, because a cycle that was already in flight still completes. The
worker always sends the `Yielded` of a committed cycle, so the client learns the
item-state revision that holds the yield.

## Combat

- `CombatIntent` (29) selects the attack target with `AttackTarget` and a target
  entity id of at least 1, or clears it with `ClearTarget` and no target.
  `CombatIntentResult` (30) answers it with the target afterwards and, on
  failure, one of the stable codes in `RealtimeCombatErrorCodes`.
- `CombatantState` (31) carries the health, maximum health, life state
  (`Alive`, `Dead`, or `Evading`), combat flag, and attack target of a visible
  combatant with a revision that only grows. It follows every `EntitySpawn` and
  `WorldActorSpawn` of a combatant, so a client never shows a combatant without
  health. A dead combatant has 0 health and no target, and nobody targets
  itself.
- `CombatEventBatch` (32) carries the events of one server tick that involve a
  combatant the recipient sees or controls, in simulation order. Every
  `CombatEvent` has a kind, source, target, `Tick`, damage, damage type, health
  afterwards, and attack style; unused fields are 0. More than
  `MaximumCombatEventsPerBatch` (32) events split into several batches.
  - `AttackStarted` (4) names the attacker, the target, the attack style, and in
    `Tick` the server tick at which the attack releases. Every client that sees
    the attacker plays its attack clip from it, timed so the clip strikes or
    looses at that tick.
  - `AttackReleased` (1) names the attack style and in `Tick` the impact tick. A
    Mob's bite releases at once, with `AttackReleased` alone.
  - `AttackCancelled` (5) names the attacker, the target, and the attack style of
    an attack that ended in its windup, with `Tick` 0.
  - `Hit` (2) carries the damage, the damage type, and the health afterwards,
    and `Died` (3) the killer, or 0 when none is known.
  - `AttackStyle` is a `CombatAttackStyle` of `Melee` (1), `Ranged` (2), or
    `Magic` (3): the combat style of a started, released, or cancelled attack,
    which selects how a client draws it, such as an arrow or a magic bolt, and
    `Melee` for every other kind.
- `OwnerCombatState` (33) goes to the owner only: its health and maximum health,
  its `Mana` and `MaximumMana`, the mana never above its maximum, its life
  state, a `LifeSequence` that grows with every death and respawn, the respawn
  tick while dead, the target, the attack status, the next attack tick, the end
  of the equipment lockout, and the equipped arrow stack with the live arrow
  count, which already subtracts shots that are not committed yet. Conditions
  that change every tick are attack statuses (`Idle`, `Attacking`, `Moving`,
  `OutOfRange`, `LineOfSightBlocked`, `EquipmentLockout`, `WeaponMissing`,
  `AmmunitionMissing`, `AmmunitionIncompatible`, `Dead`, and `NotEnoughMana`),
  not errors. Only the owner learns its mana.

## Development Admin Tools

The Admin family carries the development tools of the Unity Admin menu (F3),
which only a SimulationWorker with `DevelopmentAdmin:Enabled` carries out, as
[Development Admin Tools](SERVICE_FEATURES.md#development-admin-tools)
describes.

- `AdminIntent` (39) carries an operation id and an `AdminIntentKind` with the
  fields of its case: `SetMovementSpeed` (1) with `SpeedPercent`, a u16 from
  `MinimumAdminSpeedPercent` (100) to `MaximumAdminSpeedPercent` (1000) of the
  World's walk speed; `Teleport` (2) with a finite `DestinationX`, `DestinationY`,
  and `DestinationZ`; `RestoreHealth` (3) with none; and `SetInvulnerable` (4)
  with `Enabled`.
- `AdminIntentResult` (40) echoes the operation id and kind, and carries an
  `Error` when it failed, such as `admin_tools_disabled` from a worker without
  the tools.
- `AdminStateChanged` (41) sends the owner its `SpeedPercent`, in the same
  range, and `Invulnerable`. A worker with the tools sends it after every
  `JoinAccepted` and after every change of either, so a client that never
  receives one knows the tools are off. The client predicts with the walk speed
  it carries, through the shared `MovementSimulationSettings.WithWalkSpeedPercent`
  that the worker steps with, so the movement simulation revision stays the same.

## Versioning

Client and worker run in lockstep: both must use the same version. Decoders
reject any other version. SimulationWorker reports its version in its heartbeat,
GameService stores it with the worker, and the client refuses a placement whose
worker runs another version before it connects. During a rolling deployment,
old and new workers can therefore run side by side, and each client is placed
only on a worker with its own version.

- Bump `protocol.version` for every change to the bytes on the wire or to what
  a peer accepts, including limits, enum values, and validation rules.
- Never reuse the id of a removed message or the value of a removed enum
  member.
- Give every new message type a golden vector, and handle every new
  server-to-client type in the Unity client and in `SimulationBotSession`, which
  every development and stress bot runs. The golden tests fail for a type
  without a vector, and `SimulationBotSessionTests` fails for a type the bots do
  not handle.
- The connection key stays `Mmorpg.Realtime.v8`, so a version mismatch is
  reported as a clear protocol error instead of a refused connection.

`Schema~/realtime-protocol.lock.json` enforces the first rule. It records the
SHA-256 fingerprint of the wire contract of every version: the magic, the
connection key, the packet limits, the channel count, every limit value, and
for every message its id, direction, channel, delivery, field types and order,
value rules, presence, blank handling, variants, and whether a hand-written
rules overload takes part. Names, descriptions, and options that only shape the
C# API are left out, so a rename needs no new version. Generation fails when
the contract of a locked version changes, records a new entry when
`protocol.version` grows, and refuses a version below the newest locked one, so
a version number is never reused. The fingerprint cannot see inside a
hand-written `SatisfiesRules` overload: a change there that alters what a peer
accepts needs a version bump by hand.

Content that travels as free text, such as player archetypes, corpse
presentation keys, error and closure codes, and despawn reasons, can grow
without a new version, as long as it fits the field's limit.

## Golden Tests

The golden cases pin the exact bytes of every message type and every accept or
reject decision of the current protocol version in both .NET and Unity. They are
described in [Backend Tests](../tests/README.md#realtime-protocol-goldens).
