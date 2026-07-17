# Service Features

Last updated: 2026-07-17

## Purpose

This document records implemented service, networking, persistence, and
operational behavior. Product intent that is not implemented belongs in the MVP
specification. Player-facing behavior belongs in [Game Features](GAME_FEATURES.md).

## Service Topology

The implemented topology is:

```text
Global AuthService and PostgreSQL
  Fleet
    Node
      SimulationWorker logical id
        Worker runtime generation
        SimulationAssignment -> Shard
          Shard -> World content definition
```

There are no realms. Account and character records are global. Fleets identify
compute placement and region, while shards are the player-selectable simulation
copies. World identifies shared content.

The current runtime permits one active worker per shard and one active shard per
worker. Zone and layer partitioning are not implemented.

## AuthService HTTP Surface

| Route | Authority | Behavior |
| --- | --- | --- |
| `POST /api/accounts/register` | Public, rate limited | Create account and login session |
| `POST /api/accounts/login` | Public, rate limited | Replace the active login session |
| `GET /api/accounts/me` | Account session | Return account profile |
| `GET /api/accounts/session` | Account session | Validate the current session |
| `POST /api/accounts/logout` | Account session | Revoke current session and dependent access |
| `DELETE /api/accounts/sessions/{sessionId}` | Account session | Revoke an owned session |
| `GET /api/characters` | Account session | List owned characters |
| `POST /api/characters` | Account session | Create a character |
| `GET /api/item-catalog` and `GET /api/items/catalog` | Account session | Return the neutral current catalog with revision ETag validation |
| `GET /api/characters/{characterId}/item-state` and `GET /api/characters/{characterId}/inventory` | Owning account session | Return one coherent no-store character item-state snapshot |
| `GET /api/characters/{characterId}/bank` | Owning account session | Return focused no-store bank state |
| `GET /api/characters/{characterId}/recovery` | Owning account session | Return focused no-store Recovery Storage deliveries |
| `POST /api/characters/{characterId}/item-operations/relocate` | Owning offline account session | Relocate an item through the durable transaction kernel |
| `POST /api/characters/{characterId}/item-operations/split` | Owning offline account session | Split a stack into an exact destination slot |
| `POST /api/characters/{characterId}/item-operations/merge` | Owning offline account session | Merge compatible stacks |
| `POST /api/characters/{characterId}/item-operations/destroy` | Owning offline account session | Perform allowed player destruction with a server-owned reason |
| `POST /api/characters/{characterId}/recovery/{deliveryId}/claim` | Owning offline account session | Claim a complete available delivery to inventory or bank |
| `POST /api/items/secure-container-tier` | Offline account session | Apply an account Secure Container tier with every character revision |
| `GET /api/shards` | Public | List logical shards, status, players, and capacity |
| `POST /api/shards/{shardId}/join` | Account session | Place an owned character and issue a ticket |
| `POST /api/simulation-workers/{workerId}/heartbeat` | Worker service policy | Register or renew exact worker runtime |
| `POST /api/simulation-workers/{workerId}/offline` | Worker service policy | Release exact worker runtime authority |
| `POST /api/simulation-join-tickets/consume` | Worker service policy | Consume an exact-runtime join ticket |
| `POST /api/simulation-sessions/{id}/heartbeat` | Worker service policy | Renew an exact simulation lease |
| `POST /api/simulation-sessions/{id}/release` | Worker service policy | Release an exact simulation lease |
| `POST /api/simulation-sessions/{id}/item-operations` | Worker service policy | Execute one exact-session active-character item mutation through the durable kernel |
| `POST /api/simulation-sessions/{id}/player-deaths` | Worker service policy | Partition one exact-session authoritative player death into durable corpse and Recovery custody |
| `GET /api/simulation-workers/{workerId}/corpses` | Exact worker service policy | Restore open unexpired corpses for the worker's fresh runtime and active Shard assignment |
| `POST /api/development/simulation-bots/join-tickets` | Development loopback secret | Issue an in-memory exact-runtime bot ticket when explicitly enabled |
| `GET /health/live` | Public | Report process liveness |
| `GET /health/ready` | Public | Verify obligatory dependencies |

The public shard list never exposes a SimulationWorker host, port, or runtime id.
That endpoint appears only in a successful short-lived placement response.

The development bot route is absent unless AuthService runs in `Development`
with `DevelopmentSimulationBots:Enabled=true`. It also requires loopback and a
separate secret. Its identities, tickets, and sessions remain in memory and are
never durable account or character data. SimulationWorker uses the same worker
service routes for real and bot credentials; AuthService routes recognized bot
credentials to the in-memory authority and every other request to the existing
PostgreSQL services.

