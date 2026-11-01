# Service Features

This document owns the implemented server behavior as seen from outside the
processes: the GameService HTTP API and its authentication, the error codes,
the timings, limits, defaults, and configuration options, persistence and
maintenance, the implemented behavior of each server area, and what is not
implemented yet.

The topology and the boundaries behind this behavior are in
[Project Architecture](PROJECT_ARCHITECTURE.md), the GameService module
internals in [GameService Modules](GAME_SERVICE_MODULES.md), the wire format in
[Realtime Protocol](REALTIME_PROTOCOL.md), and the meters and logs in
[Monitoring](MONITORING.md). The game rules are in [Game Design](GAME_DESIGN.md),
the item rules in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md), the
world actor model in [NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md),
and what a player sees in [Game Features](GAME_FEATURES.md).

## GameService HTTP Surface

| Route | Authority | Behavior |
| --- | --- | --- |
| `GET /` | Public | Redirect to `/health/ready` |
| `POST /api/accounts/register` | Public, rate limited | Create an account and its login session |
| `POST /api/accounts/login` | Public, rate limited | Replace the active login session |
| `GET /api/accounts/me` | Account session | Return the account profile |
| `GET /api/accounts/session` | Account session | Validate the current session |
| `POST /api/accounts/logout` | Account session | Revoke the current session and its dependent access |
| `DELETE /api/accounts/sessions/{sessionId}` | Account session | Revoke an owned session |
| `GET /api/characters` | Account session | List owned characters with their body models |
| `POST /api/characters` | Account session | Create a character with its body model and look, checked against `content/characters/appearance.json`, or with the default look of the default body model when the request names none |
| `GET /api/character-appearance` | Account session | Return what character creation offers: the kinds of a look with their rules and every body model with its options per kind and slot |
| `GET /api/item-catalog` | Account session | Return the neutral current catalog with revision ETag validation |
| `GET /api/characters/{characterId}/item-state` | Owning account session | Return one coherent no-store character item-state snapshot, including the currency balance |
| `GET /api/characters/{characterId}/bank` | Owning account session | Return focused no-store bank state |
| `GET /api/characters/{characterId}/recovery` | Owning account session | Return focused no-store unclaimed Recovery Storage deliveries |
| `POST /api/characters/{characterId}/item-operations/relocate` | Owning offline account session | Relocate an item through the durable transaction kernel. Bank and Recovery Storage are never reachable offline |
| `POST /api/characters/{characterId}/item-operations/split` | Owning offline account session | Split a stack into an exact destination slot |
| `POST /api/characters/{characterId}/item-operations/merge` | Owning offline account session | Merge compatible stacks |
| `POST /api/characters/{characterId}/item-operations/destroy` | Owning offline account session | Perform allowed player destruction with a server-owned reason |
| `POST /api/items/secure-container-tier` | Offline account session | Return the account to the base Secure Container tier with every character revision. A larger tier needs a trusted entitlement source, so this route returns `403` with `item_authority_required` for it |
| `GET /api/shards` | Public | List logical shards, status, players, and capacity |
| `POST /api/shards/{shardId}/join` | Account session | Place an owned character and issue a ticket |
| `POST /api/simulation-workers/{workerId}/heartbeat` | Worker service policy | Register or renew the exact worker runtime and return the lease and the Shard's rule set |
| `POST /api/simulation-workers/{workerId}/offline` | Worker service policy | Release the exact worker runtime's authority |
| `POST /api/simulation-join-tickets/consume` | Worker service policy | Consume an exact-runtime join ticket |
| `POST /api/simulation-sessions/{id}/heartbeat` | Worker service policy | Renew an exact simulation lease |
| `POST /api/simulation-sessions/{id}/release` | Worker service policy | Release an exact simulation lease |
| `POST /api/simulation-sessions/{id}/appearance` | Worker service policy | Lower or raise the exact-session character's headwear, or in the Development environment only store another body model and look |
| `POST /api/simulation-sessions/{id}/item-operations` | Worker service policy | Execute one exact-session item mutation, Recovery claim, insurance or quest item action, vendor trade, or gathering yield through the durable kernel |
| `POST /api/simulation-sessions/{id}/ammunition-consumptions` | Worker service policy | Commit one idempotent batch of shots from the exact-session character's equipped ammunition stack |
| `POST /api/simulation-sessions/{id}/player-deaths` | Worker service policy | Partition one exact-session authoritative player death into durable corpse and Recovery custody |
| `GET /api/simulation-workers/{workerId}/corpses` | Exact worker service policy | Restore open unexpired corpses for the worker's fresh runtime and active Shard assignment |
| `POST /api/simulation-workers/{workerId}/mob-corpses/durable` | Exact worker service policy | Create one durable Mob corpse for the worker's runtime and Shard, idempotent by the operation id the worker derives from the death event |
| `POST /api/simulation-sessions/{id}/corpses/{corpseId}/open` | Worker service policy | Read one exact-session, unexpired, same-Shard corpse snapshot after worker proximity validation |
| `POST /api/simulation-sessions/{id}/corpses/{corpseId}/item-operations` | Worker service policy | Commit bidirectional full or partial corpse transfers, whole-item loot into an equipment slot, a corpse Bag into an empty Bag slot, Loot all, and atomic ordinary, equipment, or Bag aggregate swaps through the durable kernel |
| `POST /api/simulation-sessions/{id}/mob-loot-grants` | Worker service policy | Grant one live Mob loot entry into the slot or stack the client named, idempotent by its grant id |
| `POST /api/simulation-sessions/{id}/mob-loot-grants/loot-all` | Worker service policy | Grant every live Mob loot entry that fits, whole, in one transaction, and name every grant that left the corpse |
| `POST /api/development/simulation-bots/join-tickets` | Development loopback secret | Issue an in-memory exact-runtime bot ticket when explicitly enabled |
| `POST /api/development/stack-stress/characters/{characterId}/inventory-fixture` | Account session and Development loopback secret | Seed the starting items of a stack stress inventory workload for an owned empty character when explicitly enabled |
| `POST /api/development/stack-stress/loot-hotspot` | Account session and Development loopback secret | Create the shared loot corpse of a stack stress workload when explicitly enabled |
| `GET /health/live` | Public | Report process liveness |
| `GET /health/ready` | Public | Verify obligatory dependencies |

Route parameters named `characterId`, `sessionId`, `simulationSessionId`, and
`corpseId` must be GUIDs. The public shard list never exposes a
SimulationWorker host, port, or runtime id. That endpoint appears only in a
successful short-lived placement response.

