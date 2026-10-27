# Realtime Protocol

Last updated: 2026-10-27

The realtime protocol is the binary contract between SimulationWorker and the
Unity client over LiteNetLib. Its single source is the local Unity package
`src/Shared/GameProtocol`: Unity compiles it as the `Mmorpg.GameProtocol`
assembly, and `src/Shared/DotNet/GameProtocol` compiles the same files for the
backend, the tools, and the tests.

## Wire Format

Every packet starts with a seven-byte frame header, followed by the payload of
its message type. All numbers are little-endian.

| Bytes | Field | Rule |
| ---: | --- | --- |
| 4 | Magic | `0x4F4D4D53` |
| 2 | Version | Must equal `RealtimeProtocol.Version`, currently 27 |
| 1 | Message type | A defined `RealtimeMessageType` |

A packet is at most 1,200 bytes, and an unreliable packet at most 1,023 bytes.
Decoders reject wrong headers, truncated payloads, and trailing bytes. Encoders
throw `ArgumentException` for invalid values and `InvalidOperationException`
for a packet above the limit.

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
files and stays out of client builds.
`realtime-protocol.schema.json` next to it gives editors validation and
completion. `tools/ProtocolGenerator` reads the schema strictly, rejects unknown
properties, duplicate names or ids, and invalid values with the JSON path of the
problem, and writes `src/Shared/GameProtocol/Runtime/Generated`.

The schema currently holds:

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
| `Corpses` | `Corpses/RealtimeProtocol.Corpses.cs`: cross-field rules and the shorter `RealtimeCorpseSlot` constructors |
| `WorldActors` | `WorldActors/RealtimeProtocol.WorldActors.cs`: cross-field rules |
| `Combat` | `Combat/RealtimeProtocol.Combat.cs`: cross-field rules |
| `Admin` | None |

`RealtimeError` is the generated `Error` struct of the `Session` family.