## Accounts And Authentication

- Email and username are normalized and unique.
- Passwords use ASP.NET Core `PasswordHasher`.
- Account tokens are opaque and only their SHA-256 hashes are stored.
- PostgreSQL enforces one unrevoked account session per account.
- A successful login locks the account, revokes the older session with
  `account_session_replaced`, consumes its pending tickets, releases its active
  simulation sessions, and creates the replacement session transactionally.
- Logout and manual revoke use explicit revocation reasons and dependent cleanup.
- The Unity client periodically validates its account token and handles
  replacement as a controlled disconnect.

Login and registration use independent fixed-window limits keyed by remote IP.
Rejected requests return HTTP 429 with structured Problem Details and
`Retry-After`.

## Character Access

- Character names are validated and normalized.
- Character ownership is checked on every protected operation.
- The configured maximum character count is enforced transactionally.
- Soft-deleted characters are excluded from active queries.
- Simulation placement locks both account and character rows.

PostgreSQL permits only one active simulation session per character and one per
account. A copied account token therefore cannot run two different characters
at the same time.

## Topology Bootstrap

`AuthService/Config/appsettings.json` owns the checked-in topology bootstrap:

- World definitions.
- Fleets with display names and region codes.
- Nodes assigned to fleets.
- Shards assigned to a World and Fleet with a rule set.

Startup validation rejects empty collections, invalid identifiers, duplicates,
unknown references, invalid display names, and invalid region codes. After
migrations, `SimulationTopologySeeder` idempotently upserts configured records.
It does not manufacture online workers. A seeded shard remains offline until a
valid heartbeat and active assignment exist.

## Worker Registration And Assignment

Each SimulationWorker has:

- A stable `SimulationWorkerId`.
- A generated `RuntimeId` for the current process generation.
- Fleet, Node, Shard, and World configuration.
- An advertised UDP endpoint.
- Maximum and active connection counts.
- Protocol, simulation, and collision revisions.
- A process start timestamp.

AuthService validates identifiers, topology consistency, endpoint metadata,
capacity, revisions, and service identity. The heartbeat transaction locks the
target shard and applies these rules:

- A different healthy assigned worker causes `shard_assignment_conflict`.
- A timed-out or explicitly offline owner can be replaced.
- Replacement invalidates old-runtime tickets and simulation sessions even when
  the logical worker moves to another shard.
- A newer runtime can replace an older runtime for the same worker id.
- The older runtime is fenced by `worker_runtime_changed`.
- A stale process cannot mark a newer runtime offline.
- Active assignment indexes prevent two worker or shard owners.

SimulationWorker stores the database-issued lease duration locally. It rejects
new UDP joins without a valid registration lease. A one-second monitor stops the
process after lease expiry. Definitive topology, assignment, runtime, or service
credential rejection also stops the process.

## Shard Discovery And Placement

`GET /api/shards` derives status from fresh, online, actively assigned workers.
It aggregates active players and maximum capacity. A shard is online only when
at least one assigned worker is fresh and has free capacity. The current unique
assignment constraint means that aggregate contains at most one worker.

Join placement:

1. Validates account session and character ownership.
2. Locks the account and character.
3. Releases expired account simulation sessions.
4. Rejects another active character or cross-shard active session.
5. Selects the assigned worker only when its heartbeat is fresh and it has room.
   Capacity uses the greater of worker-reported connections and authoritative
   unexpired simulation sessions, then adds pending tickets in PostgreSQL.
6. Invalidates older pending tickets for the account.
7. Stores a hash of a new ticket bound to shard, worker, runtime, character, and
   account session.
8. Computes expiry after placement locks are acquired.
9. Returns the shard and exact endpoint metadata.

Selection serializes reservations on the target shard, then locks the selected
worker. A waiting request recalculates session and ticket usage from a fresh
database snapshot before reserving capacity. Different shards remain parallel.

## Join Tickets And Simulation Sessions

Join tickets are short lived, one use, and stored only as hashes. A worker must
present the ticket together with its own worker id, runtime id, and shard id.

Consumption locks the account, character, and character item-state row, validates
account session status, checks exact placement binding, and creates or rotates
the simulation-session token in one transaction. The response includes the
fenced item-state revision, carried weight, and capacity. Heartbeat updates the
lease and reads the current committed carry tuple in the same statement so a
worker can advance active movement state monotonically.

Simulation session properties:

