# Realtime Protocol

Last updated: 2026-10-15

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
| 2 | Version | Must equal `RealtimeProtocol.Version`, currently 14 |
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
| Text | A `u16` UTF-8 byte length, then the bytes. Required text has 1 to the limit of UTF-16 characters and at most four bytes per allowed character; invalid UTF-8 is rejected |
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
- `messages`: every message type with its id, family, direction, channel, and
  delivery. A message with `"codec": "generated"` also lists its fields, and
  the generator writes its model class and codec. A message with
  `"codec": "handWritten"` keeps its model and codec in its family folder.

The generator owns `Runtime/Generated`. `RealtimeProtocol.Core.g.cs` holds
`RealtimeMessageType`, the protocol constants, `RealtimeProtocolLimits`, and the
message descriptors. One file per family holds that family's enums, structs,
generated messages, their `Encode` and `TryDecode` methods, and their
validation. The generator writes each `.meta` file itself with a GUID derived
from the package path, so no Unity Editor step is needed and every machine gets
the same GUID. Generated C# stays within C# 8 and .NET Standard 2.1, because the
.NET project compiles it with C# 8, which Unity's C# 9 compiler also accepts.

Structural phase 2 moves the families into the schema one at a time:

| Family | Codec |
| --- | --- |
| `Movement` | Generated; `Movement/RealtimeProtocol.Movement.cs` holds its cross-field rules |
| `Replication` | Generated; no hand-written rules |
| `Items` | Generated; `Items/RealtimeProtocol.Items.cs` holds its cross-field rules |
| `Session` | Hand-written |
| `WorldActors` | Hand-written |
| `Corpses` | Hand-written |

Hand-written codecs call the generated struct helpers, such as
`WritePlayerState`, `TryReadMovementSettings`, and `WriteCarryState`, which
keep the names the hand-written code used before. `RealtimeError` is the
generated `Error` struct of the `Session` family.

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
| `string` | `string` | Text as described above; `maxLength` caps it in UTF-16 characters, and it may not be empty or whitespace when encoded |
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
decode methods should take that value directly, as `MovementInputBatch` takes
`RealtimeMovementInput[]`. `parameter` names the encode and decode parameter.

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
  when it is true.

The generated class has an internal constructor for the decoder and one public
factory per kind, so a variant can only be built with its own fields.

## Size Limits

The generator computes the smallest and largest encoded size of every
generated message from its schema. A message with unreliable delivery whose
largest size exceeds 1,023 bytes fails generation, so snapshots provably fit.
A reliable message whose largest size can exceed 1,200 bytes must set
`"runtimeSizeCheck": true`, which makes the risk a visible decision; encoding
such a packet then throws `InvalidOperationException` when it is too large.
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

## Golden Tests

The golden cases pin the exact bytes of every message type and every accept or
reject decision of protocol version 14 in both .NET and Unity. They are
described in [Backend Tests](../tests/README.md#realtime-protocol-goldens).
