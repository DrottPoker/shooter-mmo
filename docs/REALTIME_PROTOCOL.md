# Realtime Protocol

Last updated: 2026-10-16

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
| 2 | Version | Must equal `RealtimeProtocol.Version`, currently 15 |
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
| `Replication` | None |
| `Items` | `Items/RealtimeProtocol.Items.cs`: cross-field rules |
| `Corpses` | `Corpses/RealtimeProtocol.Corpses.cs`: cross-field rules and the shorter `RealtimeCorpseSlot` constructors |
| `WorldActors` | `WorldActors/RealtimeProtocol.WorldActors.cs`: cross-field rules |

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

The control channel is 0, the movement input channel is 1, and the client
receives unreliable packets on channel 0. SimulationWorker builds its route
table from the descriptors, the Unity client recognizes snapshots by their
unreliable delivery, and `RealtimeReplicationContractTests` checks that every
packet the worker sends travels as its descriptor says.

## Movement Input And Paths

Protocol version 15 carries click-to-move. Every `MovementInput` names the path
the player follows with `PathId`, or 0 to stand. `MovementInputBatch` adds an
optional `Path` with its id and 1 to `MaximumMovementPathWaypoints` (32)
waypoints. The hand-written rule requires the path id to equal the path id of
the newest input in the batch, so a path always travels with the inputs that
name it and can never arrive after them. The client keeps sending the path until
an owner snapshot reports that path id. A batch whose newest input names a path
the worker never received is a protocol error, `unknown_movement_path`.

`OwnerSimulationSnapshot` ends with `MovementPathProgress`: the followed path id,
the next waypoint index, at most 32, which equals the waypoint count once the
path ended, and the consecutive ticks in which collision blocked the path.
Without a path, the index and the blocked ticks must be 0. Remote
`PlayerState` records carry no path data, and its only flag is `IsGrounded`.
`MovementSettings` no longer has sprint speed or jump velocity.

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
reject decision of protocol version 15 in both .NET and Unity. They are
described in [Backend Tests](../tests/README.md#realtime-protocol-goldens).