- Global account and character uniqueness.
- Exact shard, worker, and worker runtime ownership.
- Opaque token with only a stored hash.
- Database-generated expiry.
- Periodic worker heartbeat.
- Idempotent exact-generation release.
- Reconnect token rotation.
- Safe stale-token rejection after reconnect.
- Graceful leave remains owned by its in-flight release operation after the
  local lease is removed, preventing concurrent leaves from being misreported as
  revoked sessions.

The worker also enforces cached lease expiry locally so an AuthService outage
cannot leave a connected player active forever.

## Realtime UDP Transport

SimulationWorker uses LiteNetLib and the versioned `GameProtocol` package.

- Connection key validation happens before application messages.
- New peers must send one reliable ordered join request before the handshake
  timeout.
- Join ticket consumption is asynchronous and does not block the UDP poll loop.
- Control messages use reliable ordered delivery.
- Movement input uses sequenced delivery.
- Simulation snapshots use unreliable delivery.
- Packet magic, version, type, bounds, lengths, and finite numeric values are
  validated.
- Protocol violations receive a stable error where possible and are then
  disconnected.

Protocol version 8 carries Shard and World identity plus the initial carry tuple
in join acceptance. Reliable ordered carry-state updates deliver later committed
item-state revisions, and typed item intents and results share that control path.
A standalone client must be rebuilt when the protocol version changes.

## Entity Registry And Replication

- `SimulationEntityRegistry` owns nonzero network entity ids.
- Persistent character id and transient network entity id are separate.
- `ConnectionEntityBindingRegistry` enforces one peer per entity and one entity
  per peer.
- Same-character reconnect preserves the entity and movement state while
  replacing the peer binding.
- Entity spawn and despawn use reliable ordered control messages.
- Unity receives a reliable baseline before relying on snapshots.
- Remote presentation lives under a separate Unity presentation root.

## Interest Management

`SimulationInterestManager` maintains a spatial hash from authoritative entity
positions. Snapshot refreshes synchronize moving entities, while joins add one
entity incrementally and update only existing nearby observers. A join no longer
rebuilds and refreshes every peer visibility set. Each peer has a visibility
set:

- Enter radius adds entities.
- A larger exit radius prevents boundary flapping.
- Visibility changes emit reliable spawn or despawn.
- Snapshots contain only visible entity ids.
- Ordered visibility remains cached until the set changes.
- Equal ordered visibility sets share one encoded snapshot packet batch per
  broadcast.
- Cell size and radii are worker-owned config values.

This is process-local interest management for one complete shard. Cross-worker
zone interest is deferred until zones exist.

## Server-Authoritative Movement

- Clients send input sequences, camera yaw, and action buttons, never accepted
  positions.
- SimulationWorker runs the shared movement code at a fixed 30 Hz by default.
- It accepts only newer input sequences and neutralizes stale input.
- Aiming blocks sprint and jump.
- New planar control is ignored while airborne.
- Collision, slopes, steps, ground support, and bounds are server-owned.
- Each authoritative entity owns one reusable collision-query workspace, so
  fixed ticks reuse broadphase list and stable-id set capacity instead of
  allocating them again for every movement step.
- Snapshots are sent at 15 Hz by default with input acknowledgement.
- Unity predicts with the same source and reconciles to authoritative snapshots.

## Collision Data And Streaming

- `WorldData/Authoring` contains neutral JSON source.
- `Tools/WorldCollisionCompiler` emits versioned binary chunks and a manifest.
- Each chunk has a SHA-256 checksum.
- The manifest has a deterministic complete collision revision.
- SimulationWorker validates format, World id, checksums, coordinates, and
  revision before binding UDP.
- Unity validates and loads the same data before prediction.
- Static shapes are assigned to every intersected chunk and deduplicated by
  stable id during queries.
- Position-driven load and larger unload radii provide streaming hysteresis.
- A composite collision world combines static chunks and mutable dynamic shapes.

The current test World uses oriented boxes for ground, boundaries, a camera
wall, ramp, steps, and cover. Triangle terrain and replicated dynamic transforms
are not implemented.

## Item Catalog, Persistence, Account APIs, And Live Mutation

Phases 1 through 10 of the approved item plan are implemented. Offline account
mutations, shared live encumbrance, and the authoritative in-world mutation
boundary plus persistent Unity inventory state and temporary presentation are
available together with durable player-death partition and corpse restoration:

- `WorldData/Authoring/Items/core.item-catalog.json` is the strict neutral
  authoring source.
- `Tools/ItemCatalogCompiler` rejects unknown or duplicate JSON properties,
  missing values, invalid identifiers and references, duplicate ids and Bag
  slots, negative or decimal weights, invalid stack limits, and impossible Bag,
  equipment, policy, or Secure Container combinations.
