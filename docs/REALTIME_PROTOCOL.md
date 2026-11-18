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
values and `InvalidOperationException` for a packet above the limit. LiteNetLib
carries the packets in its own packets, and every UDP datagram that carries
those is clear or sealed, as [Transport Security](#transport-security)
describes.

| Wire type | Encoding |
| --- | --- |
| `u8`, `u16`, `u32`, `u64`, `i16`, `i24`, `i32`, `i64` | Fixed-size integers; `i24` is three bytes of a signed value from -8,388,608 to 8,388,607 |
| `f32` | IEEE single; NaN and infinities are rejected in both directions |
| `bool` | One byte written as 0 or 1; any non-zero byte reads as true |
| `guid` | 16 bytes in the order of .NET `Guid.ToByteArray` |
| Text | A `u16` UTF-8 byte length, then the bytes. Text has 1 to the limit of UTF-16 characters, or 0 where blank text is allowed, and at most four bytes per allowed character; invalid UTF-8 is rejected |
| Enum | One byte; undefined values are rejected |
| Array | A `u8` count, then the elements |

## Transport Security

Everything that a client and SimulationWorker exchange over UDP is encrypted
and authenticated once the join is under way, LiteNetLib's own headers,
acknowledgements, and pings included, and the secret that makes it so never
travels over UDP. There is one mode: no setting turns sealing off, also not in
Development, so the tests, the bots, the load generator, and the benchmarks run
the same transport as the client. The shared code is in
`src/Shared/GameProtocol/Runtime/Security`.

### Join Ticket And Keys

A join ticket is two things, as
[Join Tickets And Simulation Sessions](SERVICE_FEATURES.md#join-tickets-and-simulation-sessions)
describes: a public ticket id, which may travel in clear, and a transport key,
32 random bytes from GameService's cryptographic random source, which only the
HTTPS answers carry: the join answer to the client, and the consume answer to
the worker. From the transport key every side derives its keys with HKDF-SHA256
(RFC 5869, no salt, `RealtimeTransportKeys`), each with its own label:

| Key | HKDF label | Used by |
| --- | --- | --- |
| Proof key | `Mmorpg.Realtime.v1 join proof` | The client to prove the ticket, GameService to check the proof |
| Client-to-worker key | `Mmorpg.Realtime.v1 client to worker` | The client to seal, the worker to open |
| Worker-to-client key | `Mmorpg.Realtime.v1 worker to client` | The worker to seal, the client to open |

Nothing uses the transport key itself. The worker zeroes the transport key and
the proof key as soon as it derived the datagram keys, and every side zeroes its
keys when the connection ends.

### The Join

The LiteNetLib connect stays in clear and carries nothing secret: the
connection key only filters unrelated traffic. The client then sends
`JoinRequest` with:

- `TicketId`: the ticket's public id, a `guid`.
- `ClientNonce`: 16 fresh random bytes (`JoinNonceSize`).
- `Proof`: 32 bytes (`JoinProofSize`), HMAC-SHA256 with the proof key over the
  ASCII label `Mmorpg.Realtime.v1 join request`, the protocol version as a
  `u16`, the ticket id as 16 bytes in the order of `Guid.ToByteArray`, the
  Shard id that the ticket was issued for as a `u16` UTF-8 length and its bytes,
  and the nonce. `RealtimeJoinProof` makes and checks it.

The worker forwards the id, the nonce, the proof, and the protocol version in
its consume call. GameService checks the proof against the ticket's stored key
in constant time before it takes any lock or changes anything, so whoever saw
only the ticket id can neither join with it nor burn it, and a wrong proof is
answered exactly like an unknown ticket. On success the consume answer carries
the transport key to the worker with the session. The call between worker and
GameService is trusted to be private: it must run over TLS or a private
network, because it carries the key.

### Sealed Datagrams

A LiteNetLib packet layer seals and opens whole datagrams, so LiteNetLib's own
packets are sealed too. Its first byte says what follows:

| Bytes | Clear datagram | Sealed datagram |
| ---: | --- | --- |
| 1 | `0` | `1` |
| 8 | | The counter, little-endian |
| n | The LiteNetLib packet | The LiteNetLib packet encrypted |
| 16 | | The tag |

The cipher is ChaCha20-Poly1305 as RFC 8439 defines it. The nonce is four zero
bytes followed by the counter, and the marker byte and the counter are the
associated data, so a changed counter fails the tag. Each direction of each
session has its own key and its own counter, which starts at 0 and is never
used twice. A sealed datagram is 25 bytes longer than its packet and a clear
one 1 byte. Every `NetManager` sets `MtuOverride` to 1,024, so LiteNetLib's
packets keep the limits of [Wire Format](#wire-format) and a datagram is at most
1,049 bytes.

### When Sealing Starts

There is no downgrade:

- The worker installs the keys for the peer's endpoint the moment the consume
  call succeeded, before the join's acceptance. From then on it seals every
  datagram to that endpoint, its answer to the join included, and drops every
  clear datagram from it.
- The client has the keys before it connects. It sends clear datagrams until
  the first sealed datagram of the worker authenticates; from then on it seals
  everything it sends and drops every clear datagram it receives. Clear
  datagrams of the client that were on their way at the switch are dropped by
  the worker, and LiteNetLib sends what was reliable again.
- Before its keys, a clear datagram may carry only LiteNetLib's connection
  control: the connect, its accept, acknowledgements, pings, the disconnect and
  its confirmation, and from the client also the reliable ordered session
  channel that carries `JoinRequest`. `RealtimeClearDatagramRules` checks this,
  inside merged packets too. A forged clear packet on another channel would
  otherwise wait in LiteNetLib for the packets before it and arrive after the
  switch as if sealed. The worker therefore says why it refuses a peer without
  keys only in the data of the disconnect, as
  [The Reason In The Disconnect](#the-reason-in-the-disconnect) describes.
- A peer without keys reaches only the join route; the worker handles no other
  message from it, because `RealtimeMessageDispatcher` routes a peer without a
  session through the unauthenticated table, which holds only `JoinRequest`,
  and the session is set only after the keys are installed.
- A datagram that does not authenticate is dropped and counted, never answered.
  Each side keeps a replay window of 1,024 counters per session
  (`RealtimeReplayWindow`): a counter seen before, or older than the window, is
  dropped. Only a datagram that authenticated moves the window.
- The keys go when the peer disconnects or its session ends. A disconnect that
  the worker sends keeps the peer's keys until the client confirms it or
  LiteNetLib gives up, so the disconnect that LiteNetLib sends again every
  300 ms after a lost one is still sealed. A reconnect uses a new ticket and so
  new keys; a datagram of an earlier session opens nothing of the next one, even
  from the same endpoint.

The worker counts the datagrams, their bytes, the sealing overhead, and the
dropped datagrams by reason, as
[Realtime UDP Transport](SERVICE_FEATURES.md#realtime-udp-transport) describes.

### Cipher Implementations

One small interface, `IRealtimeAead`, has two implementations that produce the
same bytes. Both are tested against the RFC 8439 vectors and against each other
with random inputs.

| Implementation | Where |
| --- | --- |
| `RealtimePlatformChaCha20Poly1305` | The platform's `System.Security.Cryptography.ChaCha20Poly1305`, where `IsSupported` says so: SimulationWorker and the tools. Only the .NET 10 build of the shared library compiles it. A worker whose platform has none logs one warning at its start and uses the managed one |
| `RealtimeManagedChaCha20Poly1305` | Written from RFC 8439, with a constant-time tag comparison: the Unity client in the Editor and in an IL2CPP player, whose class libraries have no ChaCha20-Poly1305 |

`RealtimeAead.Create` picks the platform implementation when it is supported
and the managed one otherwise. HKDF and the proof use `HMACSHA256`, which every
runtime has. No third-party cryptography package is used.

### What It Stops And What It Does Not

It stops whoever sits on the path from reading the traffic, from changing,
injecting, or replaying a datagram without being noticed, and from joining
with, or burning, a ticket whose id it saw. It does not stop:

- Denial of service on the path: dropping, delaying, or flooding datagrams, or
  forging the clear connection control before the keys, such as a disconnect.
- A compromised GameService or SimulationWorker, or a compromised client:
  each of them holds the keys of the sessions it serves.
- A reader of the consume call between worker and GameService, which therefore
  must be private.
- Traffic analysis: the sizes and timing of datagrams stay visible.

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
(64), and a Bag slot accepts at most `MaximumSlotTags` (16) tags. A test keeps
the `ItemCatalogFormat` limits within `RealtimeProtocolLimits`.

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
on the platform. A struct or message with `encodeInto` is also encoded into a
caller's buffer without allocating, as
[Encoding Into A Buffer](#encoding-into-a-buffer) describes.

Every family is generated from the schema. A family folder holds only what the
schema cannot say:

| Family | Hand-written part |
| --- | --- |
| `Session` | None |
| `Movement` | `Movement/RealtimeProtocol.Movement.cs`: cross-field rules |
| `Replication` | `Replication/RealtimeProtocol.Replication.cs`: the player archetype ids and the precision of the remote entity record |
| `Items` | `Items/RealtimeProtocol.Items.cs`: cross-field rules |
| `Corpses` | `Corpses/RealtimeProtocol.Corpses.cs`: cross-field rules, the shorter `RealtimeCorpseSlot` constructors, and the corpse view closure codes |
| `WorldActors` | `WorldActors/RealtimeProtocol.WorldActors.cs`: cross-field rules |
| `Combat` | `Combat/RealtimeProtocol.Combat.cs`: cross-field rules, the combat intent error codes, and `RealtimeCombatHealth`, the health rule of a combatant state |
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
| `i16`, `i24`, `i32`, `i64` | `short`, `int`, `int`, `long` | Little-endian; an `i24` property must hold a 24-bit value |
| `f32` | `float` | Always finite |
| `bool` | `bool` | One byte |
| `guid` | `Guid` | 16 bytes |
| `string` | `string` | Text as described above; `maxLength` caps it in UTF-16 characters, and it may not be empty or whitespace when encoded unless `blank` allows it |
| An enum name | The enum | One byte; the value must be defined, and a flags value may only combine defined bits |
| A struct name | The struct class | The struct's fields |
| `array` | An array of `items` | A `u8` count from `minCount` to `maxCount`, then the elements |
| `bits8`, `bits16` | One property per entry in `bits`: a `bool`, or with a `width` of 2 or more an unsigned `byte` or `ushort` that must fit that many bits | One or two bytes, lowest bit first; unknown bits are rejected |

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
`RealtimeCarryState` and `LeaveAccepted` takes its `BodySeconds`. `parameter`
names the encode and decode parameter. An unwrapped struct field may add
`"encodeParameters": "expanded"`, so the encode method takes the struct's fields
instead, as `EncodeJoinRejected(code, message)` builds the `RealtimeError`
itself. A message with `"api": "empty"` has no fields: its encode method takes
nothing, and its decode method only reports whether the packet is valid. No
message uses it today.

Text must not be empty or whitespace unless its field sets `blank`:

- `"blank": "allow"` makes empty and whitespace text valid in both directions,
  so the length prefix may be 0. `WorldInteractionResult.NoticeCode` uses it.
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

### Encoding Into A Buffer

`"encodeInto": true` adds an encoder that writes into a caller's `Span<byte>`
instead of allocating a packet, for a path that sends many packets per tick:

- On a struct of fixed size without `rules`, whose fields are numbers, enums,
  or bit fields, `Encode<Struct>(destination, members)` validates the members as
  the struct's own validation does and writes the struct's bytes.
- On a message without `rules` or variants whose last field is an array of
  such a struct, after fields of the same kinds, and without `distinct` rules,
  `Encode<Message>Header(destination, fields, count)` validates the fields and
  the count, writes the frame header, the fields, and the count, and returns
  how many bytes it wrote. The caller appends exactly count structs, each
  written by the struct's encoder.
- On a message without `rules` or variants whose fields are numbers, enums,
  bit fields, or structs of them, `Encode<Message>(destination, members)` takes
  every member, a struct field's members named after the field, such as
  `stateIsGrounded`, validates them as the message's validation does, writes
  the whole frame, and returns how many bytes it wrote. A struct field with
  `rules` needs a hand-written `Satisfies<Struct>Rules(members)` beside the
  struct's own rule, which the encoder calls.

They throw `ArgumentException` for an invalid value or a destination that is
too short. The option shapes only the C# API, so the wire contract and its
fingerprint stay the same. `SimulationSnapshot` and its `EntitySnapshot` use it,
and so does `OwnerSimulationSnapshot`, so the worker builds every snapshot
packet without allocating.

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
| 1 | `JoinRequest` | Session | Client to server | Session | Reliable ordered |
| 2 | `JoinAccepted` | Session | Server to client | Session | Reliable ordered |
| 3 | `JoinRejected` | Session | Server to client | Session | Reliable ordered |
| 4 | `LeaveRequest` | Session | Client to server | Owner | Reliable ordered |
| 5 | `LeaveAccepted` | Session | Server to client | Session | Reliable ordered |
| 6 | `LeaveRejected` | Session | Server to client | Session | Reliable ordered |
| 7 | `ServerDisconnect` | Session | Server to client | Session | Reliable ordered |
| 8 | `MovementInputBatch` | Movement | Client to server | Movement input | Sequenced |
| 9 | `SimulationSnapshot` | Replication | Server to client | Unreliable receive | Unreliable |
| 10 | `EntitySpawn` | Replication | Server to client | World | Reliable ordered |
| 11 | `EntityDespawn` | Replication | Server to client | World | Reliable ordered |
| 12 | `CarryStateChanged` | Items | Server to client | Owner | Reliable ordered |
| 13 | `ItemOperationIntent` | Items | Client to server | Owner | Reliable ordered |
| 14 | `ItemOperationResult` | Items | Server to client | Owner | Reliable ordered |
| 15 | `CorpsePresenceSnapshotChunk` | Corpses | Server to client | World | Reliable ordered |
| 16 | `CorpseInteractionIntent` | Corpses | Client to server | Owner | Reliable ordered |
| 17 | `CorpseInteractionResult` | Corpses | Server to client | Owner | Reliable ordered |
| 18 | `CorpseViewStateChunk` | Corpses | Server to client | Owner | Reliable ordered |
| 19 | `CorpseViewClosed` | Corpses | Server to client | Owner | Reliable ordered |
| 20 | `WorldActorSpawn` | WorldActors | Server to client | World | Reliable ordered |
| 21 | `WorldActorState` | WorldActors | Server to client | World | Reliable ordered |
| 22 | `WorldActorDespawn` | WorldActors | Server to client | World | Reliable ordered |
| 23 | `WorldInteractionIntent` | WorldActors | Client to server | Owner | Reliable ordered |
| 24 | `WorldInteractionOpened` | WorldActors | Server to client | Owner | Reliable ordered |
| 25 | `WorldInteractionResult` | WorldActors | Server to client | Owner | Reliable ordered |
| 26 | `WorldInteractionClosed` | WorldActors | Server to client | Owner | Reliable ordered |
| 27 | `OwnerSimulationSnapshot` | Movement | Server to client | Unreliable receive | Unreliable |
| 28 | `GatheringState` | WorldActors | Server to client | Owner | Reliable ordered |
| 29 | `CombatIntent` | Combat | Client to server | Owner | Reliable ordered |
| 30 | `CombatIntentResult` | Combat | Server to client | Owner | Reliable ordered |
| 31 | `CombatantState` | Combat | Server to client | World | Reliable ordered |
| 32 | `CombatEventBatch` | Combat | Server to client | World | Reliable ordered |
| 33 | `OwnerCombatState` | Combat | Server to client | World | Reliable ordered |
| 34 | `OwnerItemQuantityChanged` | Items | Server to client | Owner | Reliable ordered |
| 35 | `OwnerItemStateChanged` | Items | Server to client | Owner | Reliable ordered |
| 36 | `EntityAppearanceChanged` | Replication | Server to client | World | Reliable ordered |
| 37 | `AppearanceIntent` | Replication | Client to server | Owner | Reliable ordered |
| 38 | `AppearanceIntentResult` | Replication | Server to client | Owner | Reliable ordered |
| 39 | `AdminIntent` | Admin | Client to server | Owner | Reliable ordered |
| 40 | `AdminIntentResult` | Admin | Server to client | Owner | Reliable ordered |
| 41 | `AdminStateChanged` | Admin | Server to client | Owner | Reliable ordered |
| 42 | `QuestIntent` | Quests | Client to server | Owner | Reliable ordered |
| 43 | `QuestIntentResult` | Quests | Server to client | Owner | Reliable ordered |
| 44 | `QuestLogChanged` | Quests | Server to client | Owner | Reliable ordered |
| 45 | `QuestDropInCorpse` | Quests | Server to client | Owner | Reliable ordered |
| 46 | `GameServiceAvailabilityChanged` | Session | Server to client | Owner | Reliable ordered |

The session channel is 0, the movement input channel 1, the world channel 2,
and the owner channel 3, as [Channels](#channels) describes, and the client
receives unreliable packets on channel 0. SimulationWorker builds its route
table from the descriptors and sends every reliable message on the channel its
descriptor names, the Unity client and the bots check the channel and delivery
of every message against its descriptor, and
`RealtimeReplicationContractTests` checks that every packet the worker sends
travels as its descriptor says. The owner snapshot is small enough to stay
prioritized at the snapshot rate, while remote snapshots carry
application-level tick and chunk metadata.

## Channels

LiteNetLib orders a reliable channel only within itself, and one lost packet
holds back everything behind it on its channel until its resend arrives. The
reliable messages therefore travel on three reliable ordered channels, so that
a lost packet of one stream never holds back the others:

| Channel | Id | Carries |
| --- | ---: | --- |
| Session | 0 | `JoinRequest`, `JoinAccepted`, `JoinRejected`, `LeaveAccepted`, `LeaveRejected`, and `ServerDisconnect`: the join, the answers to a leave, and the message that says why a connection ends, which [The Reason In The Disconnect](#the-reason-in-the-disconnect) also puts into the disconnect |
| World | 2 | `EntitySpawn`, `EntityDespawn`, `EntityAppearanceChanged`, `WorldActorSpawn`, `WorldActorState`, `WorldActorDespawn`, `CorpsePresenceSnapshotChunk`, `CombatantState`, `CombatEventBatch`, and `OwnerCombatState`: what the client builds its picture of the World from, in order |
| Owner | 3 | Every intent and its answer, of items, corpses, world interaction, combat, looks, admin tools, and quests; the owner's state that they and the World change: `CarryStateChanged`, `OwnerItemStateChanged`, `OwnerItemQuantityChanged`, `CorpseViewStateChunk`, `CorpseViewClosed`, `WorldInteractionOpened`, `WorldInteractionClosed`, `GatheringState`, `QuestLogChanged`, `QuestDropInCorpse`, and `AdminStateChanged`; `GameServiceAvailabilityChanged`; and from the client `LeaveRequest` |

The rule for choosing a channel: a message goes on the channel of every
message that its receiver needs to have handled before it, so that a lost
packet can delay it but never put it before them. Messages without such a need
between them go on different channels. The dependencies are:

| Message | Needs before it, so on one channel with it |
| --- | --- |
| `CombatantState` | The `EntitySpawn` or `WorldActorSpawn` of its combatant: the client fails a state without presence |
| `EntityAppearanceChanged` | The `EntitySpawn` of its player: the client drops a look of a player it does not see, so a newer look must not arrive before an older spawn |
| `WorldActorState`, `WorldActorDespawn` | The spawn and the earlier states of the actor, by revision |
| `CombatEventBatch`, `OwnerCombatState` | The combatants and the combat they show, in simulation order |
| `CorpsePresenceSnapshotChunk` | The chunks of its own snapshot |
| `ItemOperationResult`, `CorpseInteractionResult`, `OwnerItemStateChanged`, `OwnerItemQuantityChanged`, `CarryStateChanged` | Each other: they carry the carry state, whose revision the client takes only ascending and fails when it goes back, and a result whose committed change has not arrived makes the client read its inventory again over HTTP |
| `WorldInteractionResult`, `GatheringState` | The `OwnerItemStateChanged` of the item-state revision they name, for the same reason, and the `WorldInteractionOpened` of their interaction |
| `WorldInteractionClosed` | The `WorldInteractionOpened` of its interaction, or the client would keep an interaction open that the worker closed |
| `QuestLogChanged`, `QuestIntentResult` | The `OwnerItemStateChanged` of the same operation, and the earlier quest log changes by revision |
| `CorpseViewStateChunk`, `CorpseViewClosed` | The `CorpseInteractionResult` that opened or changed the view, and the earlier chunks of the view |
| Every intent result | Its intent, and the answers to the intents sent before it |
| `GameServiceAvailabilityChanged` | The refusals with `game_service_unavailable` sent before it, which the client forgets when GameService answers again |
| `LeaveRequest` | The intents that the player sent before it, so the worker handles them before the leave |
| `LeaveAccepted`, `ServerDisconnect`, `JoinRejected` | Nothing: they end the session, so whatever the other channels still carry is dropped and never shown as a failure |

Three dependencies cross the channels and are resolved where the message
arrives instead:

- Every message of the world and owner channels needs `JoinAccepted`. Since
  a lost `JoinAccepted` lets them arrive first, the client and the bots hold
  such a message while the join waits for its answer and handle the held
  messages in the order they arrived right after it; at most 8,192, beyond
  which the join fails with `join_backlog_overflow`.
- `WorldInteractionOpened` names its actor and the actor's interaction
  revision, but the actor's newer state or its despawn can arrive first on the
  world channel. The client opens an interaction whose revision is older than
  the actor's, and opens nothing for an actor it no longer sees, without a
  failure; the worker closes such an interaction in order on the owner channel,
  because it closes every interaction whose actor went, stopped, or changed.
- Some owner messages name entities of the world channel, such as a combat
  target, a corpse of `QuestDropInCorpse`, or the look after an equip. The
  client shows them as they come and never fails on an entity it does not see.

At the end of a session, `LeaveAccepted` or `ServerDisconnect` can arrive
before world state or answers that the worker sent before it. The client drops
them, as it drops every message in the `Leaving` state and after the end, and
the worker disconnects once the session channel's message is acknowledged, so
LiteNetLib does not resend what the other channels still hold. Nothing durable
is lost: what GameService committed is read again on the next join.

## Session Messages

- `JoinRequest` carries the join ticket's public id, a client nonce, and the
  proof that the client holds the ticket's transport key, as
  [The Join](#the-join) describes; an empty ticket id is refused with
  `missing_join_ticket`. `JoinAccepted` carries the simulation
  session, account, and character identity, the Shard and World identity, the
  controlled entity id, the simulation and collision revisions, the join and
  session expiry times, whether the join was a reconnect, the carry state, the
  movement settings, the initial player state, and `ShardRules`, so the client
  knows whether a click on another character attacks.
- `LeaveRequest` names the simulation session to leave. `LeaveAccepted`
  carries `BodySeconds`, 0 to 3,600: how many seconds the character's body
  stays in the world after the leave, longer while it is attacked, or 0 when it
  left the world at once. The client returns to character select either way, as
  [Lingering Bodies](SERVICE_FEATURES.md#lingering-bodies) describes.
- `GameServiceAvailabilityChanged` (46, protocol 41) carries `Available`,
  whether GameService answers the worker. The worker sends it to every joined
  peer once per change of its availability, and right after `JoinAccepted` to a
  peer that joins or reconnects while GameService does not answer; a joined
  peer that has heard nothing takes GameService as answering. While it does
  not, the worker refuses items, trade, and quests with
  `game_service_unavailable`, and the client shows that saving is paused, as
  [GameService Outage](SERVICE_FEATURES.md#gameservice-outage) describes. The
  client handles it while joined, drops it while leaving, and holds it while a
  join waits for its answer, as [Channels](#channels) describes.
- `JoinRejected`, `LeaveRejected`, and `ServerDisconnect` carry an `Error`: a
  code of up to `MaximumErrorCodeLength` (64) characters and a message of up to
  `MaximumErrorMessageLength` (512). Decoders accept blank text, and the
  receiving code decides what it means.
- The code of an `Error`, of a closure, or of a notice also chooses the text
  that the client shows, as [Messages](../content/README.md#messages)
  describes. The message next to a code is an English explanation for logs,
  which no player reads. A message whose text needs values carries them next to
  its code as up to `MaximumMessageArguments` (4) `MessageArgument` entries:
  a `Name`, which is the placeholder of the client's text, a letter followed by
  letters and digits, a `Kind`, and a `Value`. `Kind` `Text` shows the value as
  it is, such as a number, and `TextKey` makes it a key of the client's text
  tables, such as the name of a tool kind. One message has no two arguments of
  the same name.
- Error codes, closure codes, and despawn reasons are ordinary text up to the
  error-code limit, so a new code needs no new protocol version. The stable
  codes are listed in [Realtime Codes](SERVICE_FEATURES.md#realtime-codes).
  `EntityDespawn.Reason` and `WorldActorDespawn.Reason` are informational, such
  as `out_of_interest`, `left_shard`, `teleported`, or `died`.

### The Reason In The Disconnect

A connection that the worker ends carries the message that says why twice: as a
reliable message on the session channel, and as the data of LiteNetLib's
disconnect (`NetManager.DisconnectPeer(peer, data)`, which the client reads as
`DisconnectInfo.AdditionalData`). The data is one whole encoded packet, frame
header included, of `LeaveAccepted`, `ServerDisconnect`, or `JoinRejected`, the
message the worker sent reliably just before, and the stop notice of a stopping
worker is such a `ServerDisconnect`. LiteNetLib sends the disconnect again every
300 ms until the client confirms it, and stops resending the reliable packets
that still wait for their acknowledgement, so the data is what reaches the
client when the reliable copy was lost or held back behind a lost packet. How
long the worker waits before it disconnects is in
[Realtime UDP Transport](SERVICE_FEATURES.md#realtime-udp-transport). A peer
whose keys the worker has not installed yet drops every clear packet on a
channel, as [When Sealing Starts](#when-sealing-starts) describes, so the worker
sends it the message only as the data of the disconnect, which follows at once,
such as a `JoinRejected` for a ticket that GameService refused.

The data is at most 1,014 bytes: LiteNetLib puts it into one disconnect packet
behind a 9-byte header, and attaches it only while that packet stays below the
MTU of 1,024 bytes, which never grows because MTU discovery is off. Above the
limit it would deliver zero bytes of the same length instead. `LeaveAccepted`
always fits; a `ServerDisconnect` or `JoinRejected` that does not fit travels in
the data with its code whole and its log message cut to fit, while the reliable
copy keeps the whole message.

A client that gets a disconnect with data handles the data exactly as if the
message had arrived on its channel, but only while it has not handled
that message yet; the client and the bots accept only the three message types
there and treat any other data as a disconnect without data.

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
it. The worker may take steps ahead of a late input along the current path, a
few or, through a silence of the line, up to its path continuation, as
[Server-Authoritative Movement](SERVICE_FEATURES.md#server-authoritative-movement)
describes; those steps never appear in the owner snapshot until their input
arrived, so the client replays exactly the inputs after the acknowledged one.
Remote `SimulationSnapshot` records carry the current state, including such
steps.

`OwnerSimulationSnapshot` ends with `MovementPathProgress`: the followed path id,
the next waypoint index, at most 32, which equals the waypoint count once the
path ended, and the consecutive ticks in which collision blocked the path.
Without a path, the index and the blocked ticks must be 0. A remote
`EntitySnapshot` carries no velocity and no path data, as
[Remote Entity Record](#remote-entity-record) describes. `MovementSettings`
carries the tick and snapshot rates, the walk speed, turn rate, falling, the
World bounds and ground height, and the character capsule and motor settings
that the client predicts with.

## Replication Messages

`EntitySpawn` announces a player entity with its kind, persistent id, display
name, archetype, server tick, initial state, and look. It and the
`WorldActorSpawn` and `CombatantState` that follow it travel on the world
channel in bursts that a budget per connection bounds, so a crowd that enters a
view can never fill the reliable queue that disconnects a peer, as
[Interest Management](SERVICE_FEATURES.md#interest-management) describes. A
remote simulation snapshot chunk carries the snapshot sequence, server tick,
chunk index and count, and up to `MaximumSnapshotEntitiesPerChunk` (71) `EntitySnapshot`
records, as many as fit the 1,023-byte unreliable packet: the 20 bytes of the
frame header and chunk fields, and 14 bytes per record.

### Remote Entity Record

An `EntitySnapshot` carries only what a client shows of another entity,
quantized, in 14 bytes:

| Bytes | Field | Encoding |
| ---: | --- | --- |
| 4 | `EntityId` | `u32`, at least 1; every other message carries the same id in a `u64` |
| 3 | `QuantizedX` | `i24` in steps of 1/128 m |
| 2 | `QuantizedY` | `i16` in steps of 1/64 m |
| 3 | `QuantizedZ` | `i24` in steps of 1/128 m |
| 2 | `Pose` | `bits16`: `YawStep` in the low 10 bits, in steps of 360/1024 degrees, then `IsGrounded`; the other 5 bits must be 0 |

The precision is in the hand-written
`Replication/RealtimeProtocol.Replication.cs`. The worker rounds every value to
the nearest step and clamps it to the range: x and z reach from -65,536 m to
65,535.99 m in steps of 7.8 mm, y from -512 m to 511.98 m in steps of 15.6 mm,
and the yaw any facing in steps of 0.35 degrees. A client multiplies back with
`PositionX`, `PositionY`, `PositionZ`, and `YawDegrees`, so it shows a position
within half a step of the worker's. A World manifest refuses bounds beyond
65,535 m on x and z and a ground height or spawn beyond 511 m, as
`WorldManifestValidator` checks. The worker gives out entity ids from 1 up to
4,294,967,295 and refuses more. The record carries no velocity and no input
sequence: the client derives the velocity it needs from the positions it
interpolates, as
[Remote Players](UNITY_CLIENT_ARCHITECTURE.md#remote-players) describes.

A remote snapshot carries a record of an entity only when it is due for that
recipient, as
[Entity Registry And Replication](SERVICE_FEATURES.md#entity-registry-and-replication)
describes: when the entity entered the view, changed, stopped changing a few
snapshots before, or waits for its refresh, at least about once a second. So
the records of one entity arrive at uneven intervals, a record at the same
position as the one before says the entity stands there, and a client keeps
showing an entity it got no record of. The recipient's own entity is never in
it; its owner snapshot carries it. A recipient with nothing due gets no remote
snapshot.

A Mob that can walk travels in `SimulationSnapshot` like a remote player: an
`EntitySnapshot` with the Mob's world actor entity id. The first tick it stands
still sends one reliable `WorldActorState` with its rest pose and a newer state
revision. Clients skip snapshot entities they do not know.

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
  every connection that sees the player and to its owner, at once within the
  detail radius and as the newest look once a second beyond it, as
  [Detail Radius](SERVICE_FEATURES.md#detail-radius) describes. The owner gets
  its own look after the join, since it never receives a spawn of its own
  player. The worker sends it when the look, the visible equipment, the
  headwear, or the tool in use changed, and the client drops a change for a
  player it does not see.
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
  revision, and the variant fields of its kind. `Equip` and `MergeStacks` carry
  a `Quantity` last, the part of the stack they move, or 0 for the whole stack;
  `SplitStack` requires one of at least 1. `CorpseInteractionIntent`
  `LootToEquipment` likewise carries a `Quantity` last, 0 for the whole stack.
  `ItemOperationResult` (14)
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
other than by a claim, and fits one packet; the client reloads its inventory in every other case. It always goes out before
the carry state and the result of the operation that caused it.

## Corpse Messages

- `CorpsePresenceSnapshotChunk` (15) carries the nearby corpses in chunks of up
  to `MaximumCorpsePresencePerChunk` (6) `CorpsePresence` structs, each with its
  id, position, facing, absolute expiry, whether it is empty, whether the
  recipient may loot it, source, presentation key, and optional look. A
  Mob's corpse belongs to the player the Mob credited, so `IsLootable` differs
  between recipients.
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

### Corpse Source

`CorpsePresence` and `CorpseViewStateChunk` carry a `CorpseSource`, which says
who left the corpse with exactly one of two optional texts: `CharacterName`,
the name the dead character had, or `ActorDefinitionId`, the actor definition
of the dead Mob. The messages carry no name of a Mob: the client finds it in
its text tables by the definition, as
[Text Tables](../content/README.md#text-tables) describes. A source with both
texts or with neither is rejected in both directions. `QuestDropInCorpse`
names its Mob by `SourceActorDefinitionId` in the same way.

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
  kind, faction, disposition, level, aggression, presentation
  archetype, state and interaction revisions, active flag, position, facing,
  activity tier, and interaction bounds; the spawn also carries the tick of its
  pose and whether a Mob walks, as
  [World Actor Spawn Pose](#world-actor-spawn-pose) describes. A state update
  goes out only when replicated state changes, and a resource node's available
  or depleted state reuses `IsActive`. A viewer beyond the detail radius gets
  only the newest state, once a second, except a change of `IsActive`, as
  [Detail Radius](SERVICE_FEATURES.md#detail-radius) describes.
- `WorldInteractionIntent` (23) opens, closes, or acts on an interaction. Every
  intent carries an operation id, the simulation session, the interaction
  session, the target kind, entity id, and runtime id, and the expected target
  revision. A `CapabilityAction` also carries the capability id, kind, and
  expected revision, the action, and the action's fields.
- `WorldInteractionOpened` (24) carries the interaction session, the target, the
  capability summary revision, and the player-specific capability summary: every
  `WorldActorCapability` with its id, kind, `ProfileId`, availability, and
  revision. `ProfileId` names the item catalog vendor profile
  of a `Vendor` capability, the resource profile of a `Gathering` capability,
  and is empty for every other kind.
- `WorldInteractionResult` (25) correlates an operation by id and carries the
  target and item-state revisions, a `NoticeCode`, the message arguments, and
  an `Error` on failure. The notice code names the text for a committed
  operation, such as `vendor_purchased`, and is empty when nothing needs
  saying; a failed result has none. The arguments belong to the notice of a
  committed operation or to the error of a failed one.
  `WorldInteractionClosed` (26) names a server-initiated closure with a stable
  code and a message.

Strings and lists are bounded. Large future capability state must use explicit
chunking or a focused service read rather than an oversized actor packet.

No world actor message carries player-facing text. An actor's name and examine
text and the name and menu verb of each capability are in the client's text
tables, and the client finds them by the actor definition id and the capability
id that these messages carry, as
[Text Tables](../content/README.md#text-tables) describes.

### World Actor Spawns And Menu Options

`WorldActorSpawn` ends with `MenuOptions` for the client's right-click world
menu: up to `MaximumWorldActorCapabilities` (16) `WorldActorMenuOption` structs
with distinct `CapabilityId` values, in the actor's authored capability order.
Each option names a capability and its `WorldActorCapabilityKind`. The spawn
carries no availability, revision, or profile: those stay in the
player-specific capability summary of `WorldInteractionOpened`.

### World Actor Level And Aggression

`WorldActorSpawn` carries what a Mob's nameplate shows besides its name and
health. `Level` is a `u16` of at most `MaximumWorldActorLevel` (10,000), the
Mob's authored level, and 0 for an NPC or resource node.
`WorldActorAggression` is `Aggressive` for a Mob that attacks the players it
detects, `Defensive` for one that fights only once damaged, and `None` for an
actor without a brain. Both are static for the actor's lifetime, so a changed
value without a despawn is an identity conflict. The level limit is public, and
`WorldActorCombatRules.MaximumLevel` in WorldData must fit it.

### World Actor Spawn Pose

Since protocol 45 `WorldActorSpawn` says what a client cannot know about a Mob
that comes into view while it walks. After `ActivityTier` it carries:

- `PoseTick` (`u32`): the server tick at which the worker read the pose, as
  `EntitySpawn.ServerTick` does for a player. It need not be a tick that a
  snapshot names: the interest refresh sends its spawns on the tick before a
  snapshot tick.
- `IsMoving` (`bool`): whether the Mob walks at that tick, so its pose is not a
  rest pose. The hand-written rule accepts it only for an active Mob; an NPC, a
  resource node, and a hidden Mob send false.

A spawn that walks starts the Mob from its pose as a first state that says
nothing about standing, as
[Mob Movement](UNITY_CLIENT_ARCHITECTURE.md#mob-movement) describes; any other
spawn pose is a rest pose. `WorldActorState` carries neither, because a client
takes every newer state as a rest pose.

### Capability Actions

A `WorldInteractionIntent` of kind `CapabilityAction` carries a
`CapabilityActionKind`: `None` (0) opens the capability, `ApplyInsurance` (1)
and `RemoveInsurance` (2) are the insurance actions, `VendorPurchase` (5),
`VendorSale` (6), and `StartGathering` (7) trade and gather, and `AcceptQuest`
(3), `RegrantQuestItems` (8), `TalkToQuestNpc` (9), `DeliverQuestItems` (10),
and `TurnInQuest` (11) are the actions of a `Quests` (11) capability. The action
also carries the expected character revision, an item, a destination,
`OfferDefinitionId`, `Quantity`, `ExpectedPrice`, `QuestId`,
`QuestObjectiveId`, and `RewardChoiceDefinitionId`. The `QuestOffer` (3) and
`QuestTurnIn` (4) capability kinds and the `AbandonQuest` (4) action that came
before quest content are retired, so those values stay unused. With every text
at its limit the intent can exceed the packet limit, so it sets
`runtimeSizeCheck`; content identifiers are ASCII and always fit. The
hand-written rules accept only the fields each action uses:

- `VendorPurchase` targets a `Vendor` capability and needs an offer definition,
  a quantity of at least 1, and an expected price of 0 or more, with no item and
  no destination.
- `VendorSale` targets a `Vendor` capability and needs an item, a quantity of
  at least 1, and an expected price, with no offer definition and no
  destination.
- `StartGathering` targets a `Gathering` capability and carries no character
  revision, item, destination, or vendor terms.
- A quest action targets a `Quests` capability and names its quest, with no
  character revision, item, destination, or vendor terms, because GameService
  places and takes the items. `TalkToQuestNpc` and `DeliverQuestItems` also name
  the objective, `TurnInQuest` may name a reward choice, and the other quest
  actions name neither. Every other action carries no quest fields.

The expected price is the total the player was shown; GameService rejects a
different current price with `vendor_price_changed`.

### Gathering State

`GatheringState` (28) reports gathering at a resource node to the gathering
player, keyed by the interaction session and the node's entity and runtime id.
Its `Kind` is `Started` with the cycle time in milliseconds, `Yielded` with the
cycle time, the yield definition and quantity, and the item-state revision
after the yield, or `Stopped` with a stable code, a message, and the message
arguments of the code, which name the tool kind when the tool is missing. A `Yielded` or
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
  (`Alive`, `Dead`, or `Evading`), combat flag, attack target,
  `TaggedByEntityId`, and health regeneration of a visible combatant with a
  revision that only grows. `TaggedByEntityId` names the player who tagged a
  Mob, or is 0. It follows every `EntitySpawn` and `WorldActorSpawn` of a
  combatant, so a client never shows a combatant without health. A dead
  combatant has 0 health, no target, and no regeneration, and nobody targets or
  tags itself. A viewer beyond the detail radius gets only the newest state,
  once a second, except a change of the life state, so a client cannot count
  on seeing every revision, as
  [Detail Radius](SERVICE_FEATURES.md#detail-radius) describes.
  - Since protocol 44 the state describes the health between states:
    `HealthRegenerationAmount` (i32, at least 0) is the health the combatant
    gains at `HealthRegenerationTick` (u32, a server tick) and again every
    second after it, up to `MaximumHealth`, while the state stands. Without a
    regeneration both are 0; the hand-written rule rejects a tick without an
    amount, and tick 0 with an amount is a tick like any other after the
    server tick wrapped.
  - `RealtimeCombatHealth.At(state, serverTick, tickRateHz)` is the one rule
    that every receiver follows: the state's `Health` plus the amount for every
    step whose tick `serverTick` reached, one step every `tickRateHz` ticks,
    capped at the maximum; the state's own health when the amount is 0, the
    combatant is not alive, or the tick rate is not positive. It compares ticks
    across the wrap like the worker. The client reads no combat rule for it, so
    a later rate per combatant needs no client change.
- `CombatEventBatch` (32) carries the events of one server tick that involve a
  combatant the recipient sees within the detail radius or controls, and the
  deaths of every combatant it sees, in simulation order. A recipient can
  therefore get an attack's release or hit without its start, or a start
  without its release, when the attacker crosses the detail radius. Every
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

## Quest Messages

The Quests family carries the quest actions outside an interaction and the
changes of the owner's quest log, as [Quests](SERVICE_FEATURES.md#quests)
describes.

- `QuestIntent` (42) carries an operation id and a `QuestIntentKind` with the
  fields of its case: `Abandon` (1) and the Admin menu's development tools
  `AdminAccept` (2), `AdminCompleteStep` (4), `AdminMakeReady` (5),
  `AdminReset` (6), and `AdminResetChain` (7) name the quest,
  `AdminCompleteObjective` (3) the quest and the objective, and
  `AdminResetAll` (8) nothing. Only a worker with the admin tools carries out
  the Admin kinds.
- `QuestIntentResult` (43) echoes the operation id and kind and carries the
  item-state revision after the intent, and an `Error` when it failed.
- `QuestLogChanged` (44) lists every quest that one committed change of the
  owner touched, as the quest log holds it after the change: up to
  `MaximumQuestLogChanges` (8) distinct `QuestChange` structs, each with the
  quest, its current step as optional text, which is empty once the quest is no
  longer active, up to `MaximumQuestObjectives` (8) distinct
  `QuestObjectiveProgress` structs of the step's counted objectives with a
  progress from 0 to `MaximumQuestObjectiveProgress` (1,000), and the completion
  count. It also carries the operation that caused the change, or empty, the
  `PreviousQuestLogRevision` the change applies to, and the `QuestLogRevision`
  after it, which the hand-written rule requires to be higher. A change that
  one packet cannot list sets `ReloadRequired` and lists no quest, and the
  client then reads its quest log again; a quest without a step has no progress.
  The message can exceed the packet limit, so it sets `runtimeSizeCheck`, and
  `RealtimeProtocol.CanEncodeQuestLogChanged` lets the worker check a change.
  It goes out after the operation's `OwnerItemStateChanged` and before its
  result. A client whose quest log revision differs from the previous revision
  reads its quest log again instead of patching it.
- `QuestDropInCorpse` (45) tells the owner of a new Mob corpse about one quest
  item that the corpse holds for them: the corpse, the Mob's name of up to
  `MaximumCorpseSourceNameLength` (128) characters, the quest, the item
  definition, and a quantity of at least 1. The worker sends one for every
  quest drop once the corpse exists, live or durable, and none to an owner who
  is not connected; the corpse view shows the items as usual.

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
  not handle. Give every new client-to-server type a route in SimulationWorker,
  as [Registering A Realtime Feature](PROJECT_ARCHITECTURE.md#registering-a-realtime-feature)
  describes; `RealtimeFeatureRegistrationTests` fails for a type without one.
- The connection key changes only when the framing of the datagrams changes,
  such as the marker and seal of [Transport Security](#transport-security), so
  a peer of another framing is refused at the connect, while a mismatch of the
  protocol version alone is reported as a clear protocol error instead of a
  refused connection. A client never connects to a worker of another version,
  because it checks the placement first.

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
hand-written `SatisfiesRules` overload or the precision of the remote entity
record: a change there that alters what a peer accepts or what a value means
needs a version bump by hand.

Content that travels as free text, such as player archetypes, corpse
presentation keys, error and closure codes, and despawn reasons, can grow
without a new version, as long as it fits the field's limit.

## Golden Tests

The golden cases pin the exact bytes of every message type and every accept or
reject decision of the current protocol version in both .NET and Unity. They are
described in [Backend Tests](../tests/README.md#realtime-protocol-goldens).