The quality gate, CI, and `ProtocolGeneratorTests` in `Mmorpg.Tools.Tests` all
fail when a generated file is stale, missing, or not produced by the schema.
The commands are in
[Local Development](LOCAL_DEVELOPMENT.md#realtime-protocol-generation).

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
  accept blank text, as the Session messages always did, so the receiving code
  decides what blank text means. On a struct field, such as the `Error` of
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
message that cannot exceed the limit must not set it.
Text counts as up to three UTF-8 bytes per allowed UTF-16 character, the most
any character encodes to.

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
packet the worker sends travels as its descriptor says.

## Player Archetypes

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

## Movement Input And Paths

Protocol version 15 added click-to-move. Every `MovementInput` names the path
the player follows with `PathId`, or 0 to stand. `MovementInputBatch` adds an
optional `Path` with its id and 1 to `MaximumMovementPathWaypoints` (32)
waypoints. The hand-written rule requires the path id to equal the path id of
the newest input in the batch, so a path always travels with the inputs that
name it and can never arrive after them. The client keeps sending the path until
an owner snapshot reports that path id. A batch whose newest input names a path
the worker never received is a protocol error, `unknown_movement_path`.

`OwnerSimulationSnapshot.LastProcessedInputSequence` names the newest input the
worker processed, and the state in the same snapshot is the state right after
it. SimulationWorker steps once per input, in sequence order, and may take a few
steps ahead of a late input along the current path; those steps never appear in
the owner snapshot until their input arrived. The client therefore replays
exactly the inputs after the acknowledged one. Remote `SimulationSnapshot`
records carry the current state, including such steps. This needed no wire
change.

`OwnerSimulationSnapshot` ends with `MovementPathProgress`: the followed path id,
the next waypoint index, at most 32, which equals the waypoint count once the
path ended, and the consecutive ticks in which collision blocked the path.
Without a path, the index and the blocked ticks must be 0. Remote
`PlayerState` records carry no path data, and its only flag is `IsGrounded`.
`MovementSettings` no longer has sprint speed or jump velocity.

## Corpse Sections

A corpse view section may have zero slots. Protocol version 16 allows
`SectionSlotCapacity` 0, because a live Mob corpse carries only general loot and
its equipment and Bag sections are empty, and a fully looted live Mob corpse has
no general slots left. Carried weight and carry capacity are unitless integers.

## Corpse Presence Facing

Since protocol version 23, every `CorpsePresence` carries `YawDegrees`, the
facing of whoever left the corpse around the vertical axis. The worker takes it
from the rotation it stores with a player corpse, a turn around Y that the death
request builds from the character's yaw, so the client lays the body of a player
corpse down as it fell. A durable Mob corpse carries the facing its Mob had and a
live Mob corpse sends 0; neither matters until Mob corpses get models.

The presentation key tells the client what to show. A player corpse sends
`corpse.remains`, which the client shows as the body, and a Mob corpse sends
`corpse.mob`, which keeps the generic capsule. GameService accepts only these
keys for player deaths and for durable Mob corpses.

## Corpse View Closures

`CorpseViewClosed` names why a view ended with a stable code, and the hand-written
`RealtimeCorpseViewClosureCodes` holds the codes the worker sends itself:

| Code | Meaning |
| --- | --- |
| `corpse_view_closed` | The viewer closed the view; it follows the close result. |
| `corpse_emptied` | The last item of a live Mob corpse was taken. |
| `corpse_expired` | The corpse reached its deadline. |
| `corpse_invalidated` | The corpse disappeared or its state can no longer be trusted. |
| `corpse_not_found` | The worker or GameService no longer knows the corpse. |

A view also closes with `combat_character_dead` when its viewer dies, and with
the code of a rejection that ends it, such as `wrong_simulation_worker`. The code
is ordinary text up to the error-code limit, so `corpse_emptied` was added without
a new protocol version. The client treats the viewer's own close, an emptied or
expired corpse, and a death as intended ends and every other code as an error.

## Capability Actions

Protocol version 17 added vendor trades and gathering. A
`WorldInteractionIntent` of kind `CapabilityAction` carries a
`CapabilityActionKind`, renamed from `NpcLifecycleActionKind`: `None` (open the
capability), the insurance and quest actions, and the new `VendorPurchase`
(5), `VendorSale` (6), and `StartGathering` (7). The action also carries
`OfferDefinitionId`, `Quantity`, and `ExpectedPrice`. The hand-written rules
accept only the fields each action uses:

- `VendorPurchase` targets a `Vendor` capability and needs an offer definition,
  a quantity of at least 1, and an expected price of 0 or more, with no item and
  no destination.
- `VendorSale` targets a `Vendor` capability and needs an item, a quantity of
  at least 1, and an expected price, with no offer definition and no
  destination.
- `StartGathering` targets a `Gathering` capability and carries no character
  revision, item, destination, or vendor terms.

The expected price is the total the player was shown. GameService recomputes
the price from the catalog and rejects a difference with `vendor_price_changed`,
so a stale client display can never charge a different amount.

`WorldActorKind` gained `ResourceNode` (3), and `WorldActorCapabilityKind`
gained `Gathering` (10). Every `WorldActorCapability` now carries a `ProfileId`:
the item catalog vendor profile of a `Vendor` capability, the resource profile
of a `Gathering` capability, and empty for every other kind.

`GatheringState` reports gathering at a resource node to the gathering player,
keyed by the interaction session and the node's entity and runtime id. Its
`Kind` is `Started` with the cycle time in milliseconds, `Yielded` with the
cycle time, the yield definition and quantity, and the item-state revision
after the yield, or `Stopped` with a stable code and a message, such as
`resource_node_depleted`, `gathering_interrupted`, `world_interaction_closed`,
or a GameService rejection code. A `Yielded` or `Stopped` can arrive after the
interaction closed, and a `Yielded` even after `Stopped`, because a cycle that
was already in flight still completes. The worker always sends the `Yielded` of
a committed cycle, so the client learns the item-state revision that holds the
yield.

## Combat

Protocol version 18 added target-based combat, a `Combat` message family, an
owner item delta, and the Shard rules:

- `CombatIntent` selects the attack target with `AttackTarget` and a target
  entity id of at least 1, or clears it with `ClearTarget` and no target.
  `CombatIntentResult` answers it with the target afterwards and, on failure,
  one of the stable codes in `RealtimeCombatErrorCodes`:
  `combat_target_not_found`, `combat_target_not_attackable`,
  `combat_target_dead`, `combat_target_self`,
  `combat_player_versus_player_disabled`, `combat_character_dead`, and
  `combat_intent_invalid`. Combat intents share a per-peer budget of 8 per second
  with a burst of 12; more return `combat_intent_rate_limited`.
- `CombatantState` carries the health, maximum health, life state, combat flag,
  and attack target of a visible combatant with a revision that only grows. It
  follows every `EntitySpawn` and `WorldActorSpawn` of a combatant, so a client
  never shows a combatant without health. A dead combatant has 0 health and no
  target, and nobody targets itself.
- `CombatEventBatch` carries the events of one server tick that involve a
  combatant the recipient sees or controls, in simulation order: `AttackReleased`
  with the impact tick, `Hit` with the damage, damage type, and health afterwards,
  and `Died` with the killer, or 0 when none is known. Unused fields are 0. More
  than `MaximumCombatEventsPerBatch` (32) events split into several batches.
- `OwnerCombatState` goes to the owner only: its health, life state, a
  `LifeSequence` that grows with every death and respawn, the respawn tick while
  dead, the target, the attack status, the next attack tick, the end of the
  equipment lockout, and the equipped arrow stack with the live arrow count,
  which already subtracts shots that are not committed yet. Conditions that
  change every tick are attack statuses (`Moving`, `OutOfRange`,
  `LineOfSightBlocked`, `EquipmentLockout`, `WeaponMissing`, `AmmunitionMissing`,
  `AmmunitionIncompatible`), not errors.
- `OwnerItemQuantityChanged`, in the Items family, tells the owner that a
  committed operation it did not ask for changed an item's quantity, today only
  spent arrows: the item-state revision it applies to, the new carry state, the
  item, and its new quantity and revision, both 0 when the stack was used up.
  The client patches its inventory when its known revision matches and refreshes
  it otherwise.
- `JoinAccepted` ends with `ShardRules`, so the client knows whether a click on
  another character attacks.

Since the Mob brains, a moving Mob travels in `SimulationSnapshot` like a remote
player: an `EntitySnapshot` with the Mob's world actor entity id, input sequence
0, and its `PlayerState`, while its pose changes. The first tick it stands still
sends one reliable `WorldActorState` with its rest pose and a newer state
revision. The wire format already allowed any entity id, and clients skip
snapshot entities they do not know, so this needed no new protocol version.

## Owner Item State Changes

Protocol version 21 added `OwnerItemStateChanged` (35), in the Items family, so
the client patches its inventory after a committed transaction instead of
reloading it over HTTP. It carries:

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
  and each item lists up to `MaximumItemPolicies` (8) active
  `ItemPolicySummary` structs with a kind, a status, and a protection source of
  up to `MaximumItemPolicyTextLength` (64) characters. Since protocol version
  22 a Bag in the Bag equipment slot also names its `BagContentsContainerId`
  and `BagContentsRevision`, and the change lists every item inside a Bag that
  the transaction put into the Bag slot. A hand-written rule rejects any field
  that does not belong to the location, and contents on anything but equipment.
- `ClaimedRecoveryDeliveryIds`: since protocol version 22, up to
  `MaximumItemResultRecoveryDeliveries` (8) distinct deliveries that a Recovery
  claim took out of Recovery Storage, whose items the change places elsewhere.

The message can exceed the packet limit, so it sets `runtimeSizeCheck`, and
`RealtimeProtocol.CanEncodeOwnerItemStateChanged` lets the worker check a change
before it encodes it. The worker sends the change only when the transaction
reports every changed item, touches the owner alone, adds no Recovery delivery
other than by a claim, changes no Secure Container tier, and fits one packet; the
client reloads its inventory in every other case. It always goes out before the
carry state and the result of the operation that caused it.
## Corpse Loot Into Equipment

Protocol version 19 added `CorpseInteractionKind.LootToEquipment` (11) and the
variant field `EquipmentSlotId` to `CorpseInteractionIntent`. The case carries
the whole corpse item (`ItemInstanceId`, `ExpectedItemRevision`), the
equipment slot, and, as the `TargetItemInstanceId` group, the item the
character had equipped in that slot when the client looked, or no target for
an empty slot. The hand-written rule rejects a target equal to the looted
item. GameService tops up an equipped stack of the same definition, equips into
an empty slot, or swaps the equipped item into the corpse slot the loot left;
a live Mob corpse never swaps: it fills an empty slot or tops up the named stack
of the same item, and GameService rejects any other target. Payloads of every earlier case are
unchanged.

Since protocol version 26 a corpse Bag also loots into equipment: with no
target it takes the empty Bag slot together with whatever it holds, and the
corpse keeps a new, empty Bag section. An occupied Bag slot still needs
`SwapBag`. An empty corpse Bag also loots into an empty container slot through
`LootItem`.

## Corpse Loot All

Protocol version 26 added `CorpseInteractionKind.LootAll` (12). It carries only
the operation, the corpse, and `ExpectedCorpseRevision`, which must equal the
corpse's revision when the worker handles it, so a corpse that changed answers
`item_state_conflict` with a refresh. GameService plans every destination, as
[Game Features](GAME_FEATURES.md#player-inventory-foundation) describes, and
takes everything that fits in one transaction; nothing fitting is the
`inventory_full` rejection. The result, the committed change, and the corpse
view follow as for any corpse mutation; a Loot all that changes more than one
`OwnerItemStateChanged` can describe makes the client reload its inventory.

## World Menu Options

Protocol version 20 appended two fields to `WorldActorSpawn` for the client's
right-click world menu: `ExamineText`, up to
`MaximumWorldActorExamineTextLength` (200) characters, and `MenuOptions`, up to
`MaximumWorldActorCapabilities` (16) `WorldActorMenuOption` structs with
distinct `CapabilityId` values. Each option names a capability, its
`WorldActorCapabilityKind`, and its `Label`, the menu verb of up to
`MaximumWorldActorMenuOptionLength` (32) characters, in the actor's authored
capability order. The spawn carries no availability, revision, or profile: those
stay in the player-specific capability summary of `WorldInteractionOpened`. Both
limits are public, and `WorldActorTextRules` in WorldData must fit them.

## Character Appearance

Protocol version 24 sends every client how a player looks, as
[Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#the-look-on-the-server)
describes. `CharacterAppearance` holds the `BodyModel`, such as `Human_Female`,
up to `MaximumAppearanceChoices` (16) `AppearanceChoice` structs, each a `Kind`,
an optional `Slot` for a kind worn per slot such as underwear in `legs`, and an
`Option`, up to `MaximumVisibleEquipment` (8) `VisibleEquipment` structs, each an
equipment slot and an item definition with distinct slots, and `HeadwearLowered`.
Kinds and slots are at most `MaximumAppearanceKindLength` and
`MaximumAppearanceSlotLength` (32) characters, and body models, options, and
definitions at most `MaximumAppearanceNameLength` (64).

- `EntitySpawn` appends the player's `Appearance`. The worker fills it from the
  look GameService sent with the join ticket, the items of the visible equipment
  slots of `content/characters/appearance.json` in their content order, and for a
  development bot the look and outfit it picks from the bot's character id.
- `EntityAppearanceChanged` (36) sends a player's new look, by its entity id, to
  every connection that sees the player and to its owner. The owner gets its own
  look after the join, since it never receives a spawn of its own player. The
  worker sends it when the look, the visible equipment, or the headwear changed,
  and the client drops a change for a player it does not see.
- `AppearanceIntent` (37) asks to lower the headwear (`LowerHeadwear` = 1), raise
  it (`RaiseHeadwear` = 2), or in Development set another body model and look
  (`SetDevelopmentLook` = 3, with `BodyModel` and `Choices`). The worker asks
  GameService, which refuses a development look outside the Development
  environment, and answers with `AppearanceIntentResult` (38), which echoes the
  operation id and kind; the new look follows as `EntityAppearanceChanged`. A
  development bot cannot change its look, and one change per peer runs at a time.
- `CorpsePresence` appends an optional `Appearance`: a player corpse shows the
  dead character's body model, look, and headwear, with the items still in the
  corpse's equipment section, and a Mob corpse has none. A corpse whose look would
  not fit one presence packet is sent without it and shows the default body.

Protocol version 25 appended an optional `ToolInUse` to `CharacterAppearance`:
the item definition, at most `MaximumDefinitionIdLength` (128) characters, of the
tool the character holds while it uses it, which is the item of its Tool slot
while it gathers, and empty otherwise and for every corpse. The worker sends a
new look when the player starts or stops using its tool; see
[Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#tools-in-use).

## Development Admin Tools

Protocol version 27 added the Admin family for the development tools of the
Unity Admin menu (F3), which only a SimulationWorker with `DevelopmentAdmin:Enabled`
carries out; see
[Development Admin Tools](SERVICE_FEATURES.md#development-admin-tools).

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
  that the worker steps with, so the simulation revision stays the same.

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

## Golden Tests

The golden cases pin the exact bytes of every message type and every accept or
reject decision of the current protocol version in both .NET and Unity. They are
described in [Backend Tests](../tests/README.md#realtime-protocol-goldens).