- The compiler emits sorted deterministic runtime content under
  `WorldData/Runtime/Items`, including one catalog revision and structural
  fingerprints for every item definition and Secure Container tier.
- The checked-in development catalog has nine representative definitions, all
  canonical equipment slots, the medical, material, and ammunition tags, one
  Bag layout with general and specialized slots, and the four-slot base Secure
  Container tier.
- Framework-neutral pure rules cover stack compatibility, specialized slot tag
  acceptance, equipment compatibility, Secure Container eligibility, empty and
  non-empty Bag destinations, Bag containment-cycle rejection, unitless integer
  stack weight, base character capacity `200`, exact 140 percent admission, and
  the fixed-point linear encumbrance multiplier.
- `Shooter MMO > Tools > Item Catalog` edits definitions through an Editor-only
  assembly, invokes the strict shared compiler, shows display-only and structural
  changes, locks baked ids, and writes authoring plus deterministic bake output
  transactionally.
- `Shooter MMO > Tools > Inventory Item Grants` lists initialized local
  characters and active definitions, grants individual stacks, and applies
  deterministic inventory, equipment, stack, encumbrance, Secure Container, and
  Recovery packages. Its short-lived Development command is loopback-only,
  requires offline targets, uses `ItemTransactionService`, and adds no gameplay
  HTTP or realtime protocol surface.
- The client presentation catalog is bundled under Unity Resources. It maps
  stable definition ids to optional icons, localization keys, fallback text,
  and optional prefab presentation keys with its own deterministic revision
  tied to the exact gameplay catalog revision.
- AuthService bundles the checked-in runtime gameplay catalog, revalidates its
  revision and structural fingerprints, then mirrors the full relational
  definition structure in one advisory-locked PostgreSQL transaction before
  accepting traffic.
- The item persistence migration adds typed containers, stable slots, an exact
  container-or-equipment location union, character item state, item policies,
  recovery deliveries, idempotent operations, and relational audit foundations
  with explicit CHECK, foreign-key, unique, partial unique, and revision rules.
- The player-corpse migration adds durable corpses, three section-container
  bindings, presentation-only snapshots, and unique death events. Player expiry
  is constrained to database creation time plus exactly five minutes. Corpse
  transforms require a bounded position and normalized quaternion. Snapshot
  rows contain no item-instance identity.
- A companion Phase 10 migration removes the obsolete row-level 140 percent
  check from `character_item_states`. The transaction kernel remains the only
  hard-cap authority so an authoritative death can persist retained Secure
  Container weight after its equipped Bag capacity bonus is removed.
- Startup idempotently backfills every active character with base carry capacity
  `200`, 20 permanent inventory slots, 40 bank slots, the account-entitled
  Secure Container slots, and one unbounded Recovery Storage identity.
- New character creation calls the same PostgreSQL bootstrap function before
  committing its existing transaction, so a character cannot commit through
  the service without its required item state.
- Startup permits display-only catalog revisions but rejects structural
  definition changes with live item instances and tier changes with live
  entitlements until an explicit data migration resolves them.
- Character deletion cascades live character item custody and recovery rows.
  Account deletion also removes the account Secure Container entitlement.
  Operation and change audit rows remain with deleted actor or item references
  set to null.
- `ItemCatalogQueryService` returns the current relational catalog graph in a
  read-only repeatable-read transaction. Definitions are present exactly once
  in that response. The HTTP response uses the catalog revision as a strong ETag,
  is revalidated by clients, and returns `304 Not Modified` when unchanged.
- `ItemQueryService` first verifies exact account and active-character
  ownership, then reads permanent inventory, equipment, equipped Bag contents,
  bank, Secure Container, Recovery deliveries, revisions, and encumbrance state
  from one repeatable-read snapshot.
- Item instance DTOs contain only instance id, stable definition id, quantity,
  revision, and active policy kind and status. Policy sources, recovery source
  event ids, operation payloads, structural fingerprints, credentials, icons,
  and other client presentation data are excluded.
- Weight and capacity remain unitless integers. Load ratio and movement
  multiplier are returned as deterministic basis points.
- `ItemTransactionService` is the only durable item mutation kernel. Typed
  internal commands cover grant, relocation, equip, unequip, stack split and
  merge, atomic ordinary container-slot swap, quantity consumption, allowed
  destruction, empty Bag storage, complete Bag aggregate swap, Recovery delivery
  add and claim, Secure Container tier change, policy application and removal,
  quest-grant abandonment cleanup, player-death partition, and corpse expiry.
- Cross-character Bag aggregate swaps lock and evaluate the Bag roots and every
  child. A protected or insured Bag or child rejects the complete swap without
  changing custody, quantity, or revisions.