The development bot route is absent unless GameService runs in `Development`
with `DevelopmentSimulationBots:Enabled=true`. It also requires a loopback
caller and the separate `ACTIVE_SIMULATION_BOTS_SECRET`. Bot identities,
tickets, and sessions remain in memory and are never durable account or
character data. SimulationWorker uses the same worker service routes for real
and bot credentials; GameService routes recognized bot credentials to the
in-memory authority and every other request to the PostgreSQL services. The
guards, the ticket rules, and the connections kept for real players are
described in
[Architecture And Safety Boundary](ACTIVE_SIMULATION_BOTS.md#architecture-and-safety-boundary).

The stack stress fixture routes are absent unless GameService runs in
`Development` with `StackStressFixtures:Enabled=true`. They also require a
loopback caller, the `X-Stack-Stress-Fixture-Key` header with
`STACK_STRESS_FIXTURE_SECRET`, a loopback PostgreSQL host, and a database name
that contains `stress` or `test`; startup fails when an enabled fixture lacks
any of these. They grant only the item definitions that configuration names,
through the item transaction kernel. [Stack Stress Testing](STACK_STRESS_TESTING.md)
uses them.

## Accounts And Authentication

- Email and username are normalized and unique. An email must be a plain
  address; display-name forms such as `Name <a@b.c>` are rejected.
- Passwords are hashed with BCrypt at work factor 12. A password must be 8 to
  128 characters and at most 72 UTF-8 bytes, because BCrypt ignores later bytes.
- Login releases its database connection before the password check, and an
  unknown login is checked against a fixed hash, so response time does not show
  whether an account exists. Registration still reports whether an email or
  username is taken until an email verification flow exists; its rate limit
  bounds enumeration.
- Account tokens are opaque and only their SHA-256 hashes are stored.
- PostgreSQL enforces one unrevoked account session per account. A session
  lives `Auth:SessionLifetimeHours`.
- A successful login locks the account, revokes the older session with the
  stored reason `session_replaced` and the client error code
  `account_session_replaced`, consumes its pending tickets, releases its active
  simulation sessions, and creates the replacement session transactionally.
- Logout and manual revoke use explicit revocation reasons and dependent cleanup.
  They lock the account first, like login and join, and clean up only sessions
  that belong to the caller. Revoking another account's session id succeeds
  without effect, so foreign session ids are not revealed.

Login and registration use independent fixed-window limits keyed by remote IP,
`RateLimiting:Authentication:PermitLimit` requests per
`RateLimiting:Authentication:WindowSeconds`. Rejected requests return HTTP 429
with `rate_limit_exceeded` in Problem Details and a `Retry-After` that counts
down to the end of the current window. The policy is taken from the matched
endpoint, so a different path casing such as `/api/accounts/LOGIN` cannot skip
the distributed limit.

## Character Access

- Character names are validated and normalized.
- Character ownership is checked on every protected operation.
- The configured maximum character count is enforced transactionally.
- Soft-deleted characters are excluded from active queries.
- Simulation placement locks both account and character rows.

PostgreSQL permits only one active simulation session per character and one per
account. A copied account token therefore cannot run two different characters
at the same time.

## Character Currency

- `characters.currency` is the one balance of a character; the currency rules
  are in
  [Character Currency](INVENTORY_AND_DEATH_LOOT_DESIGN.md#character-currency).
  Every change goes through the Characters wallet in the caller's transaction
  and writes one ledger row, as
  [Character Wallet](GAME_SERVICE_MODULES.md#character-wallet) describes.
- A debit that the balance does not cover is rejected with
  `currency_insufficient`, and a credit above the maximum balance with
  `currency_limit_exceeded`. Neither changes the balance.
- New characters receive the catalog's starting balance with a
  `starting_balance` ledger row in the character creation transaction.
- The item-state snapshot carries the balance from the same repeatable-read
  snapshot as the items, and every in-world currency change runs inside an item
  transaction, so it advances the item-state revision that clients refresh on.
- A player death never reads or writes currency.
- `--dev-items-currency` credits an offline character for local testing, as
  [Development Item Commands](#development-item-commands) describes.

## Topology Bootstrap

`src/Server/GameService/Config/appsettings.json` owns the checked-in topology
bootstrap in `Simulation:Topology`: Fleets with display names and region codes,
Nodes assigned to Fleets, and Shards assigned to a Fleet with a display name
and a rule set. World definitions come from the World manifests below
`WorldData:WorldsPath`, not from the topology, and no Shard names a World.

Startup validation rejects empty collections, invalid identifiers, duplicates,
unknown references, invalid display names, invalid region codes, and missing
rule sets. After migrations, `SimulationTopologySeeder` idempotently mirrors the
World definitions and upserts the configured records. It does not manufacture
online workers. A seeded shard remains offline until a valid heartbeat and
active assignment exist. The checked-in topology seeds two shards in
`local-fleet` with the rule set `mvp-open-risk`: `local-shard-1`
(`Local Shard 1`), which the default worker binds to Development World 2, and
`local-shard-2` (`Local Stress Arena`), which the SimulationWorker `StressArena`
launch profile binds to Development World 1.

A worker heartbeat binds a shard to the worker's configured `WorldId`, and a
different `WorldId` rebinds it only under the rules in
[Topology Model](PROJECT_ARCHITECTURE.md#topology-model). An unknown World or a
blocked rebind is a definitive worker authority failure:
`simulation_world_not_found` or `shard_world_rebind_blocked`. Before retrying a
rebind, stop the worker cleanly and drain the World-local state, as
[Rebind An Offline Shard](LOCAL_DEVELOPMENT.md#rebind-an-offline-shard-to-another-world)
describes. The next worker runtime must report the rebound World identity, so
stale content cannot silently simulate the shard. A migration placeholder
assignment with no worker runtime is not simulation authority and therefore
does not block a rebind.

## Worker Registration And Assignment

Each SimulationWorker has:

- A stable `SimulationWorkerId`.
- A generated `RuntimeId` for the current process generation.
- Fleet, Node, Shard, and World configuration.
- An advertised UDP endpoint.
- Maximum and active connection counts.
- Protocol, simulation, collision, and item catalog revisions.
- A process start timestamp.

GameService validates identifiers, topology consistency, endpoint metadata,
capacity, revisions, and service identity. The heartbeat transaction locks the
target shard and applies the rules in
[Runtime Identity](PROJECT_ARCHITECTURE.md#runtime-identity-and-assignment-safety):

- A different healthy assigned worker causes `shard_assignment_conflict`.
- A timed-out or explicitly offline owner can be replaced.
- Replacement invalidates old-runtime tickets and simulation sessions even when
  the logical worker moves to another shard.
- Worker secrets are matched to the exact, case-sensitive worker id.
- The older runtime is fenced by `worker_runtime_changed`.
- A stale process cannot mark a newer runtime offline.
- Active assignment indexes prevent two worker or shard owners.

A worker is healthy while its heartbeats arrive within
`Simulation:WorkerHeartbeatTimeoutSeconds`. It heartbeats every
`SimulationWorker:RegistryHeartbeatSeconds`, and a new runtime keeps retrying
for `SimulationWorker:RegistryAcquireTimeoutSeconds` while a previous runtime's
lease is still live.

Every heartbeat response also names the rule set of the Shard. SimulationWorker
applies the rule set with that id from its combat rules and stops with
`shard_rule_set_unknown` when its rules do not define it.

SimulationWorker stores the database-issued lease duration locally, measured
from the moment the heartbeat request was sent. It rejects new UDP joins without
a valid registration lease. A one-second monitor stops the process after lease
expiry. Definitive topology, assignment, runtime, or service credential
rejection also stops the process.

## Shard Discovery And Placement

`GET /api/shards` derives status from fresh, online, actively assigned workers.
It aggregates active players and maximum capacity. A shard is online only when
at least one assigned worker is fresh and has free capacity. The unique
assignment constraint means that aggregate contains at most one worker.

Join placement:

1. Validates account session and character ownership.
2. Locks the account and character.
3. Releases expired account simulation sessions.
4. Rejects another active character or cross-shard active session with
   `account_character_already_active` or `character_already_active`.
5. Invalidates older pending tickets for the account, so a retried join is not
   blocked by its own earlier ticket.
6. Selects the assigned worker only when its heartbeat is fresh and it has room,
   or rejects with `shard_unavailable`; a reconnect whose owning worker is
   unavailable gets `simulation_session_worker_unavailable`. Capacity uses the
   greater of worker-reported connections and authoritative unexpired simulation
   sessions, then adds pending tickets in PostgreSQL. A reconnect ticket for an
   already counted session is not added again. In Development, the in-memory
   sessions and pending tickets of synthetic bots are joined into the same
   query, so players and bots never reserve the same connections.
7. Re-reads the assignment after locking the worker, so a worker that moved to
   another Shard in the meantime is never placed.
8. Rejects the join, and a reconnect to the owning worker, with `409`
   `simulation_worker_item_catalog_mismatch` when the worker's last heartbeat
   reported another item catalog revision than the one GameService mirrors.
   Combat data and Mob loot come from the worker's catalog and item custody
   from GameService's, so both must be the same catalog.
9. Stores a hash of a new ticket bound to shard, worker, runtime, character, and
   account session.
10. Computes expiry with `clock_timestamp()` after placement locks are acquired.
    Ticket consumption and session leases use the same clock, so time spent
    waiting for a lock never shortens or extends a ticket or lease.
11. Returns the shard and exact endpoint metadata.

Selection serializes reservations on the target shard, then locks the selected
worker. A waiting request recalculates session and ticket usage from a fresh
database snapshot before reserving capacity. Different shards remain parallel.

## Join Tickets And Simulation Sessions

Join tickets live `Simulation:JoinTicketLifetimeSeconds`, are used once, and are
stored only as hashes. A worker must present the ticket together with its own
worker id, runtime id, and shard id.

Consumption locks the account, character, and character item-state row,
validates account session status, checks exact placement binding, and creates or
rotates the simulation-session token in one transaction. The response includes
the fenced item-state revision, carried weight, capacity, and equipment: every
occupied equipment slot with its item id, definition id, quantity, and item
revision at that item-state revision. It also carries the character's look.
Heartbeat updates the lease and then reads the current committed carry tuple and
equipment, so a worker can advance active movement and combat state
monotonically. Reconnect reads the tuple again under the admission lock.

Simulation session properties:

- Global account and character uniqueness.
- Exact shard, worker, and worker runtime ownership.
- Opaque token with only a stored hash.
- Database-generated expiry after `Simulation:SessionLeaseLifetimeSeconds`.
- A worker heartbeat every `SimulationWorker:SimulationSessionHeartbeatSeconds`,
  at most `SimulationSessionHeartbeatMaxConcurrency` at a time, so one worker
  cannot create an unbounded GameService request fan-out.
- Idempotent exact-generation release.
- Reconnect token rotation.
- Safe stale-token rejection after reconnect.
- Graceful leave remains owned by its in-flight release operation after the
  local lease is removed, preventing concurrent leaves from being misreported as
  revoked sessions.

The worker also enforces cached lease expiry locally so a GameService outage
cannot leave a connected player active forever. The local deadline is the
request send time plus the lease duration that GameService returns, so it does
not depend on the database clock.

SimulationWorker keeps each character's item state by exact simulation-session
identity and applies only increasing item-state revisions. A same-revision
conflict invalidates the local lease instead of accepting contradictory weight,
capacity, or equipment. The worker updates the item state only from committed
GameService results and heartbeats, and sends a newer carry tuple reliably
before it affects movement.

## Realtime UDP Transport

SimulationWorker uses LiteNetLib and the versioned `GameProtocol` package.

- Connection key validation happens before application messages. Without a
  valid registration lease, or with every one of the `MaxConnections` slots
  taken, a connection request is refused silently.
- New peers must send one reliable ordered join request within the join
  handshake timeout, which is also LiteNetLib's disconnect timeout.
- Join ticket consumption is asynchronous and does not block the UDP poll loop.
- The realtime loop runs on its own thread and sleeps until the next tick or the
  next transport poll, `NetworkPollIntervalMilliseconds`, whichever comes first.
  On Windows it waits with a high-resolution waitable timer, because ordinary
  waits round up to the 15.6 ms timer tick; ticks start within about a
  millisecond of their schedule.
- At the end of every loop iteration the worker wakes LiteNetLib's update thread,
  so the queued packets leave at once instead of on the transport's own 15 ms
  update.
- LiteNetLib's packet pool holds one packet per allowed connection plus one per
  512 bytes of the aggregate snapshot budget of one broadcast, between 1,000 and
  65,536, about 6,200 with the default settings, because one snapshot broadcast
  queues an owner packet for every connection and the remote chunks the budget
  allows before they are sent.
- Control messages use reliable ordered delivery, movement input uses sequenced
  delivery, and simulation snapshots use unreliable delivery.
- Packet magic, version, type, bounds, lengths, and finite numeric values are
  validated. Protocol violations receive a stable error where possible and are
  then disconnected.
- `SimulationWorker:Admission` limits remote peers that have not joined yet: at
  most `MaxUnauthenticatedPeers` in total and `MaxUnauthenticatedPeersPerAddress`
  per address, and new connections per address are rate limited by
  `ConnectionAttemptsPerAddressPerSecond` with a `ConnectionAttemptBurstPerAddress`
  burst. Loopback peers, such as local bots and stress tools, are exempt.
  Refused connections are counted by the
  `simulation_worker.realtime.connections.rejected` metric.
- A peer with more than `MaxReliableQueuedPackets` unacknowledged reliable
  control packets is disconnected, so a client that never acknowledges cannot
  grow server memory without bound.

Join acceptance carries Shard and World identity, the Shard rules, and the
initial carry tuple. A standalone client must be rebuilt when the protocol
version changes, and a mismatched client receives the explicit protocol
rejection path, as [Versioning](REALTIME_PROTOCOL.md#versioning) describes.

## UDP Resilience And Quotas

Per-peer token buckets enforce `SimulationWorker:UdpQuotas` packet and byte
rates with burst allowance. Sustained inbound abuse is rejected with
`udp_rate_limited`. Excess unreliable snapshot output may be dropped without
delaying reliable lifecycle messages.

An additional worker-wide snapshot byte bucket bounds aggregate unreliable
output. Snapshot admission occurs for a complete chunk batch, so a peer receives
either all chunks for one snapshot sequence or none. The recipient start rotates
past the admitted group after each broadcast, distributing overload gaps across
peers instead of starving the same tail of the connection list. Reliable
control, spawn, despawn, and leave traffic does not consume this snapshot
budget.

HTTP calls to GameService map timeout, connection, invalid-response,
authentication, and domain failures to structured results:

- `auth_service_timeout` and `auth_service_unavailable` are uncertain outcomes,
  because GameService may have committed the operation before the response was
  lost. Item, corpse, and live Mob loot results with those codes always ask the
  client to refresh, and operations that the worker retries reuse their
  operation id.
- `worker_runtime_changed`, `simulation_session_invalid`,
  `auth_service_authentication_failed`, and `invalid_auth_response` end the
  player's session, and so does `wrong_simulation_worker` for an item or NPC
  lifecycle operation. For a corpse operation `wrong_simulation_worker` means a
  corpse of another Shard and closes only the corpse view.
- Live Mob loot claims send the current committed item-state revision from the
  item state store, not the revision from the time of the join.

## Entity Registry And Replication

- `SimulationEntityRegistry` owns nonzero network entity ids.
- Persistent character id and transient network entity id are separate.
- `ConnectionEntityBindingRegistry` enforces one peer per entity and one entity
  per peer.
- Same-character reconnect preserves the entity and movement state while
  replacing the peer binding.
- Entity spawn and despawn use reliable ordered control messages, and Unity
  receives a reliable baseline before relying on snapshots.
- Each joined peer receives a dedicated fixed-rate owner snapshot for input
  acknowledgement and reconciliation.
- Remote visible entities use MTU-safe shared packet batches. Under dense
  overload, a rotating fair subset reduces remote update cadence before the
  worker-wide byte bucket begins dropping complete frames.
- `SnapshotReplication:OverloadTargetUtilizationBasisPoints` reserves aggregate
  burst headroom. Deferred remote entity updates remain visible through the
  `simulation_worker.simulation.snapshot.entity_updates_deferred` counter.
- A moving Mob travels in the remote snapshots like a remote player, as
  [Mob Brains](#mob-brains) describes.

## Interest Management

`SimulationInterestManager` maintains a spatial hash from authoritative entity
positions. Snapshot refreshes synchronize moving entities, while joins add one
entity incrementally and update only existing nearby observers. Each peer has a
visibility set:

- The enter radius, 128 m by default, adds entities.
- A larger exit radius, 144 m by default, prevents boundary flapping.
- Visibility changes emit reliable spawn or despawn.
- Snapshots contain only visible entity ids.
- Ordered visibility remains cached until the set changes.
- Each connection's visibility set is recomputed every
  `RefreshIntervalSnapshots` snapshots, 3 by default, the connections in turn,
  so a 15 Hz snapshot rate refreshes a third of them per snapshot. The band
  between the enter and exit radius keeps an entity visible while its viewer
  waits for its turn, and joins and leaves still update visibility at once.
- The spatial cells store the entity positions, so the visibility test reads
  them without a dictionary lookup per candidate.
- Equal ordered visibility sets share one encoded snapshot packet batch per
  broadcast.
- The maximum unreliable packet is `1023` bytes, so a remote snapshot chunk
  contains at most 24 current entity records.
- Cell size, radii, and the refresh interval are worker-owned configuration.

This is process-local interest management for one complete shard. Cross-worker
zone interest is deferred until zones exist.

## Server-Authoritative Movement

- Clients send input sequences and click-to-move paths, never accepted
  positions. Each input names the followed path by id, and a batch carries the
  waypoints of the newest input's path until the owner snapshot reports it.
- SimulationWorker runs the shared movement code at a fixed 30 Hz by default.
- It accepts only newer input sequences. A new path has at most 32 waypoints,
  must keep them in the World bounds, and must stay within 256 meters from the
  authoritative position through every waypoint; a path that breaks the limits
  is adopted as already ended, so client and worker agree that the character
  stands. An input that names a path the worker never received is a protocol
  error, `unknown_movement_path`.
- The worker steps the character exactly once per input, in sequence order, as
  the client prediction does. Inputs wait in a short queue that absorbs uneven
  packet timing; a tick applies the oldest one, and a second one while more than
  one still waits.
- When the next input is late, the tick still steps along the current path, at
  most three times in a row, about 100 ms. The late input then takes over that
  step, or the steps are taken again with it when it names another path. A
  longer gap pauses the character on the worker until the inputs arrive, and the
  queue then catches up at two steps per tick. Inputs lost from every batch are
  filled with the path before them, up to 15; a longer gap skips to the newest
  input.
- A step budget keeps the average at one step per tick, with 1/64 of slack for a
  client clock that runs slightly fast, so a client cannot move faster by
  sending more inputs. A client may have at most 16 inputs waiting; a client
  that floods inputs loses the oldest.
- A path that collision blocks for half a second ends.
- New planar control is ignored while airborne.
- Collision, slopes, steps, ground support, and bounds are server-owned.
- Each authoritative entity owns one reusable collision-query workspace, so
  fixed ticks reuse broadphase list and stable-id set capacity instead of
  allocating them again for every movement step.
- Owner snapshots are sent at 15 Hz by default with input acknowledgement,
  authoritative reconciliation state, and path progress. They carry the state
  after the last processed input, without the steps taken ahead of late inputs,
  so the client replays exactly the inputs after the acknowledged one. Remote
  snapshots, combat, and interest use the current state.
- Remote snapshots use the remaining fair byte budget. Their cadence degrades
  explicitly under dense overload instead of starving owner reconciliation.
- The carry state slows walking through the shared encumbrance multiplier, as
  [Carry Weight And Encumbrance](INVENTORY_AND_DEATH_LOOT_DESIGN.md#carry-weight-and-encumbrance)
  defines.

## Collision Data And Streaming

- Each `content/worlds/<WorldId>/collision.json` contains neutral collision
  source for that World.
- `tools/WorldCollisionCompiler` emits versioned binary chunks and a manifest
  below `src/Shared/WorldData/Generated/WorldCollision/<WorldId>`.
- Each chunk has a SHA-256 checksum, and the manifest has a deterministic
  complete collision revision.
- SimulationWorker validates format, World id, checksums, coordinates, and
  revision before binding UDP. It recomputes the revision from the manifest
  chunk list and rejects a manifest whose stored revision differs. Startup fails
  when no collision chunk is available around the World spawn.
- Unity validates and loads the same data, with the same revision check, before
  prediction.
- Collision data stays within plus or minus 1,000,000 world units, chunk sizes
  stay between 1 and 4096, and one shape may span at most 65,536 chunks. Chunk
  coordinates are clamped to plus or minus 2^24, so .NET and Unity map every
  position to the same chunk, and a query wider than the loaded set scans the
  loaded chunks instead of every cell in its range.
- The character motor derives its substep count from the full displacement
  length, so no substep, diagonal or not, exceeds the configured limit.
- Static shapes are assigned to every intersected chunk and deduplicated by
  stable id during queries. A chunk with more than 32 shapes indexes them in a
  planar grid of about 2 m cells, so terrain chunks with thousands of triangles
  stay cheap to query.
- Collision loads within `CollisionStreaming:LoadRadiusChunks` chunks, 2 by
  default, of every streaming anchor and unloads outside `UnloadRadiusChunks`,
  3 by default, which gives streaming hysteresis. The anchors are the players,
  the World spawn, and walking Mobs.
- A composite collision world combines static chunks and mutable dynamic shapes.

Collision format version 2 holds oriented boxes and one-sided triangles. The
bake turns non-convex MeshColliders into triangles and TerrainColliders into
heightfields. The shared compiler triangulates heightfields, two triangles per
cell except for holes, so Unity and .NET produce identical chunks. A triangle
collides only from its front, so a tunnel can run below walkable terrain. The
compilers also add one movement-layer box per placed resource node, as
[Collision Bounds](../content/README.md#collision-bounds) describes. Replicated
dynamic transforms are not implemented. Collision authoring is described in
[Development Worlds](DEVELOPMENT_WORLDS.md).

## World Actors And Interaction

SimulationWorker loads its World content before binding UDP:

- The actor runtime of its World, `world-actors.json` in its output World
  directory. It rejects malformed runtime content, revision drift, wrong World
  identity, unsupported values, invalid references, or unsafe collection bounds,
  and content that the realtime protocol limits cannot carry.
- The compiled item catalog from `SimulationWorker:ItemCatalogPath`, verified
  strictly. Every actor item reference must resolve against it, and the worker
  reports its revision in each heartbeat.
- The combat rules and the character appearance content, as
  [Target-Based Combat](#target-based-combat) and
  [Character Looks](#character-looks) describe.

Assignment activation reconstructs ordinary actors from compiled WorldData with
fresh runtime and network entity ids. The actor model, the capability kinds, and
the interaction rules are in
[NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md). The worker implements
them as follows:

- Every current NPC and resource node is authoritatively invulnerable.
- NPCs and resource nodes are event-driven. Mobs occupy bounded central dormant
  or active schedule buckets selected by their activity profile. Delayed actor
  work, the respawn of a depleted resource node or a dead Mob, is scheduled in
  the central `WorldActorTimerQueue`, a min-heap by due tick that
  `WorldActorRuntimeDriver` drains every tick.
- Actor presence reuses reliable interest enter and exit messages, while actor
  state changes use bounded reliable updates. Assignment-local despawn
  tombstones retain runtime identity until every interest exit can encode the
  actor-specific despawn contract. Each interest pass drops a tombstone once no
  connection still has the actor in its interest set, so they stay bounded by
  current visibility.
- Insurance, quest offer, quest completion, bank, Recovery Storage, vendor, and
  gathering have handlers; every other capability kind answers with
  `world_interaction_capability_deferred` until its business authority exists.
  Quest completion answers `quest_turn_in_unavailable`, because no progression
  authority exists yet.
- Bank, Recovery Storage, and an opened vendor only confirm the capability;
  their item access follows the open interaction. Insurance apply or removal,
  quest accept or abandon, and vendor purchase or sale reuse the interaction
  session, and a started gathering activity runs in the central gathering pass
  while the session stays open.
- One world-interaction authority validates the exact simulation session,
  worker runtime, assignment, Shard, target identity and revision, active
  state, closest-point bounds distance, static and dynamic line of sight from
  the player's eye, 1.6 m above the root, to the target's bounds center with the
  same ray as combat, player-specific capability availability and revision,
  rate limits, and the one-active-interaction lease.
- Start range is `3.0` metres and maintain range is `3.5` metres. Open sessions
  are revalidated at 5 Hz and close on target invalidation, range, line of
  sight, disconnect, or assignment lifecycle changes.
- World interaction intents share a per-peer budget of 8 per second with a burst
  of 12; more return `world_interaction_rate_limited`.
- Capability summaries are returned only after a successful interaction open
  and are independently revisioned for each player. Multiple players may
  interact with the same actor, but one player cannot overlap an actor
  interaction and a corpse view.

The insurance and quest actions bridge the validated interaction to the worker
item route. Insurance applies or removes a one-death policy and charges
`Items:NpcLifecycle:InsurancePrice` through the wallet with an `insurance`
ledger row. Accepting the quest grants `Items:NpcLifecycle:QuestItemQuantity` of
`QuestItemDefinitionId` with the exact quest-grant lineage
`Items:NpcLifecycle:QuestGrantId` and suppresses a duplicate active grant.
Abandoning removes only the active protected items of that lineage; an item held
in Recovery Storage also loses its slot row, and an unclaimed delivery left
without items is removed, so no empty delivery remains. Insurance removal keeps
the item instance.

## Vendor Trades

The vendor rules are in
[Vendor Trades](INVENTORY_AND_DEATH_LOOT_DESIGN.md#vendor-trades), and the
vendor profiles are item catalog content, described in
[Vendor Profiles](../content/items/README.md#vendor-profiles). The shipped
`vendor.city_general_store` is the profile of Mira the Quartermaster's vendor
capability.

- Opening the vendor capability only confirms it, so the client can show the
  offers from its bundled catalog. A `CapabilityAction` `VendorPurchase` or
  `VendorSale` goes through the same lease, range, line of sight, and capability
  revision checks as every capability, then through the NPC item lifecycle path
  to GameService. The worker sends the capability's vendor profile as the
  vendor access of the operation, never a client value, and answers
  "Purchased." or "Sold." on success.
- GameService resolves the vendor and its offers from the active mirrored
  catalog and prices them with the shared `VendorPricing` rule. The request
  carries the total the client showed, and any difference is rejected with
  `vendor_price_changed`.
- A vendor command without the live vendor access of an active vendor is
  rejected with `vendor_access_required`, an unknown or inactive vendor with
  `vendor_unavailable`, an offer it does not make with
  `vendor_offer_unavailable`, an item it does not buy, or a sale worth nothing,
  with `vendor_will_not_buy`, and a purchase that does not fit with
  `inventory_full`.
- Purchases and sales are idempotent by operation id like every other item
  mutation, so a retry never charges or pays twice. Synthetic development bots
  cannot trade.

## Resource Nodes And Gathering

A resource node and its resource profile are actor content, described in
[Resource Nodes And Gathering](NPC_AND_MOB_SYSTEM_DESIGN.md#resource-nodes-and-gathering)
and [Resource Nodes And Item References](../content/README.md#resource-nodes-and-item-references).
SimulationWorker runs gathering as follows:

- Charges are live Shard state in `WorldActorStore`, shared by every gatherer.
  Each node keeps remaining and reserved charges and a generation that advances
  on every depletion, so a stale completion or respawn never touches a newer
  cycle. A worker restart restores every node with full charges.
- A `CapabilityAction` `StartGathering` passes the usual interaction checks, and
  the gathering handler rejects a depleted node with `resource_node_depleted`.
  It then reads the `tool` slot from the equipment GameService last committed
  for the session and rejects an item without the profile's tool kind, or an
  empty slot, with `gathering_tool_required` and the kind's name, such as
  `Woodcutting Axe required in the Tool slot.`, in the interaction result, so
  gathering never starts; without loaded equipment it rejects with
  `gathering_unavailable`. The realtime loop then registers one gathering
  activity per player and sends `GatheringState` `Started` with the cycle time.
- The gathering pass runs every tick after the lease revalidation. A due cycle
  reserves one charge and queues a `gather_resource` call to GameService behind
  the player's other authority work. When every remaining charge is reserved by
  other gatherers, the cycle waits for the next tick. GameService checks that an
  item of the required tool kind is equipped in the `tool` slot again, since the
  tool may have been taken off since the start, otherwise
  `gathering_tool_required` with the same message, which the worker passes on in
  `GatheringState` `Stopped`. It places the yield through carried acquisition.
- A committed yield consumes the charge, applies the committed carry state, and
  sends `GatheringState` `Yielded` with the item-state revision. The next cycle
  starts one cycle time later. The last charge makes the node inactive for every
  viewer, closes its interaction leases at their next revalidation, stops
  everyone gathering there with `resource_node_depleted`, and schedules its
  respawn in `WorldActorTimerQueue`. The respawn restores the charges and makes
  the node active again.
- Moving more than 0.1 m from where gathering started stops it with
  `gathering_interrupted`, and a closed interaction with
  `world_interaction_closed`. A rejected yield releases the charge and stops
  with the rejection code, such as `inventory_full`. An uncertain GameService
  outcome retries up to three times with the same operation id, so a lost answer
  never grants twice, and a rejection that ends the item session disconnects
  the player.
- While a player gathers, the worker shows the tool of its `tool` slot in its
  look, as [Character Looks](#character-looks) describes.
- Synthetic development bots cannot gather.

## Target-Based Combat

SimulationWorker loads the combat rules from `SimulationWorker:CombatRulesPath`
before binding UDP. The default is `WorldData/Combat/combat-rules.json` in the
worker output, which the build copies from `content/combat/combat-rules.json`.
The loader rejects unknown and duplicate properties, another format version,
values out of range, and rule set ids that are not sorted, lowercase, and
unique. The rules, described in [Combat Rules](../content/README.md#combat-rules),
hold the player maximum health and mana, the respawn delay, the combat timeout,
health and mana regeneration, the equipment lockout, the arrow batch interval,
and the Shard rule sets. Weapon values, weapon family windups and projectile
speeds, and ammunition bonuses come from the item catalog, and Mob values from
the actor catalog. The combat model is in
[Game Design](GAME_DESIGN.md#combat-model).

- The Shard rule set `mvp-open-risk` allows attacks on other characters.
  `JoinAccepted` carries the rule as `ShardRules`.
- A `CombatIntent` `AttackTarget` selects a target that the peer sees, that is a
  combatant, alive, and not the attacker itself, and that is a character only
  when the rule set allows it. `ClearTarget` clears the target. The target stays
  selected until it dies or leaves, or the rule set no longer allows it. Combat
  intents share a per-peer budget of 8 per second with a burst of 12; more
  return `combat_intent_rate_limited`.
- On every tick, a character with a target attacks on its weapon's cooldown when
  it stands still, the target's bounds are within the weapon range, the line of
  sight from eye height reaches the target's center, a weapon is equipped in
  `main_hand`, the equipped arrows belong to the weapon's ammunition family, and
  for a magic weapon the character has the weapon's mana cost. Otherwise the
  owner sees the attack status that holds the attack; `NotEnoughMana` keeps the
  target and the attack resumes once the mana is back. Line of sight costs a
  raycast, so it is checked when an attack is due to start or to release and
  five times per second otherwise.
- An attack starts on the cooldown with an `AttackStarted` event that names the
  tick at which it releases, and releases its blow, arrow, or bolt when the
  windup of its weapon family ends, the family's `windupSeconds`, timed so the
  body's attack clip strikes or looses then. The interval runs from the start,
  so the windup leaves the damage per second as it was. Whatever would keep a
  new attack from starting, such as a step, a lost target, a lockout, or the
  target leaving sight, cancels an attack in its windup with an
  `AttackCancelled` event: nothing is spent, and the next attack may start as
  soon as the character stands ready again. In its windup an attack keeps a
  target that walks on within a quarter more than the weapon's range,
  `CombatSystem.WindupRangeLeeway`, so a patrolling wolf does not cancel a
  shot, and is cancelled only beyond that. A Mob's bite releases at once.
- An attack rolls damage between the weapon's minimum and maximum and adds the
  arrow's bonus percent, rounded half up. An arrow or bolt flies at its weapon
  family's projectile speed and hits at its impact tick, at least one tick after
  the release, when the target is still alive. An arrow is spent when it looses,
  and a magic attack spends the weapon's mana cost when its bolt leaves, whether
  or not it hits. Every `AttackStarted` and `AttackReleased` event names the
  attack's combat style, so clients draw an arrow or a bolt.
- A different item in any equipment slot holds attacks for the equipment
  lockout, and an item or corpse operation that may change equipment holds them
  while it runs. A changed arrow count keeps the same item and does not count.
- A shot counts against the committed arrow stack at once, so the owner sees the
  live count in `OwnerCombatState`. The spent arrows commit as one batch through
  `ammunition-consumptions` once the arrow batch interval has passed since the
  first uncommitted shot, and also before any other item or corpse operation of
  the character, before a leave, and before a disconnected session is
  released, all through the character's authority lane. A committed batch sends
  the owner `OwnerItemQuantityChanged`. An uncertain batch retries with the same
  operation id up to 8 attempts, with a backoff from 0.5 seconds that doubles up
  to 10 seconds; after that its arrows stay with the player. A rejected batch
  loses its shots, and a rejection that ends the item session disconnects the
  player. An item intent or an NPC or vendor action whose expected revision only
  arrow batches advanced is sent with the newest revision instead. Corpse
  operations carry no expected character revision.
- A hit that brings health to 0 kills at once, so a later arrow of the same tick
  neither hits nor takes the kill, and a character killed by an arrow does not
  shoot in that tick any more. The killed character stops moving, its world
  interaction and corpse view close with `combat_character_dead`, its waiting
  operations are rejected with that code, and so are its new item, corpse,
  world, and combat intents. The death commits through `player-deaths` with a
  death event id that the worker creates once, together with the shots that are
  not committed yet and the killer. It retries with the same id and a backoff
  from 0.5 seconds that doubles up to 10 seconds until it commits, and drops the
  uncommitted shots when the arrow stack changed underneath, so they cannot
  block the death. After the respawn delay, and once the death committed, the
  character respawns at the World spawn with full health and mana, and every
  viewer receives its respawn.
- A combatant is in combat until the combat timeout after its last attack or
  hit. A living character out of combat regenerates health and mana every
  second up to its maximum. Mana is owner state: only `OwnerCombatState`
  carries it. A joining character and a respawn start with full health and mana.
- Every damageable Mob with a combat profile is a combatant, and a Mob with a
  brain and a locomotion profile also thinks and walks, as
  [Mob Brains](#mob-brains) describes. At 0 health a Mob can no longer be
  targeted, and every attacker's target on it clears. Its death event id is
  `ScopedGuid("mob-death", runtimeActorId)`; each Mob life has its own runtime
  id, so the id is stable for retries and unique per death. The Mob rolls its
  loot profile in entry order, where each entry drops with its chance in basis
  points and a quantity between its bounds. A non-empty roll becomes the Mob's
  corpse: a live corpse in worker memory, or for a Mob whose content selects
  durable custody a durable corpse in GameService, which retries with the same
  death event id and a backoff from 0.5 seconds that doubles up to 10 seconds
  until it commits. When GameService answers that the corpse can never be
  created, the Mob leaves without one. The Mob then leaves every view with the
  despawn reason `died`, a Mob without a corpse at the end of its death tick,
  after its death reached the viewers, and its spawn instance returns after its
  respawn profile's delay with a new runtime and entity id. Respawn timers reset
  on a worker restart.
- A disconnect does not cancel combat work. The session stays until the death
  and the last arrow batch committed and is released afterwards, and a leave
  waits for the same work. A worker that stops commits the uncommitted shots it
  can still name exactly, as a best effort, including the shots behind a batch
  whose answer arrived while it stopped; a batch without a committed answer is
  left alone, and its arrows stay with the player. A worker crash loses at most
  the uncommitted shots of one batch interval, which stay with the player, and a
  death between the lethal hit and its commit, after which the character keeps
  its items.
- `CombatantState` follows every spawn of a combatant, and every changed
  combatant reaches its viewers once at the end of the tick. Combat events reach
  every peer that sees the source or the target, and `OwnerCombatState` reaches
  the owner.
- Synthetic development bots are no combatants, because they have no durable
  character that could die. An attack on one returns
  `combat_target_not_attackable`.

## Mob Brains

A Mob with a brain profile and a locomotion profile runs a brain in
SimulationWorker, in the Mob phase of every tick before combat. Nothing gets its
own task, timer, or thread. The values below are those of the shipped profiles
in `content/actors/catalog.json`:

- The activity tiers decide who works. A dormant Mob neither thinks nor moves; a
  coarse check at the activity profile's dormant rate, every 2 seconds for
  `mob.standard`, makes it active when a player comes within the activation
  range, 48 m, and dormant again when none is within the dormancy range, 64 m. A
  Mob that fights, walks home, or died stays active whoever is near. One grid of
  player positions per tick answers these checks and detection, instead of
  comparing every Mob with every player.
- An active Mob thinks at the activity profile's active rate, 10 per second,
  staggered by entity id, and a moving Mob steps every tick with the player motor
  and collision at its locomotion profile's capsule: at its patrol speed while
  it patrols, 1.4 m/s for the wolves, and at its walk speed while it chases or
  walks home, 4.5 m/s. Mobs do not collide with players or with each other.
- The brain is a state machine: `Idle` stands at the spawn, `Patrol` walks the
  spawn's patrol path with its waits, `Chase` walks toward the target, `Attack`
  stands still and bites on the combat profile's cooldown, `Evade` walks home,
  and `Dead` waits for the corpse and despawn.
- An aggressive brain detects the nearest living player within its detection
  range, 8 m for the Feral Wolves and 10 m for the Feral Alpha, with line of
  sight. The defensive brain `mob.defensive` of the Grey Wolf detects no one.
  Damage starts a fight for every brain. Every spawn of a Mob carries its
  brain's aggression, so clients can color its nameplate. When the target dies or becomes
  invalid, the Mob takes the attacker with the most damage that is still alive,
  then the nearest detected player, and evades otherwise.
- A chasing Mob walks to half its attack range from the target and plans again
  when the target moved more than 1.5 m from the planned goal. In range with
  line of sight from its eye, at 80 percent of its capsule height, it stands,
  faces the target, and combat lets it bite: the bite lands at once, rolls the
  combat profile's damage, and may kill the character with the Mob as the
  recorded killer.
- The leash and the give-up time end a fight. A Mob that is farther than its
  leash range, 30 m, from where the fight started, the spawn of an idle Mob or
  the patrol position of a patrolling one, or that neither bit nor was hit for
  its give-up time, 10 seconds, evades: it clears its target, cannot be damaged,
  walks back, and on arrival has full health and returns to its routine. A Mob
  that cannot walk home after three routes is placed there.
- Routes come from the shared `MovementPathPlanner`, one planner per locomotion
  profile, through one queue that plans at most 8 routes per tick; each route
  expands at most 4,096 grid cells and ends at the closest reachable point when
  the goal cannot be reached. A Mob has at most one waiting request. Spawn
  points and patrol points are projected onto the ground under them, searched
  20 m above and below their authored height, and a Mob starts to move only once
  the collision under it is loaded; walking Mobs keep their collision loaded
  like players.
- A moving Mob travels in the unreliable `SimulationSnapshot` like a remote
  player, and the snapshots change neither the state nor the interaction
  revision. The first tick it stands still sends one reliable `WorldActorState`
  with its rest pose, which advances the state revision but never the
  interaction revision.

## Character Looks

How looks work is in
[The Look On The Server](EQUIPMENT_VISUALS_DESIGN.md#the-look-on-the-server).
The servers implement it as follows:

- GameService stores each character's body model, look choices, and lowered
  headwear in `character_appearances` and checks every stored look against
  `content/characters/appearance.json`. Character creation rejects a look that
  the body model does not offer with `invalid_character_appearance`.
- SimulationWorker loads the same appearance content from
  `SimulationWorker:CharacterAppearancePath` and sends each player's look, the
  items of its visible equipment slots, its headwear, and the tool it uses while
  it gathers, as [Character Appearance](REALTIME_PROTOCOL.md#character-appearance)
  describes. Development bots get a look and an outfit that the worker picks
  from their character id.
- An `AppearanceIntent` lowers or raises the headwear, or in the Development
  environment stores another body model and look. The worker forwards it to the
  appearance route, which refuses a malformed change with
  `invalid_appearance_change`, a development look outside the Development
  environment with `development_look_disabled`, and a look that the appearance
  content does not offer with `invalid_character_appearance`. One change per
  peer runs at a time, and a second one while it runs is answered with
  `appearance_change_in_flight`. A development bot cannot change its look.

## Development Admin Tools

A SimulationWorker with `DevelopmentAdmin:Enabled` lets every player, but never
a synthetic bot, use the tools of the Unity Admin menu (F3). The option fails
startup outside the Development environment, is on in the worker's
`appsettings.Development.json`, and the worker logs a warning when it is on.

- After every join, a reconnect too, the worker sends the owner
  `AdminStateChanged` with its walk speed percent and invulnerability. A client
  that receives none knows the tools are off.
- `AdminIntent` asks for one tool and `AdminIntentResult` answers it; a worker
  without the option answers `admin_tools_disabled`.
  - `SetMovementSpeed` walks the character at 100 to 1000 percent of the World's
    walk speed: `AuthoritativePlayerMovement` steps with
    `MovementSimulationSettings.WithWalkSpeedPercent`, the shared method the
    client predicts with. 1000 percent, 50 m/s, stays far inside the motor's
    substep budget, and the step budget still allows one step per input.
  - `Teleport` moves a living character onto walkable ground at a point inside
    the World bounds, standing still, and despawns and spawns it again for
    everyone who sees it, as a respawn does, with the despawn reason
    `teleported`. It loads the collision chunks around the destination first. A
    dead character gets `admin_character_dead`, a point outside the World
    `admin_teleport_outside_world`, and a point with no walkable ground near it
    `admin_teleport_no_ground`.
  - `RestoreHealth` fills a living character's health and mana.
  - `SetInvulnerable` makes hits leave the character's health as it is, as an
    evading Mob's are. Without combat it answers `admin_combat_unavailable`.
- The walk speed and invulnerability belong to the character's entity, so they
  last until the session ends, and a reconnect of the same session keeps them.
  Nothing is stored in PostgreSQL.

## Items And Corpses

The item rules are in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md), the
durable item boundary in
[Durable Item Boundary](PROJECT_ARCHITECTURE.md#durable-item-boundary), and the
kernel and its operation kinds in
[Items Transaction Kernel](GAME_SERVICE_MODULES.md#items-transaction-kernel).
This section describes the implemented behavior around them.

### Item Catalog

- GameService bundles the checked-in runtime gameplay catalog from
  `Items:CatalogPath`, revalidates its revision and structural fingerprints, and
  mirrors the full relational definition structure in one advisory-locked
  PostgreSQL transaction before accepting traffic. It refuses to start when the
  new catalog would invalidate live state, as
  [Revisions And Structural Changes](../content/items/README.md#revisions-and-structural-changes)
  describes; such a change needs an explicit data migration.
- `GET /api/item-catalog` returns the current relational catalog graph from a
  read-only repeatable-read transaction. Definitions are present exactly once in
  that response. The response uses the catalog revision as a strong ETag, is
  revalidated by clients, and returns `304 Not Modified` when unchanged.
- SimulationWorker runs on the same compiled catalog, and placement refuses a
  worker whose catalog revision differs, as
  [Shard Discovery And Placement](#shard-discovery-and-placement) describes.
- Server snapshots must match the client's bundled gameplay revision, or the
  Unity client reports `item_catalog_update_required` and refuses stale item
  state.

### Item State Reads

- The item-state, bank, and Recovery routes first verify exact account and
  active-character ownership, then read permanent inventory, equipment, equipped
  Bag contents, bank, Secure Container, Recovery deliveries, revisions,
  encumbrance state, and the currency balance from one repeatable-read
  snapshot. An owning account may inspect its bank and Recovery Storage from
  anywhere; that read authorizes no mutation.
- Recovery reads return only unclaimed deliveries.
- Item instance DTOs contain only instance id, stable definition id, quantity,
  revision, and active policy kind and status. Policy sources, recovery source
  event ids, operation payloads, structural fingerprints, credentials, icons,
  and other client presentation data are excluded.
- Weight and capacity remain unitless integers. Load ratio and movement
  multiplier are returned as deterministic basis points.
- Character-specific reads and writes return `Cache-Control: no-store` and
  `Pragma: no-cache`. GameService never returns icon bytes, Unity references, or
  client presentation entries.

### Item Transactions

Every durable item mutation commits or rejects as a whole through the kernel.
Seen from outside:

- Every mutation carries an operation id. Replaying the same id and canonical
  payload returns the stored result; reusing it with another payload returns
  `item_operation_conflict`. A retry of a committed SimulationWorker operation
  replays its stored result even when the live session has ended since, so a
  lost response never turns a committed change into a reported failure.
- Player-chosen operation ids, from account routes and from SimulationWorker
  requests other than the retried system operations, must be random version 4
  UUIDs or the command returns `item_operation_id_invalid` without claiming the
  id. Every derived system id is a version 5 UUID, so a player cannot claim an
  id that a system operation needs.
- Two kinds of rejection are returned without being stored, so the same id can
  run again: `item_transaction_timeout`, which also covers lock timeouts,
  statement timeouts, deadlocks, and serialization failures, and every
  rejection of a retried system operation: player death, durable Mob corpse
  creation, single live Mob loot grants, corpse expiry, and Recovery expiry. A
  live Mob loot grant therefore stays claimable after a rejected attempt until
  one claim commits.
- A canonical command above `Items:Operations:MaximumCommandPayloadBytes` is
  rejected with `item_command_payload_too_large` before any database access,
  and an HTTP body above `MaximumHttpRequestBodyBytes` never reaches it.
  Statements and lock waits are bounded by `TransactionTimeoutMilliseconds` and
  `LockTimeoutMilliseconds`.
- Player mutations carry the expected character item-state revision, and
  targeted item, Bag, and corpse mutations carry the revisions of the rows they
  target, so an unrelated change in the same container does not reject a
  targeted loot request. A stale revision returns `item_state_conflict`,
  `item_quantity_changed`, `bag_state_changed`, or `corpse_state_changed`.
- Voluntary mutations enforce the exact 140 percent hard cap with
  `carry_weight_limit_exceeded`. The kernel is the only hard-cap authority; no
  row-level check exists, so an authoritative death can persist retained Secure
  Container weight above the cap, after which only non-worsening remediation
  commits.
- Equipping into a slot that an equipped item blocks, or with an item that would
  block an occupied slot, is rejected with `equipment_slot_blocked`, and below
  the item's required character level with
  `equipment_level_requirement_not_met`, on every path into an equipment slot,
  including corpse Bag swaps. An equip request for a stack of the equipped
  definition is rejected with `equipment_slot_occupied`, because it tops up the
  equipped stack through a merge instead.
- `grant_mob_loot` grants into an empty container slot or, with an equipment
  slot instead, an empty equipment slot under a derived item id, or tops up the
  stack the worker names in that slot when the two merge within the slot limit.
  It never swaps, because the worker cannot take live Mob loot back, so a named
  stack of another item is rejected with `item_slot_occupied` or
  `equipment_slot_occupied`.
- Every worker mutation response, from item operations, ammunition batches,
  corpse operations, live Mob loot grants, and player deaths, carries
  `characterState`: the acting character's item-state revision, carry tuple,
  and equipment, read right after the commit. A later commit can make it newer
  than the operation's own revision but never older, and SimulationWorker
  rejects a response whose state is older, belongs to another character, or has
  a duplicate equipment slot or item. Stored operation results stay unchanged,
  so a replay returns the same result with the current state.

### Offline Account Item Routes

- Account mutation requests derive authority from the authenticated principal
  and use an offline-required actor. Character and item-state rows are locked
  before checking active simulation sessions, using the same character lock as
  simulation admission. An active simulation session returns
  `item_offline_access_required` without partial mutation.
- Bank and Recovery Storage sources and destinations are never reachable
  offline, so Recovery deliveries are claimed only in the world.
- The Secure Container tier route lets an account actor select only the catalog
  base tier. A larger tier is granted only by a trusted entitlement or purchase
  source with system authority, which does not exist yet.

### Live Item Operations

- SimulationWorker accepts item intents only from an exact joined player on the
  control channel, runs at most one per peer at a time in the character's
  authority lane, and bounds each pending queue to eight operations; more return
  `item_operation_queue_full`. Synthetic development bots cannot mutate durable
  items.
- Bank and Recovery Storage custody require the matching access flag. The worker
  sets it when the operation starts while the player has an open interaction
  with a world actor whose `bank` or `recovery_storage` capability is available;
  otherwise GameService rejects with `bank_access_required` or
  `recovery_access_required`. The interaction closes when the player leaves its
  range or loses line of sight, which ends the access. Secure Container custody
  has no city requirement. Insurance and quest actions pass their flags only
  through their own NPC capability, and vendor trades pass the vendor profile of
  the validated `vendor` capability.
- In the Development environment, `DevelopmentItemInteractions:GlobalBankAndRecoveryAccess`
  grants Bank and Recovery access from any position for local item testing; the
  worker's `appsettings.Development.json` turns it on. Startup rejects it in
  every other environment, and it never includes insurance access.
- The worker service-authenticated endpoint carries the exact account,
  character, simulation session token, worker id, runtime id, and Shard. The
  authenticated worker id must match the body, or the route answers
  `wrong_simulation_worker`. GameService locks and validates the character, item
  state, active simulation session, active account session, online worker
  runtime, and current assignment inside the same transaction that runs the
  command.
- A committed operation returns its item changes, which the worker sends to the
  owner as `OwnerItemStateChanged` when one packet can describe them, as
  [Owner Item State Changes](REALTIME_PROTOCOL.md#owner-item-state-changes)
  describes; the client refreshes its inventory otherwise. A committed result
  without committed character state, or whose revisions contradict the active
  simulation state, ends the session with `item_state_diverged`.

### Player Death And Durable Corpses

- A player-death request carries the expected character revision, the
  transform, and the presentation key `corpse.remains`. It may carry
  `ammunitionConsumption`, the shots that were not committed yet, which the
  death transaction consumes before it partitions the remaining stack into the
  corpse, and `killer`, either `mob` with an actor definition id or `character`
  with a character id. `death_events` stores the killer, with a character id in
  canonical form. Both fields are optional, and a death without them keeps the
  request fingerprint it had without them.
- Death-event ids add a second idempotency fence above the operation journal. A
  replay with the same authoritative character, Shard, transform, and
  presentation data returns the original corpse, absolute expiry, Recovery
  deliveries, and committed character revision without repartitioning. Reusing
  the event id with different data returns `death_event_conflict`. The event row
  is removed together with its closed corpse after the closed-corpse retention
  period. The partition always advances the character revision, so a replay
  that outlives the event row is rejected as a revision conflict instead of
  partitioning twice.
- The database constrains a player corpse's expiry to its database creation time
  plus exactly five minutes, and its transform to a bounded position and a
  normalized quaternion. Snapshot rows contain no item-instance identity.
  Durable Mob corpses use the lifetime their content selects.
- `CorpseExpiryHostedService` scans database-time deadlines in bounded batches.
  Each corpse owns one durable expiry operation id. Cleanup locks the corpse,
  records one `item_destructions` and item-operation audit entry per remaining
  instance, deletes children before Bag roots, closes remaining section
  containers, and marks the corpse expired. Empty player corpses are not closed
  early. A request that arrives after expiry receives `corpse_expired`.
- After obtaining a current registration lease, SimulationWorker restores only
  open, unexpired corpses for its exact runtime and Shard assignment, and checks
  again every `ItemInteraction:DurableCorpseRefreshSeconds`. GameService returns
  database time with the snapshot. The worker advances that time with a
  monotonic clock and keeps the presentation state without owning or copying
  item custody. A worker restart restores each corpse at its persisted
  transform with the unchanged deadline.

### Corpse Interaction

- SimulationWorker exposes corpse presence within
  `ItemInteraction:CorpseDiscoveryRadius` of the player, twice per second. One
  player can inspect one corpse at a time, within
  `ItemInteraction:CorpseInteractionRadius`, while any number of players may
  inspect the same corpse. The worker revalidates its authoritative
  three-dimensional position and cached absolute lifetime before every open,
  refresh, or mutation, and rejects with `corpse_out_of_range`,
  `corpse_view_not_open`, or `corpse_interaction_active`. GameService repeats
  exact-session, Shard, and lifetime authority inside the transaction boundary.
- Corpse reads use short read-only snapshots. Full and partial transfers,
  internal corpse moves, ordinary occupied-slot swaps, and atomic Bag aggregate
  swaps reuse the kernel with targeted item, container, and Bag-root revisions.
  Internal moves can split, merge, or swap across the three corpse sections
  without advancing character item-state or carry. Unrelated corpse changes may
  commit concurrently, competing custody has one stable winner, and no
  PostgreSQL transaction remains open across a client network wait.
- Loot all locks the looter, the corpse at the revision the client saw, and
  every corpse and carried item, plans the steps with `CorpseLootAllPlanner`
  from the locked state, and commits each step under the checks of the single
  loot it stands for. A corpse that changed answers `item_state_conflict`, and a
  Loot all that takes nothing answers `inventory_full`. A live Mob Loot all
  plans the worker's entries the same way with whole items only.
- Every live Mob grant, single or Loot all, records its grant id in
  `live_mob_loot_grants`, so a grant committed once is never committed again. A
  single loot of a recorded grant is rejected as `item_already_looted`, and the
  worker then takes the entry out of the corpse.
- The worker broadcasts each committed targeted delta or replacement to every
  current viewer. An open or refresh always gives its requester a complete
  snapshot, while the other viewers receive the delta from the newer revision.
  Its runtime store accepts only monotonic corpse revisions. A completion that
  arrives after its peer disconnected never releases the corpse reservation of
  a new peer that reuses the LiteNetLib peer id; disconnect cleanup releases
  every reservation of the old peer first.
- Expiry, invalidation, and missing-corpse results close affected views with
  stable codes, while stale item or quantity conflicts trigger a refresh. Taking
  the last item of a live Mob corpse closes every view with `corpse_emptied`
  instead of `corpse_invalidated`, also when the presence broadcast notices the
  emptied corpse first. The emptied corpse stays inactive in worker memory until
  its deadline, so a replayed creation cannot restore its loot. The closure
  codes are listed in [Realtime Codes](#realtime-codes).
- GameService accepts `corpse.remains` as the presentation key of a player
  death, and only `corpse.mob` and the keys below it for a durable Mob corpse,
  as [Corpse Presence Facing](REALTIME_PROTOCOL.md#corpse-presence-facing)
  describes. Every Mob corpse gives whole items only.

### Development Item Commands

The Development item tool is a set of process-local GameService commands, not
HTTP routes, so no development grant route exists. Each runs in the Development
environment only, against a loopback PostgreSQL host whose database name does
not contain `prod`, applies every change through the item transaction kernel,
and requires an offline target character. The Unity window
`MMORPG > Tools > Inventory Item Grants` drives them, and
[Development Item Commands](MANUAL_TESTING.md#development-item-commands) shows
how to run them.

| Command | Behavior |
| --- | --- |
| `--dev-items-list` | Report every local character with its balance, the active definitions, and the development packages |
| `--dev-items-grant <characterId> <definitionId> <quantity> <permanent\|bank\|secure>` | Grant one stack into the permanent inventory, the bank, or the Secure Container |
| `--dev-items-package <characterId> <packageId>` | Apply one package from `content/development/item-packages.json` to a new empty character. It refuses a replay once an item or Recovery delivery exists, and rejects a package that misses its exact expected load |
| `--dev-items-corpse <characterId> <shardId> <x> <y> <z>` | Apply the corpse seed package to an empty source when necessary, then run the normal durable death transaction at that Shard position. The corpse keeps its database deadline across a worker restart |
| `--dev-items-level <characterId> <level>` | Set the character's level, because no experience exists yet |
| `--dev-items-currency <characterId> <amount>` | Credit a positive whole amount through the wallet with a `development` ledger row |

## Error Codes

Error codes are stable contracts for programs; messages are for people and may
change. GameService returns its codes in RFC Problem Details as the `code`
extension, SimulationWorker returns realtime codes in the `Error` of protocol
results and closures, and Unity adds `item_catalog_update_required`.

### HTTP Error Responses

- A malformed request body returns `400` with `invalid_request`, a transient
  PostgreSQL connection failure returns `503` with `database_unavailable`, and
  any other unhandled failure returns `500` with `internal_server_error`. A
  request that the client aborted is logged at debug level and answered with
  `499`, not reported as a server error.
- Item rejections map to statuses by class on the account, worker item, and
  corpse routes: `401` for an invalid simulation session, `403` for missing
  authority or service access, such as `item_authority_required`,
  `wrong_simulation_worker`, `bank_access_required`, `recovery_access_required`,
  and `vendor_access_required`, `404` for a missing item, policy, Recovery
  delivery, or corpse, `409` for stale state and races, such as
  `item_state_conflict`, `item_operation_conflict`, `item_transaction_timeout`,
  `item_slot_occupied`, `equipment_slot_occupied`, `item_quantity_changed`,
  `bag_state_changed`, `worker_runtime_changed`, `vendor_price_changed`,
  `inventory_full`, the corpse lifecycle codes, and on account routes
  `item_offline_access_required`, and `422` for every other domain rejection.
  An incomplete request returns `400` with `item_operation_id_required`,
  `item_secure_tier_request_invalid`, or `item_simulation_operation_invalid`.

### GameService Codes

| Area | Codes |
| --- | --- |
| Requests and platform | `invalid_request`, `internal_server_error`, `database_unavailable`, `rate_limit_exceeded`, `session_cache_invalidation_unavailable` |
| Accounts and sessions | `invalid_email`, `invalid_username`, `invalid_password`, `invalid_login`, `invalid_credentials`, `email_already_used`, `username_already_used`, `account_already_exists`, `account_not_found`, `invalid_session_token`, `account_session_replaced` |
| Characters | `invalid_character_name`, `character_name_taken`, `character_limit_reached`, `invalid_character_appearance`, `character_not_found` |
| Placement and join tickets | `invalid_character_id`, `shard_not_found`, `shard_unavailable`, `simulation_session_worker_unavailable`, `account_character_already_active`, `character_already_active`, `simulation_worker_item_catalog_mismatch`, `invalid_ticket`, `invalid_simulation_target`, `simulation_session_changed`, `wrong_simulation_assignment` |
| Worker authentication and registration | `invalid_service_credentials`, `service_worker_mismatch`, `invalid_simulation_worker_identity`, `invalid_worker_runtime`, `invalid_worker_started_at`, `invalid_worker_host`, `invalid_worker_port`, `invalid_worker_capacity`, `invalid_worker_revision`, `invalid_protocol_version`, `simulation_topology_mismatch`, `simulation_world_not_found`, `simulation_assignment_target_not_found`, `simulation_worker_not_found`, `shard_assignment_conflict`, `shard_world_rebind_blocked`, `worker_runtime_changed` |
| Simulation sessions and looks | `invalid_simulation_session_id`, `invalid_simulation_session_token`, `invalid_simulation_session`, `invalid_simulation_session_request`, `invalid_appearance_change`, `development_look_disabled` |
| Item reads and requests | `item_catalog_not_found`, `character_inventory_not_found`, `character_bank_not_found`, `character_recovery_not_found`, `item_operation_id_required`, `item_secure_tier_request_invalid` |
| Item authority and session | `item_authority_required`, `item_offline_access_required`, `item_simulation_operation_invalid`, `wrong_simulation_worker`, `simulation_session_invalid` |
| Item operations | `item_operation_id_invalid`, `item_operation_conflict`, `item_transaction_timeout`, `item_command_payload_too_large` |
| Item state | `item_not_found`, `item_not_owned`, `item_state_conflict`, `item_quantity_changed`, `bag_state_changed` |
| Item placement | `item_slot_occupied`, `item_slot_incompatible`, `equipment_slot_occupied`, `equipment_slot_incompatible`, `equipment_slot_blocked`, `equipment_level_requirement_not_met`, `secure_container_item_forbidden`, `bag_not_empty`, `carry_weight_limit_exceeded` |
| Stacks and ammunition | `item_stack_incompatible`, `item_stack_limit_exceeded`, `ammunition_not_equipped` |
| Policy, quest, and currency | `item_policy_restricted`, `item_policy_not_found`, `item_destroy_forbidden`, `quest_grant_invalid`, `currency_insufficient`, `currency_limit_exceeded` |
| Vendors and acquisition | `vendor_access_required`, `vendor_unavailable`, `vendor_offer_unavailable`, `vendor_price_changed`, `vendor_will_not_buy`, `inventory_full` |
| Gathering | `gathering_tool_required`, `gathering_yield_invalid` |
| Bank and Recovery | `bank_access_required`, `recovery_access_required`, `recovery_delivery_not_found`, `recovery_delivery_not_expired` |
| Death and corpses | `death_event_conflict`, `mob_corpse_invalid`, `mob_loot_grant_invalid`, `corpse_not_found`, `corpse_not_expired`, `corpse_state_changed`, `corpse_expired`, `corpse_invalidated`, `item_already_looted` |
| Development tools | `development_simulation_bots_disabled`, `development_bot_loopback_required`, `invalid_development_bot_authority`, `invalid_development_bot_index`, `invalid_development_bot_instance_id`, `development_bot_already_active`, `development_bot_identity_changed`, `development_bot_placement_unavailable`, `development_bot_worker_capacity_reached`, `invalid_shard_id`, `invalid_join_ticket`, `invalid_stack_stress_fixture_authority`, `stack_stress_fixture_loopback_required`, `stack_stress_fixture_invalid`, `stack_stress_fixture_not_empty` |

### Realtime Codes

| Area | Codes |
| --- | --- |
| Packets and dispatch | `invalid_packet`, `udp_rate_limited`, `join_required`, `unexpected_message`, `internal_error`, and per family an `invalid_*_delivery` code for a message on the wrong channel or delivery and an `invalid_*` code for a malformed intent, such as `invalid_join_request`, `invalid_movement_input`, `invalid_item_operation`, and `invalid_world_interaction` |
| Join and session | `missing_join_ticket`, `join_timeout`, `join_failed`, `leave_failed`, `invalid_leave_request`, `worker_registry_lease_unavailable`, `session_missing`, `session_expired`, `session_revoked`, `session_reconnected`, `account_session_replaced`, `simulation_session_changed`, `wrong_simulation_assignment`, `character_already_active`, `carry_state_conflict`, `carry_state_missing` |
| GameService calls | `auth_service_timeout`, `auth_service_unavailable`, `auth_service_rejected`, `auth_service_authentication_failed`, `invalid_auth_response` |
| Movement | `movement_unavailable`, `unknown_movement_path` |
| Items | `item_operation_queue_full`, `item_operation_failed`, `item_operation_unavailable`, `item_state_diverged` |
| Corpses | `corpse_out_of_range`, `corpse_view_not_open`, `corpse_interaction_active`, `corpse_interaction_invalid`, `corpse_interaction_unavailable`, `corpse_operation_failed`, `live_mob_corpse_operation_unsupported` |
| Corpse view closures | `corpse_view_closed` when the viewer closed the view, `corpse_emptied` when the last item of a live Mob corpse was taken, `corpse_expired` at the deadline, `corpse_invalidated` when the corpse disappeared or its state can no longer be trusted, and `corpse_not_found` when the worker or GameService no longer knows it; a view also closes with `combat_character_dead` when its viewer dies, and with the code of a rejection that ends it, such as `wrong_simulation_worker` |
| World interaction | `world_actor_not_found`, `world_actor_unavailable`, `world_interaction_invalid`, `world_interaction_active`, `world_interaction_out_of_range`, `world_interaction_line_of_sight_blocked`, `world_interaction_target_changed`, `world_interaction_capability_unavailable`, `world_interaction_capability_deferred`, `world_interaction_session_invalid`, `world_interaction_closed`, `world_interaction_failed`, `world_interaction_unavailable`, `world_interaction_queue_full`, `world_interaction_rate_limited`, `npc_lifecycle_action_invalid`, `npc_lifecycle_unavailable`, `quest_turn_in_unavailable` |
| Gathering | `resource_node_depleted`, `gathering_interrupted`, `gathering_action_invalid`, `gathering_unavailable`, `gathering_failed`, and the GameService rejection of a yield, such as `gathering_tool_required` |
| Combat | `combat_target_not_found`, `combat_target_not_attackable`, `combat_target_dead`, `combat_target_self`, `combat_player_versus_player_disabled`, `combat_character_dead`, `combat_intent_invalid`, `combat_intent_rate_limited`, `combat_unavailable` |
| Admin tools | `admin_tools_disabled`, `admin_character_dead`, `admin_teleport_outside_world`, `admin_teleport_no_ground`, `admin_combat_unavailable`, `admin_unavailable`, `invalid_admin_intent` |
| Looks | `appearance_unavailable`, `appearance_change_in_flight`, `appearance_change_failed`, `invalid_appearance_intent` |
| Unity client | `item_catalog_update_required` |

Conditions that change every tick, such as being out of range or out of mana,
are attack statuses in `OwnerCombatState`, not error codes. How a code travels
on the wire is in [Realtime Protocol](REALTIME_PROTOCOL.md).

## HTTP Security And Error Handling

- Account routes use the `AccountSession` authentication handler and policy.
- Worker routes use the `SimulationWorker` handler and policy.
- Worker credentials use `X-Simulation-Worker-ID` and
  `X-Simulation-Worker-Secret`.
- Local shared-secret mode authenticates any valid worker identifier and leaves
  topology authorization to the registry. When a per-worker credential map is
  configured in `ServiceAuthentication:SimulationWorkers`, it becomes an exact
  allowlist and shared fallback is disabled.
- Secrets are compared in fixed time. Every worker secret must have at least 32
  characters, and startup rejects the `replace-with-` placeholders of
  `.env.example`.
- Central middleware converts unhandled failures into RFC Problem Details.
- Responses carry `X-Correlation-ID`, error responses too, as
  [Correlation Ids](MONITORING.md#correlation-ids) describes.
- Token-bearing responses use `Cache-Control: no-store` and `Pragma: no-cache`.
- No debug HTTP endpoint is registered in the current service surface.

### Redis Account-Session Acceleration

GameService uses Redis as a bounded cache-aside accelerator for successful
account-session validation. Token hashes, never bearer tokens, identify cache
entries below the `RedisAcceleration:KeyPrefix`. Every entry expires no later
than both `SessionCacheTtlSeconds` and the durable PostgreSQL session expiry. A
cache miss, corrupt value, tombstone, or Redis read failure falls back to
PostgreSQL. `RedisAcceleration:SessionCacheEnabled=false` measures the
PostgreSQL validation path without disabling the distributed rate limit, and
`RedisAcceleration:Enabled=false` stops every Redis use except the readiness
check.

Logout, manual revocation, and login replacement write a session-id tombstone
before committing the PostgreSQL revocation, then delete the cached token key.
The tombstone lives `SessionRevocationTombstoneSeconds`, at least as long as any
token cache entry. If GameService cannot store the required tombstone, it
returns `503 session_cache_invalidation_unavailable` and does not commit the
durable revocation. This prevents a successfully revoked session from remaining
usable through stale cache state.

Login and registration also use a Redis fixed-window rate limit keyed by a hash
of the remote address and policy name. The process-local ASP.NET rate limit
remains active as a fallback if Redis is unavailable. Disconnected Redis
commands fail fast instead of accumulating in a client backlog. The Redis
counters and logs are in [Monitoring](MONITORING.md#gameservice).

## Configuration Defaults

Checked-in non-secret defaults live in each service's `Config` folder, and the
ignored root `.env` holds local credentials and connection strings, not worker
topology or World selection. Every setting fails fast at startup; the
precedence and reload rules are in
[Configuration Ownership](PROJECT_ARCHITECTURE.md#configuration-ownership). The
telemetry settings are in [Monitoring](MONITORING.md#configuration).

### GameService

| Setting | Default | Meaning |
| --- | --- | --- |
| `ConnectionStrings:Postgres`, `ConnectionStrings:Redis` | from `.env` | Required. The PostgreSQL connection must name host, database, username, and password; GameService connects with the application name `Mmorpg.GameService` |
| `Database:RunMigrationsOnStartup` | `true` | Apply the migrations, the seeds, and the topology at startup |
| `Database:MaximumPoolSize` | `64` | Npgsql pool size of one process, at most 1,000 |
| `HealthChecks:TimeoutMilliseconds` | `1000` | Timeout of each readiness probe |
| `Auth:SessionLifetimeHours` | `24` | Account session lifetime, 1 to 720 hours |
| `RateLimiting:Authentication:PermitLimit`, `WindowSeconds` | `5`, `60` | Login and registration requests per remote address and window, each limited separately |
| `RedisAcceleration:Enabled`, `SessionCacheEnabled`, `AuthenticationRateLimitingEnabled` | `true` | Redis session cache and distributed rate limit |
| `RedisAcceleration:KeyPrefix` | `shooter-mmo` | Prefix of every Redis key, 1 to 64 letters, digits, `-`, `_`, or `.` |
| `RedisAcceleration:SessionCacheTtlSeconds` | `60` | Lifetime of a cached session, 1 to 300 seconds |
| `RedisAcceleration:SessionRevocationTombstoneSeconds` | `120` | Lifetime of a revocation tombstone, at least the cache lifetime and at most 3,600 seconds |
| `RedisAcceleration:MetricsIntervalSeconds` | `30` | Interval of the Redis counter log |
| `SIMULATION_WORKER_SERVICE_SECRET` or `ServiceAuthentication:SimulationWorkers:<workerId>` | from `.env` | The shared worker secret, or one secret per worker id |
| `Game:MaxCharactersPerAccount` | `5` | Characters an account may own |
| `Characters:AppearancePath` | `WorldData/Characters/appearance.json` in the output | The character appearance content |
| `Simulation:JoinTicketLifetimeSeconds` | `30` | Join ticket lifetime |
| `Simulation:SessionLeaseLifetimeSeconds` | `30` | Simulation session lease that each heartbeat renews |
| `Simulation:WorkerHeartbeatTimeoutSeconds` | `30` | Time after the last heartbeat until a worker counts as gone |
| `Simulation:MetricsIntervalSeconds` | `15` | Refresh interval of the Shard gauges, 1 to 3,600 seconds |
| `Simulation:Topology` | `local-fleet`, `local-node-1`, `local-shard-1`, `local-shard-2` | Fleets, Nodes, and Shards, as [Topology Bootstrap](#topology-bootstrap) describes |
| `WorldData:WorldsPath` | `WorldData/Worlds` | The World manifests that seed the World definitions |
| `Items:CatalogPath` | `WorldData/Items/core.item-catalog.json` | The compiled item catalog that GameService mirrors |
| `Items:Operations:TransactionTimeoutMilliseconds` | `10000` | Statement timeout of an item transaction, 100 to 120,000 |
| `Items:Operations:LockTimeoutMilliseconds` | `2000` | Lock timeout of an item transaction, 10 to 120,000 and at most the transaction timeout |
| `Items:Operations:MaximumCommandPayloadBytes` | `65536` | Largest canonical command, 1,024 to 1,048,576 |
| `Items:Operations:MaximumHttpRequestBodyBytes` | `262144` | Kestrel request body limit, 4,096 to 4,194,304 |
| `Items:Operations:MetricsIntervalSeconds`, `MaintenanceIntervalSeconds` | `30`, `60` | Item summary log and maintenance intervals, 1 to 3,600 seconds |
| `Items:Operations:CleanupBatchSize` | `64` | Rows per maintenance batch, 1 to 1,000 |
| `Items:Operations:AuditRetentionDays`, `ClosedCorpseRetentionDays` | `30`, `30` | Retention of completed operations and of closed corpses, 1 to 3,650 days; the corpse retention cannot be shorter than the audit retention |
| `Items:NpcLifecycle:InsurancePrice` | `100` | Server-owned insurance price |
| `Items:NpcLifecycle:QuestGrantId`, `QuestItemDefinitionId`, `QuestItemQuantity` | `quest.local.sealed_letter`, `quest_item.sealed_letter`, `1` | The quest grant of the quest offer capability |
| `Items:Development:ItemPackagesPath` | `Content/Development/item-packages.json` in the output | The Development item packages |
| `DevelopmentSimulationBots:*` and `ACTIVE_SIMULATION_BOTS_SECRET` | off | Development only: `Enabled`, ticket and lease lifetimes of `30` seconds, the `CharacterNamePrefix` `Active Bot`, and `ReservedPlayerConnections` `8` |
| `StackStressFixtures:*` and `STACK_STRESS_FIXTURE_SECRET` | off | Development only: `Enabled` and the inventory and loot definitions that the fixtures grant |

### SimulationWorker

The worker settings live in the `SimulationWorker` section. Environment
variables such as `SIMULATION_WORKER_ID`, `SIMULATION_SHARD_ID`,
`SIMULATION_WORLD_ID`, and `SIMULATION_WORKER_UDP_PORT` override the identity
and endpoint settings, as the `StressArena` launch profile does.

| Setting | Default | Meaning |
| --- | --- | --- |
| `SimulationWorkerId`, `FleetId`, `NodeId`, `ShardId`, `WorldId` | `local-simulation-worker-1`, `local-fleet`, `local-node-1`, `local-shard-1`, `development-world-2` | Worker identity and desired World |
| `WorldDataPath` | `WorldData/Worlds` | The World directories in the output |
| `UdpPort`, `AdvertisedHost`, `AdvertisedUdpPort` | `27015`, `127.0.0.1`, `27015` | UDP endpoint and the endpoint that placement hands out |
| `MaxConnections` | `1000` | Connection slots, at most 10,000 |
| `JoinHandshakeTimeoutSeconds` | `10` | Time for a new peer to send its join request; also LiteNetLib's disconnect timeout |
| `NetworkPollIntervalMilliseconds` | `15` | Longest wait between transport polls, at most 1,000 |
| `GameServiceBaseUrl`, `GameServiceTimeoutSeconds` | `http://localhost:5000`, `5` | GameService address and HTTP timeout |
| `GameServiceSecret` | from `SIMULATION_WORKER_SERVICE_SECRET` | Worker service secret, at least 32 characters |
| `SimulationSessionHeartbeatSeconds`, `SimulationSessionHeartbeatMaxConcurrency` | `10`, `8` | Session heartbeat interval and parallel heartbeats, at most 128 |
| `RegistryHeartbeatSeconds` | `10` | Worker heartbeat interval |
| `RegistryAcquireTimeoutSeconds` | `120` | How long a new runtime waits for a previous runtime's lease, longer than the heartbeat interval and at most 3,600 seconds |
| `NetworkMetricsLogSeconds` | `30` | Interval of the network, status, and performance logs |
| `UdpQuotas:InboundPacketsPerSecond`, `InboundPacketBurst` | `120`, `240` | Inbound packets per peer |
| `UdpQuotas:InboundBytesPerSecond`, `InboundByteBurst` | 128 KiB, 256 KiB | Inbound bytes per peer |
| `UdpQuotas:SnapshotBytesPerSecond`, `SnapshotByteBurst` | 256 KiB, 512 KiB | Unreliable snapshot bytes per peer |
| `UdpQuotas:AggregateSnapshotBytesPerSecond`, `AggregateSnapshotByteBurst` | 38 MiB, 4 MiB | Unreliable snapshot bytes of the whole worker, with fair recipient rotation |
| `Admission:MaxUnauthenticatedPeers`, `MaxUnauthenticatedPeersPerAddress` | `256`, `8` | Remote peers that have not joined yet |
| `Admission:ConnectionAttemptsPerAddressPerSecond`, `ConnectionAttemptBurstPerAddress` | `5`, `20` | New connections per remote address |
| `Admission:MaxReliableQueuedPackets` | `1024` | Unacknowledged reliable packets before a disconnect |
| `InterestManagement:CellSize`, `EnterRadius`, `ExitRadius` | `64`, `128`, `144` m | Spatial hash and visibility hysteresis; at most 64 cells per axis direction |
| `InterestManagement:RefreshIntervalSnapshots` | `3` | Snapshots between two refreshes of one connection's visibility, at most one second of snapshots |
| `SnapshotReplication:OverloadTargetUtilizationBasisPoints` | `9500` | Share of the aggregate snapshot budget that dense remote state may use before it degrades |
| `CollisionStreaming:LoadRadiusChunks`, `UnloadRadiusChunks` | `2`, `3` | Collision streaming radii around each anchor, the unload radius at most 16 |
| `ItemInteraction:CorpseInteractionRadius` | `3` m | Range for opening and using a corpse, at most 20 m |
| `ItemInteraction:CorpseDiscoveryRadius` | `32` m | Range of corpse presence, at least the interaction radius and at most 1,000 m |
| `ItemInteraction:DurableCorpseRefreshSeconds` | `30` | Interval of durable corpse reconciliation, at most 300 seconds |
| `Movement:TickRateHz`, `SnapshotRateHz` | `30`, `15` | Fixed tick and snapshot rates; the snapshot rate must divide the tick rate |
| `Movement:WalkSpeed`, `RotationSpeedDegrees` | `5` m/s, `720` | Walk speed and turn rate |
| `Movement:Gravity`, `MaximumFallSpeed`, `GroundedVerticalVelocity` | `-24`, `55`, `-2` | Falling |
| `Movement:CharacterRadius`, `CharacterHeight`, `StepHeight`, `MaximumSlopeDegrees`, `GroundSnapDistance` | `0.35`, `2`, `0.35`, `45`, `0.4` | Character capsule and ground handling |
| `Movement:MaximumSubstepDistance`, `MaximumPenetrationIterations` | `0.1`, `6` | Motor substep length and collision resolution |
| `ItemCatalogPath`, `CombatRulesPath`, `CharacterAppearancePath` | `WorldData/Items/core.item-catalog.json`, `WorldData/Combat/combat-rules.json`, `WorldData/Characters/appearance.json` | Content files in the output; the actor runtime and collision come from the World directory |
| `ConnectionStrings:Redis`, `HealthChecks:TimeoutMilliseconds` | `127.0.0.1:6379`, `1000` | Redis for the health check and its timeout |
| `DevelopmentItemInteractions:GlobalBankAndRecoveryAccess` | `true` in `appsettings.Development.json` | Development only: Bank and Recovery access from anywhere |
| `DevelopmentAdmin:Enabled` | `true` in `appsettings.Development.json` | Development only: the admin tools |

SimulationWorker requires one valid `WorldData/Worlds/<WorldId>/world.json` in
its output folder for its configured `WorldId`, built from
`content/worlds/<WorldId>/world.json`. The manifest supplies the client scene,
movement bounds, ground height, and authoritative spawn, and the actor and
collision data resolve below the same World directory. This prevents a shard
restart on another World from silently retaining coordinates from the previous
map. Missing manifests, invalid bounds, out-of-bounds spawns, or invalid runtime
content fail before UDP admission.

## Persistence And Maintenance

- Startup bootstraps only characters that have no item state, each in its own
  short transaction, with the catalog's character settings: base carry
  capacity, permanent inventory and bank slot counts, plus the account-entitled
  Secure Container slots and one unbounded Recovery Storage identity. It then
  recomputes carry state in batches of 500 rows that it locks first, so a
  concurrent item transaction is never overwritten with a stale weight, and it
  advances a character's item-state revision only when the weight or capacity
  changed. Startup scales with the number of accounts.
- New character creation calls the same PostgreSQL bootstrap function before
  committing, so a character cannot commit through the service without its
  required item state.
- Partial indexes support completed operation retention, closed-corpse
  retention, and unclaimed Recovery expiry. A bounded hosted maintenance service
  runs every `Items:Operations:MaintenanceIntervalSeconds` and uses
  deterministic system-authority operations to destroy expired Recovery items
  with durable audit. It removes empty closed corpses only after their
  retention, removes claimed Recovery deliveries after the audit retention
  period, and deletes old operation rows only when no destruction or death
  record references them.
- Every database unit uses statement and lock limits plus `skip locked` batch
  selection where concurrent maintenance is permitted.
- The item metrics and logs are in [Monitoring](MONITORING.md#logs).

## Not Yet Implemented

- HTTP item grant routes. Development grants use the guarded process-local
  commands, and gameplay grants come only from NPC quest, Mob loot, vendor
  purchase, and gathering paths.
- Consume and use intents for potions, food, and other consumables.
- A trusted entitlement or purchase source for larger Secure Container tiers.
- Profession experience and levels, gathering success chances, other
  professions, durable resource node state, limited vendor stock, dynamic
  prices, buyback, and player trading.
- Quest progression and quest completion, and the dialogue, crafting, and
  trainer capability business operations.
- A threat table, Mob abilities beyond one attack, Mob groups that assist each
  other, armor, defense, resistances, and the combat triangle.
- Durable unique actor state.
- Zones, cross-zone handoff, or layers.
- Multiple workers cooperating on one shard.
- A production scheduler or fleet autoscaler.
- Alert notifications to a chat or paging service, and a shared or staging
  deployment of the monitoring stack.
- Rigid-body collision and replicated dynamic collision transforms.

The planned and open item rules are in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md#planned),
the deferred actor work in
[Deferred Work](NPC_AND_MOB_SYSTEM_DESIGN.md#deferred-work),
and the deferred game features in
[Deferred Features](GAME_DESIGN.md#deferred-features).