- Every command uses exactly one Npgsql connection and one `READ COMMITTED`
  transaction. It claims a global operation id, hashes a canonical request,
  acquires character, Bag, container, item, policy, and delivery locks in stable
  order, validates current ownership and expected revisions, and stores one
  replayable committed or rejected result.
- Domain rejection uses a savepoint so no partial item, custody, quantity,
  policy, delivery, entitlement, carried-state, revision, or audit mutation can
  survive. Replaying the same operation id and canonical payload returns the
  stored result. A different payload returns `item_operation_conflict`.
- Successful commands recompute unitless carried weight and equipped Bag
  capacity from authoritative custody, advance touched character, container,
  Bag, and item revisions, and append before and after audit rows. Voluntary
  mutations enforce the exact 140 percent hard cap. Authoritative death may
  create an involuntary over-cap state when retained Secure Container weight
  outlives a removed Bag bonus; remediation may neither increase weight nor
  worsen the exact load ratio. Equipment-slot item roots have zero carried
  weight. Equipped Bag contents still count and the equipped Bag bonus still
  increases capacity. Startup reconciliation corrects older stored tuples only
  when they differ.
- Bag content containers are closed while an empty Bag is ordinary storage and
  active only while equipped. Bag and child commands share one aggregate-root
  lock, and aggregate swaps validate both Bag item and content-container
  revisions before exchanging complete Bags.
- Recovery Storage remains system-write-only. Internal delivery creation and
  complete claims to permanent inventory or bank preserve item identity,
  quantity, and policy lineage. Recovery custody remains excluded from weight.
- Secure Container tier changes update every active character on the account.
  Removed slots are drained in descending order to deterministic
  `secure_capacity_reduction` Recovery deliveries before the slots are removed.
  Character bootstrap and tier changes share one account-entitlement advisory
  key so a concurrently created character receives a coherent tier.
- `ItemPolicyRules` independently evaluates trade, auction, vendor sale, player
  destruction, death disposition, insurance eligibility, and stacking
  capability. Protected and insured items block every transfer capability.
- `ItemPolicyService` and `QuestItemService` are system-only adapters over the
  same transaction kernel. Insurance removal changes policy lifecycle without
  replacing the item. Quest abandon removes only active protected items with the
  exact quest-grant source id, and reaccept suppresses duplicate active grants.
- Account mutation requests derive authority from the authenticated principal
  and use an offline-required actor. Character and item-state rows are locked
  before checking active simulation sessions, using the same character lock as
  simulation admission. Active ownership returns
  `item_offline_access_required` without partial mutation.
- Character-specific reads and writes return `Cache-Control: no-store` and
  `Pragma: no-cache`. Domain failures use RFC Problem Details with stable `code`
  values. AuthService never returns icon bytes, Unity references, or client
  presentation entries.
- Admission returns one item-state-row-fenced carry tuple with the accepted
  session lease. Heartbeat returns the current committed tuple with the renewed
  lease. Reconnect reads the tuple again under the admission lock.
- SimulationWorker stores carry state by exact simulation-session identity and
  applies only increasing item-state revisions. Same-revision conflicts
  invalidate the local lease instead of accepting contradictory weight or
  capacity.
- GameSimulation revision `movement-simulation-v3` owns the immutable carry
  state and deterministic encumbrance calculation used by worker movement and
  Unity prediction. Sprint is allowed through exactly 100 percent load, then
  disabled, while the movement multiplier falls linearly to `0.20` at the exact
  140 percent hard cap.
- Realtime protocol version `8` includes carry state on join, later committed
  carry updates, and bounded item-operation intents and results on the reliable
  ordered control path. Supported operations are relocate, equip, unequip,
  split stack, merge stacks, atomic ordinary container-slot swap, allowed
  destruction, and complete Recovery Storage claim. The swap kind reuses the
  existing two-item expectation packet shape, so the protocol version remains
  `8`. Corpse operations remain reserved for their later phase.
- SimulationWorker accepts item intents only from an exact joined player on
  channel 0, processes at most one per peer at a time, and bounds each pending
  queue to eight operations. Synthetic development bots cannot mutate durable
  items.
- Configured `bank`, `recovery_storage`, and `insurance_npc` service points are
  evaluated against the worker's authoritative player position. Bank and
  Recovery Storage custody require the matching access flag. Secure Container
  custody has no city requirement. The insurance flag is available to the
  boundary, but no insurance purchase operation exists yet.
- The worker service-authenticated endpoint carries the exact account,
  character, simulation session token, worker id, runtime id, and Shard. The
  authenticated worker id must match the body. AuthService locks and validates
  the character, item state, active simulation session, active account session,
  online worker runtime, and current assignment inside the same transaction
  that calls `ItemTransactionService`.
- Worker and account APIs cannot both own carried mutation authority. Offline
  account routes and live worker routes coordinate through the character lock,
  and operation ids remain globally idempotent through the existing kernel.
- Death-event ids add a second idempotency fence above the operation journal. A
  replay with the same authoritative character, Shard, transform, and
  presentation data returns the original corpse, absolute expiry, Recovery
  deliveries, and committed character revision without repartitioning. Reusing
  the event id with different data returns `death_event_conflict`.
- One death transaction leaves currency, bank, existing Recovery Storage, and
  Secure Container contents unchanged. Protected items and otherwise-lootable
  active insured items move to source-correlated Recovery deliveries. Insurance
  is consumed only in the latter case, so protected priority preserves an active
  insurance policy.
- Remaining permanent inventory, equipment, Bag root, and Bag children move to
  general, equipment, and Bag corpse sections. Protected or insured Bag roots
  move after every child and therefore reach Recovery empty. Normal children use
  the detached corpse Bag section, while protected and insured children receive
  their own policy result.
- Every corpse has a non-interactive Secure tier snapshot. Effective insured
  equipment and protected or insured Bags add definition-level presentation
  snapshots, but no protected item placeholder or real protected instance id is
  exposed.
- `CorpseExpiryHostedService` scans database-time deadlines in bounded batches.
  Each corpse owns one durable expiry operation id. Cleanup locks the corpse,
  records one `item_destructions` and item-operation audit entry per remaining
  instance, deletes children before Bag roots, closes remaining section
  containers, and marks the corpse expired. Empty player corpses are not closed
  early.
- After obtaining a current registration lease, SimulationWorker restores only
  open, unexpired corpses for its exact runtime and Shard assignment. AuthService
  returns database time with the snapshot. The worker advances that time with a
  monotonic clock and retains the generic loot-crate presentation key without
  owning or copying item custody.
- SimulationWorker updates its carry-state store only from committed AuthService
  results. A newer tuple is sent reliably before it affects movement. Conflicting
  same-revision data or stale live authority causes a targeted refresh response
  or safe disconnect.
- Unity keeps one persistent definition-id catalog index, presentation and icon
  cache, complete and focused snapshot state, monotonic observed revisions,
  structured errors, and one operation journal. It correlates protocol-v8
  results and refreshes authoritative HTTP state to the committed revision
  without optimistically changing item custody or quantity.
- The runtime uGUI panel is replaceable presentation over that state. It exposes
  Permanent inventory, equipment, equipped Bag, Secure Container, Bank, and
  Recovery Storage plus every current live mutation. Typed reusable drag sources
  and targets submit relocation, equipment, split, merge, atomic ordinary swap,
  and atomic Recovery claim operations only after drop validation. `B` shows
  character storage, `C` shows equipment plus character storage, and `I` shows
  the complete Development view with contextual Bank and Recovery. Item clicks
  select only the split and allowed-destruction controls. Bank and Recovery
  reads are globally inspectable by the owning account.
- SimulationWorker normally derives Bank, Recovery Storage, and insurance access
  from authored service points at the authoritative player position. Its
  environment-specific Development configuration can explicitly grant global
  Bank and Recovery access for local item testing. Enabling that option outside
  Development is rejected at startup, and insurance access remains proximity
  based. Every mutation still requires the joined worker path and AuthService's
  exact session, runtime, assignment, transaction, item-rule, and capacity
  validation.
- Gameplay and presentation catalogs load once and must share a source revision.
  Server snapshots must match the same bundled gameplay revision or the client
  reports `item_catalog_update_required` and refuses stale item state.
- `--seed-phase9-items <characterId>` is a process-local Development fixture
  command, not an HTTP route. It calls only `ItemTransactionService`, requires a
  new empty offline character, restricts PostgreSQL to loopback and a
  non-production-like database name, and refuses replay after the first item or
  Recovery delivery exists.

The shared pure rules have no HTTP, PostgreSQL, UnityEngine, or SimulationWorker
runtime dependency. The Editor assembly is isolated from runtime WorldData
assemblies.
AuthService exposes authenticated owned-character reads, offline account item
write routes, and one service-authenticated active-session item route. It exposes
no development grant route. The one-shot Phase 9 fixture is a guarded local
process command rather than a remotely callable service. SimulationWorker has no
inventory database access and holds no item collection. Unity is not an item-rule
authority.

## UDP Resilience And Quotas

Per-peer token buckets enforce configured packet and byte rates with burst
allowance. Sustained inbound abuse is rejected. Excess unreliable snapshot
output may be dropped without delaying reliable lifecycle messages.

An additional worker-wide snapshot byte bucket bounds aggregate unreliable
output. The checked-in local limit is 38 MiB per second with a 4 MiB burst.
Snapshot admission occurs for a complete chunk batch, so a peer receives either
all chunks for one snapshot sequence or none. The recipient start rotates past
the admitted group after each broadcast, distributing overload gaps across
peers instead of starving the same tail of the connection list. Reliable
control, spawn, despawn, and leave traffic does not consume this snapshot
budget.

Session heartbeats use bounded concurrency so one worker cannot create an
unbounded AuthService request fan-out. HTTP calls map timeout, connection,
invalid-response, authentication, and domain failures to structured results.

## Metrics And Logs

The meter `ShooterMmo.SimulationWorker.Realtime` exposes:

- Active peers and entities.
- Joined real players, joined synthetic bots, and unauthenticated peers as
  separate gauges.
- Sent and received packets and bytes.
- Quota rejections, total dropped snapshots, and the subset dropped by
  aggregate snapshot backpressure.
- Accepted and rejected joins.
- Spawn and despawn packet counts.
- Snapshot entity record counts.

Periodic structured logs expose the same totals. A worker status line also
reports real players, synthetic bots, unauthenticated peers, process CPU
normalized to total logical-core capacity, single-core-equivalent CPU, working
set, packet and payload rates, snapshot
drops, quota rejections, and the sample window. Metrics deliberately avoid
account, character, session, entity, and peer identifiers as labels.

The performance meter `ShooterMmo.SimulationWorker.Performance` records network
poll, completed-operation, join-queue-delay, join-finalization, simulation-tick,
tick-lag, collision-streaming, movement, interest, and snapshot-broadcast
durations plus fixed-tick resynchronizations. The periodic metrics log reports
interval averages, approximate p95 and p99 upper bounds, and maximum durations.
It also reports the current and interval-maximum completed-operation backlog so
admission congestion is distinguishable from UDP loss. Snapshot broadcast is
the complete snapshot pipeline and includes the separately reported interest
phase. The same interval log reports process allocation, GC collection counts,
managed heap size and fragmentation, live managed memory, distinct visibility
groups, encoded snapshot packet count, and sent snapshot packet count.

These operational values remain server-side. They are not added to the realtime
gameplay protocol and are not broadcast to Unity clients. The Unity F2 panel
derives its values only from local rendering, prediction, and received traffic.

SimulationWorker filters routine successful `AuthServiceClient` HTTP pipeline
messages below `Warning`. Domain failures and HTTP warnings remain visible, but
high-frequency bot heartbeat and release requests do not drown out worker
status, performance, lifecycle, or error logs.

Routine UDP connect and disconnect events plus successful synthetic bot joins
and leaves are logged at `Debug`. Successful real-player lifecycle events remain
at `Information`, and all admission failures remain visible. Synthetic bot
population is reported by the aggregate worker status line.

Auth, client, and simulation logs use the categories `[AUTH]`, `[CLIENT]`, and
`[SIMULATION]` in the Unity console.

## HTTP Security And Error Handling

- Account routes use the `AccountSession` authentication handler and policy.
- Worker routes use the `SimulationWorker` handler and policy.
- Worker credentials use `X-Simulation-Worker-ID` and
  `X-Simulation-Worker-Secret`.
- Secrets are compared in fixed time.
- Central middleware converts unhandled failures into RFC Problem Details.
- Responses carry `X-Correlation-ID`.
- Token-bearing responses use `Cache-Control: no-store` and `Pragma: no-cache`.
- No debug HTTP endpoint is registered in the current service surface.

## Health And Configuration

AuthService liveness confirms the process loop. Readiness runs PostgreSQL
`select 1` and Redis `PING` within configured timeouts and returns HTTP 503 if an
obligatory dependency fails.

SimulationWorker `--health-check-only` checks AuthService readiness, Redis, and
UDP port availability and exits 0 only when all checks pass.

All application settings fail fast. Checked-in non-secret defaults live in each
service's `Config` folder. The ignored root `.env` contains local credentials
and overrides. Environment variables and command-line options have higher
precedence.

## Persistence And Migrations

Migrations run under a PostgreSQL advisory transaction lock. Each migration id
is inserted only after its SQL succeeds. The topology migration preserves older
data while separating World content from Shard and SimulationWorker runtime
metadata. A later migration enforces one active simulation session per account.
Previously shipped migration ids and their source-schema references retain their
historical names because changing an applied migration would break upgrade
compatibility. The resulting current schema uses only the canonical topology
names listed above.

Integration tests reset only a database whose name contains `test`. Never point
the test connection variable at development or production data.

## Quality Coverage

- Configuration validation tests.
- Authentication, token, and session replacement tests.
- Protocol codec and invalid-packet tests.
- Entity lifecycle, interest, quota, metrics, and movement tests.
- Runtime heartbeat and exact-runtime fencing tests.
- Isolated PostgreSQL tests for migrations, placement, reconnect, global
  session constraints, worker failover, and transactional races.
- Unity EditMode tests for contracts, state, input, movement, and authored assets.
- Unity PlayMode bootstrap smoke tests.
- Socket-level realtime and load-test coverage.
- External headless stress authority and bot coverage for real UDP admission,
  movement, snapshots, graceful leave, process resources, and phase timing.
- Item catalog determinism, malformed and duplicate content rejection,
  structural fingerprint detection, pure rule behavior, and exact integer
  encumbrance boundaries.
- Isolated PostgreSQL item tests for concurrent migration initialization,
  complete and idempotent character backfill, catalog reconciliation and
  compatibility fencing, exact custody constraints, delete behavior, and
  atomic character bootstrap.
- Isolated PostgreSQL read-model tests for cross-account denial, complete and
  ordered empty state, every owned snapshot section, definition and policy
  resolution, catalog metadata deduplication, secret exclusion, and read-only
  behavior.
- Isolated PostgreSQL transaction tests for every internal command, authorization
  and policy lineage, atomic hard-cap rejection, canonical operation replay,
  competing item and slot mutations, stack quantity races, shared Bag locks,
  failed aggregate swaps, Recovery claims, and account-wide Secure Container
  reduction without item loss.
- Isolated policy and HTTP tests for insurance removal, exact quest-grant cleanup
  and reaccept, ETag `304`, no-store responses, owner scoping, stable Problem
  Details, offline session fencing, Recovery deposit rejection, system delivery,
  successful claims, and hard-cap claim rollback.
- Shared carry and movement tests for exact custody contribution, base and Bag
  capacity, lower-capacity Bag rejection, sprint at and above 100 percent,
  fixed-point movement reference points, monotonic active-session propagation,
  protocol validation, and reconnect restoration.
- Phase 8 protocol, HTTP client, worker socket, configuration, and isolated
  PostgreSQL tests for bounded intent decoding, committed results, every exact
  identity mismatch, stale assignment, duplicate intent replay, account-versus-
  worker races, bank and Recovery access, Secure Container carry changes, and
  reconnect restoration.
- Phase 9 backend tests cover fixture command validation, one real PostgreSQL
  transaction-service fixture flow and readback, one-shot rejection, and the
  Recovery delivery revision
  exposed to Unity. Unity EditMode tests cover catalog cache reuse, revision
  mismatch, snapshot validation and coherence, operation correlation,
  specialized and Secure Container targets, non-empty Bag rules, split weight,
  and the hard cap. PlayMode tests cover persistent controller and uGUI creation.
- Phase 10 tests cover migration idempotency and constraints, exact live service
  authentication, death-event replay, total item custody conservation, currency
  and Secure retention, protected and insurance precedence, insured equipment
  snapshots, Bag-root and child ordering, involuntary capacity-loss overflow and
  hard-cap remediation, cross-Shard restart restoration, database-timed
  empty-corpse lifetime, idempotent audited expiry, and a custody-versus-expiry
  race. Worker unit tests cover death and restoration response validation plus
  monotonic database-time expiry in the runtime store.

## Not Yet Implemented

- Development or gameplay item grant routes. The guarded local fixture command
  is intentionally not a route.
- Final inventory visual design, drag-and-drop polish, accessibility, and policy
  detail presentation. The current uGUI is temporary presentation only.
- Corpse operation intents, corpse proximity, corpse views, or loot mutation
  through SimulationWorker or Unity.
- The authoritative combat death producer and active corpse representation.
  The durable authenticated death boundary is ready but is not fabricated by the
  current movement-only gameplay.
- Insurance NPC pricing and purchase behavior, concurrent corpse looting, or
  configurable NPC corpse persistence. Effective one-death insurance
  consumption inside durable player death is implemented.
- Zones, cross-zone handoff, or layers.
- Multiple workers cooperating on one shard.
- Production scheduler or fleet autoscaler.
- Metric exporter, dashboards, and alerting.
- Persistent NPC or combat simulation.
- General terrain mesh and rigid-body collision.

The locked product design and remaining implementation phases for the item
system are documented in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md) and
[Items And Inventory Implementation Plan](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md).
