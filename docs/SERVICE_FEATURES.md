# Service Features

Last updated: 2026-10-26

## Purpose

This document records implemented service, networking, persistence, and
operational behavior. Product intent that is not implemented belongs in the MVP
specification. Player-facing behavior belongs in [Game Features](GAME_FEATURES.md).

## Service Topology

The implemented topology is:

```text
Global GameService and PostgreSQL
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

## GameService HTTP Surface

| Route | Authority | Behavior |
| --- | --- | --- |
| `POST /api/accounts/register` | Public, rate limited | Create account and login session |
| `POST /api/accounts/login` | Public, rate limited | Replace the active login session |
| `GET /api/accounts/me` | Account session | Return account profile |
| `GET /api/accounts/session` | Account session | Validate the current session |
| `POST /api/accounts/logout` | Account session | Revoke current session and dependent access |
| `DELETE /api/accounts/sessions/{sessionId}` | Account session | Revoke an owned session |
| `GET /api/characters` | Account session | List owned characters with their body models |
| `POST /api/characters` | Account session | Create a character with its body model and look, checked against `content/characters/appearance.json`, or the default look of the default body model without one |
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
| `POST /api/simulation-workers/{workerId}/heartbeat` | Worker service policy | Register or renew exact worker runtime |
| `POST /api/simulation-workers/{workerId}/offline` | Worker service policy | Release exact worker runtime authority |
| `POST /api/simulation-join-tickets/consume` | Worker service policy | Consume an exact-runtime join ticket |
| `POST /api/simulation-sessions/{id}/heartbeat` | Worker service policy | Renew an exact simulation lease |
| `POST /api/simulation-sessions/{id}/release` | Worker service policy | Release an exact simulation lease |
| `POST /api/simulation-sessions/{id}/appearance` | Worker service policy | Lower or raise the exact-session character's headwear, or in the Development environment only store another body model and look |
| `POST /api/simulation-sessions/{id}/item-operations` | Worker service policy | Execute one exact-session active-character item mutation, vendor trade, or gathering yield through the durable kernel |
| `POST /api/simulation-sessions/{id}/ammunition-consumptions` | Worker service policy | Commit one idempotent batch of shots from the exact-session character's equipped ammunition stack |
| `POST /api/simulation-sessions/{id}/player-deaths` | Worker service policy | Partition one exact-session authoritative player death into durable corpse and Recovery custody |
| `GET /api/simulation-workers/{workerId}/corpses` | Exact worker service policy | Restore open unexpired corpses for the worker's fresh runtime and active Shard assignment |
| `POST /api/simulation-sessions/{id}/corpses/{corpseId}/open` | Worker service policy | Read one exact-session, unexpired, same-Shard corpse snapshot after worker proximity validation |
| `POST /api/simulation-sessions/{id}/corpses/{corpseId}/item-operations` | Worker service policy | Commit bidirectional full or partial corpse transfers, whole-item loot into an equipment slot, and atomic ordinary, equipment, or Bag aggregate swaps through the durable kernel |
| `POST /api/development/simulation-bots/join-tickets` | Development loopback secret | Issue an in-memory exact-runtime bot ticket when explicitly enabled |
| `GET /health/live` | Public | Report process liveness |
| `GET /health/ready` | Public | Verify obligatory dependencies |

The public shard list never exposes a SimulationWorker host, port, or runtime id.
That endpoint appears only in a successful short-lived placement response.

The development bot route is absent unless GameService runs in `Development`
with `DevelopmentSimulationBots:Enabled=true`. It also requires loopback and a
separate secret. Its identities, tickets, and sessions remain in memory and are
never durable account or character data. SimulationWorker uses the same worker
service routes for real and bot credentials; GameService routes recognized bot
credentials to the in-memory authority and every other request to the existing
PostgreSQL services. A new ticket request of a bot supersedes the bot's own
unconsumed ticket, as a player's join supersedes the account's pending ticket,
so an abandoned bot ticket neither blocks the bot nor holds a connection.

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
- PostgreSQL enforces one unrevoked account session per account.
- A successful login locks the account, revokes the older session with the
  stored reason `session_replaced` and the client error code
  `account_session_replaced`, consumes its pending tickets, releases its active
  simulation sessions, and creates the replacement session transactionally.
- Logout and manual revoke use explicit revocation reasons and dependent cleanup.
  They lock the account first, like login and join, and clean up only sessions
  that belong to the caller. Revoking another account's session id succeeds
  without effect, so foreign session ids are not revealed.
- The Unity client periodically validates its account token and handles
  replacement as a controlled disconnect.

Login and registration use independent fixed-window limits keyed by remote IP.
Rejected requests return HTTP 429 with structured Problem Details and a
`Retry-After` that counts down to the end of the current window. The policy is
taken from the matched endpoint, so a different path casing such as
`/api/accounts/LOGIN` cannot skip the distributed limit.

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

- `characters.currency` is the one balance of a character, owned by the
  Characters module. It is not an item, has no weight, and is global across
  every Shard. A database check keeps it between `0` and `1,000,000,000,000`.
- Every change goes through `ICharacterWallet.DebitAsync` or `CreditAsync` in the
  caller's transaction and appends one append-only `character_currency_ledger`
  row with source kind, source id, item operation id when there is one, signed
  delta, and balance after. The source kinds are `starting_balance`,
  `vendor_purchase`, `vendor_sale`, `insurance`, and `development`, and the
  ledger migration opened each existing nonzero balance with one
  `opening_balance` row, so a character's ledger deltas always sum to its
  balance.
- A debit that the balance does not cover is rejected with
  `currency_insufficient`, and a credit above the maximum balance with
  `currency_limit_exceeded`. Neither changes the balance.
- New characters receive the catalog's starting balance, `100`, with a
  `starting_balance` ledger row in the character creation transaction. Existing
  characters keep their balance.
- The item-state snapshot carries the balance from the same repeatable-read
  snapshot as the items, and every in-world currency change runs inside an item
  transaction, so it advances the item-state revision that clients refresh on.
- A player death never reads or writes currency.
- `--dev-items-currency <characterId> <positiveAmount>` is a process-local
  Development command that credits an offline character through the wallet with
  a `development` ledger row, and `--dev-items-list` reports each character's
  balance. The Unity window `MMORPG > Tools > Inventory Item Grants` uses both.

## Topology Bootstrap

`src/Server/GameService/Config/appsettings.json` owns the checked-in topology bootstrap:

- World definitions.
- Fleets with display names and region codes.
- Nodes assigned to fleets.
- Shards assigned to a World and Fleet with a rule set.

Startup validation rejects empty collections, invalid identifiers, duplicates,
unknown references, invalid display names, and invalid region codes. After
migrations, `SimulationTopologySeeder` idempotently upserts configured records.
It does not manufacture online workers. A seeded shard remains offline until a
valid heartbeat and active assignment exist. The checked-in topology seeds two
shards in `local-fleet`: `local-shard-1` (`Local Shard 1`), which the default
worker binds to Development World 2, and `local-shard-2` (`Local Stress
Arena`), which the SimulationWorker `StressArena` launch profile binds to
Development World 1.

A shard is rebound to another World when a worker heartbeat proposes a different
configured `WorldId`. GameService locks the shard row and refuses the change while
a healthy assignment, pending unexpired join ticket, active unexpired simulation
session, or open durable corpse still references the shard. A worker that stopped heartbeating is treated as gone once the heartbeat timeout
passes. Its pending tickets and simulation sessions are released before the
blockers are counted, so a crashed worker cannot block a rebind until its
leases expire. Open durable corpses always block. The
worker must be stopped cleanly and World-local state must be drained before
retrying. The next worker runtime must report the rebound World identity, so
stale content cannot silently simulate the shard. A migration placeholder assignment with no worker
runtime is not simulation authority and therefore does not block a rebind.

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
target shard and applies these rules:

- A different healthy assigned worker causes `shard_assignment_conflict`.
- A timed-out or explicitly offline owner can be replaced.
- Replacement invalidates old-runtime tickets and simulation sessions even when
  the logical worker moves to another shard.
- A newer runtime can replace an older live runtime for the same worker id, and
  any runtime can replace one that is offline or past the heartbeat timeout.
- Worker secrets are matched to the exact, case-sensitive worker id.
- The older runtime is fenced by `worker_runtime_changed`.
- A stale process cannot mark a newer runtime offline.
- Active assignment indexes prevent two worker or shard owners.

Every heartbeat response also names the rule set of the Shard. SimulationWorker
applies the rule set with that id from its combat rules and stops with
`shard_rule_set_unknown` when its rules do not define it.

SimulationWorker stores the database-issued lease duration locally, measured
from the moment the heartbeat request was sent. It rejects
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
5. Invalidates older pending tickets for the account, so a retried join is not
   blocked by its own earlier ticket.
6. Selects the assigned worker only when its heartbeat is fresh and it has room.
   Capacity uses the greater of worker-reported connections and authoritative
   unexpired simulation sessions, then adds pending tickets in PostgreSQL.
   A reconnect ticket for an already counted session is not added again. In
   Development, the in-memory sessions and pending tickets of synthetic bots
   are joined into the same query, so players and bots never reserve the same
   connections.
7. Re-reads the assignment after locking the worker, so a worker that moved to
   another Shard in the meantime is never placed.
   Rejects the join, and a reconnect to the owning worker, with `409`
   `simulation_worker_item_catalog_mismatch` when the worker's last heartbeat
   reported another item catalog revision than the one GameService mirrors.
   Combat data and Mob loot come from the worker's catalog and item custody
   from GameService's, so both must be the same catalog.
8. Stores a hash of a new ticket bound to shard, worker, runtime, character, and
   account session.
9. Computes expiry with `clock_timestamp()` after placement locks are acquired.
   Ticket consumption and session leases use the same clock, so time spent
   waiting for a lock never shortens or extends a ticket or lease.
10. Returns the shard and exact endpoint metadata.

Selection serializes reservations on the target shard, then locks the selected
worker. A waiting request recalculates session and ticket usage from a fresh
database snapshot before reserving capacity. Different shards remain parallel.

## Join Tickets And Simulation Sessions

Join tickets are short lived, one use, and stored only as hashes. A worker must
present the ticket together with its own worker id, runtime id, and shard id.

Consumption locks the account, character, and character item-state row, validates
account session status, checks exact placement binding, and creates or rotates
the simulation-session token in one transaction. The response includes the
fenced item-state revision, carried weight, capacity, and equipment: every
occupied equipment slot with its item id, definition id, quantity, and item
revision at that item-state revision. Heartbeat updates the lease and then reads
the current committed carry tuple and equipment, so a worker can advance active
movement and combat state monotonically.

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

The worker also enforces cached lease expiry locally so an GameService outage
cannot leave a connected player active forever. The local deadline is the
request send time plus the lease duration that GameService returns, so it does
not depend on the database clock.

## Realtime UDP Transport

SimulationWorker uses LiteNetLib and the versioned `GameProtocol` package.

- Connection key validation happens before application messages.
- New peers must send one reliable ordered join request before the handshake
  timeout.
- Join ticket consumption is asynchronous and does not block the UDP poll loop.
- The realtime loop runs on its own thread and sleeps until the next tick or the
  next transport poll, `NetworkPollIntervalMilliseconds`, whichever comes first.
  On Windows it waits with a high-resolution waitable timer, because ordinary
  waits round up to the 15.6 ms timer tick and started ticks up to a whole tick
  late; ticks now start within about a millisecond of their schedule.
- At the end of every loop iteration the worker wakes LiteNetLib's update thread
  so the packets it queued leave at once. Before, they waited for the
  transport's own 15 ms update, which delayed every snapshot by a random part of
  that period.
- LiteNetLib's packet pool holds one packet per allowed connection plus one per
  512 bytes of the aggregate snapshot budget of one broadcast, between 1,000 and
  65,536, about 6,200 with the default settings, because one snapshot broadcast
  queues an owner packet for every connection and the remote chunks the budget
  allows before they are sent. With the default pool of 1,000 every further
  packet was a new allocation that survived into the older GC generations.
- Control messages use reliable ordered delivery.
- Movement input uses sequenced delivery.
- Simulation snapshots use unreliable delivery.
- Packet magic, version, type, bounds, lengths, and finite numeric values are
  validated.
- Protocol violations receive a stable error where possible and are then
  disconnected.
- `SimulationWorker:Admission` limits remote peers that have not joined yet: at
  most `MaxUnauthenticatedPeers` in total and `MaxUnauthenticatedPeersPerAddress`
  per address, and new connections per address are rate limited by
  `ConnectionAttemptsPerAddressPerSecond` with a `ConnectionAttemptBurstPerAddress`
  burst. Loopback peers, such as local bots and stress tools, are exempt.
- A peer with more than `MaxReliableQueuedPackets` unacknowledged reliable
  control packets is disconnected, so a client that never acknowledges cannot
  grow server memory without bound. Rejected connections are counted by the
  `simulation_worker.realtime.connections.rejected` metric.

Join acceptance carries Shard and World identity plus the initial carry tuple. Reliable ordered control messages carry later item
state, corpse, world-actor presence, bounded actor state, correlated world
interaction contracts, and gathering state. Ordinary actor spawn packets contain
presentation, identity, bounds, disposition, state, and revision data, but not
the complete
player-specific capability summary. A standalone client must be rebuilt when
the protocol version changes, and mismatched clients retain the explicit
protocol rejection path.

## Entity Registry And Replication

- `SimulationEntityRegistry` owns nonzero network entity ids.
- Persistent character id and transient network entity id are separate.
- `ConnectionEntityBindingRegistry` enforces one peer per entity and one entity
  per peer.
- Same-character reconnect preserves the entity and movement state while
  replacing the peer binding.
- Entity spawn and despawn use reliable ordered control messages.
- Unity receives a reliable baseline before relying on snapshots.
- Each joined peer receives a dedicated fixed-rate owner snapshot for input
  acknowledgement and reconciliation.
- Remote visible entities use MTU-safe shared packet batches. Under dense
  overload, a rotating fair subset reduces remote update cadence before the
  worker-wide byte bucket begins dropping complete frames.
- `SnapshotReplication:OverloadTargetUtilizationBasisPoints` reserves aggregate
  burst headroom. Deferred remote entity updates remain visible through the
  `simulation_worker.simulation.snapshot.entity_updates_deferred` counter.
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
- Cell size, radii, and the refresh interval are worker-owned config values.

This is process-local interest management for one complete shard. Cross-worker
zone interest is deferred until zones exist.

## World Actors And Interaction

SimulationWorker loads the compiled item catalog from
`SimulationWorker:ItemCatalogPath` before binding UDP, verifies it strictly,
checks every actor item reference against it, and reports its revision in each
worker heartbeat. The default is `WorldData/Items/core.item-catalog.json` in the
worker output, which the build copies from the WorldData runtime catalog.

SimulationWorker loads `SimulationWorker:ActorDataPath` before binding UDP and
rejects malformed runtime content, revision drift, wrong World identity,
unsupported values, invalid references, or unsafe collection bounds. The
checked-in development content contains six shared definitions, 14 spawn
instances for `development-world-1`, and 21 spawn instances for
`development-world-2`, among them nine Iron Vein resource nodes in the first and
seven Iron Veins and four Oak Trees in the second. The compiler rounds every float step of a group or area
placement to float on its own, so the Unity Editor under Mono, which keeps
intermediates in double, places exactly what the .NET tools place.

- Neutral actor JSON in `content/actors`, one file per actor, and each World's
  `content/worlds/<WorldId>/actor-spawns.json` are compiled deterministically by
  `tools/WorldActorCompiler` into one revisioned runtime manifest per World.
- Stable definition and spawn ids never substitute for fresh worker-runtime
  actor ids or shared nonzero network entity ids.
- Assignment activation reconstructs ordinary actors from compiled WorldData.
  They do not receive one PostgreSQL row per live instance.
- NPC, Mob, and resource node are distinct actor kinds. Faction and disposition
  remain independent, and every current NPC and resource node is
  authoritatively invulnerable.
- NPCs and resource nodes are event-driven. Mobs occupy bounded central dormant
  or active schedule buckets selected by their shared activity profile. No actor
  owns a task, timer, thread, database session, or HTTP poller. Delayed actor
  work, today the respawn of a depleted resource node, is scheduled in the
  central `WorldActorTimerQueue`, a min-heap by due tick that
  `WorldActorRuntimeDriver` drains every tick.
- Actor presence reuses reliable interest enter and exit messages, while actor
  state changes use bounded reliable updates. Assignment-local despawn
  tombstones retain runtime identity until every interest exit can encode the
  actor-specific despawn contract. Each interest pass drops a tombstone once no
  connection still has the actor in its interest set, so they stay bounded by
  current visibility.
- One typed capability registry composes dialogue, vendor, quest, crafting,
  insurance, trainer, bank, Recovery Storage, and gathering descriptors.
  Insurance, quest offer, quest completion, bank, Recovery Storage, vendor, and
  gathering have handlers; every other kind answers with an explicit deferred
  result until its business authority exists.
- A capability carries a `profileId`: the item catalog vendor profile for
  `vendor`, the resource profile for `gathering`, and empty for every other
  kind. The opened capability summary carries it to the client.
- One world-interaction authority validates the exact simulation session,
  worker runtime, assignment, Shard, target identity and revision, active
  state, closest-point bounds distance, static and dynamic line of sight,
  player-specific capability availability and revision, rate limits, and the
  one-active-interaction lease.
- Start range is `3.0` metres and maintain range is `3.5` metres. Open sessions
  are revalidated at 5 Hz and close on target invalidation, range, line of
  sight, disconnect, or assignment lifecycle changes.
- Corpse views use the same target and lease registry while retaining the
  complete corpse state and GameService transaction authority.
- Mutations for the same corpse pass through one worker-local coordinator
  before GameService, while different corpses remain parallel. Open and refresh
  state targets only the requester. Committed deltas are encoded once and reuse
  the same immutable packet bytes for every viewer.

Capability summaries are returned only after a successful interaction open and
are independently revisioned for each player. Multiple players may interact
with the same actor, but one player cannot overlap an actor interaction and a
corpse view.

## Vendor Trades

- Item values and vendor profiles are item catalog content. The shipped vendor
  `vendor.city_general_store` ("General Store") is the profile of the `vendor`
  capability of Mira the Quartermaster (`npc.city_services`). It sells Bronze
  Arrow, Minor Healing Potion, Cooked Trout, Starter Pickaxe, Woodcutting Axe,
  the clothes, armor, sword, and shield at 100 percent of value and buys every category except quest items at 40 percent. Stock is
  unlimited and there is no buyback.
- Opening the vendor capability only confirms it, so the client can show the
  offers from its bundled catalog. A `CapabilityAction` `VendorPurchase` or
  `VendorSale` goes through the same lease, range, line of sight, and capability
  revision checks as every capability, then through the NPC item lifecycle path
  to GameService. The worker sends the capability's vendor profile as the
  vendor access of the operation, never a client value, and answers
  "Purchased." or "Sold." on success.
- GameService resolves the vendor and its offers from the active mirrored
  catalog and prices them with the shared `VendorPricing` rule: the purchase
  total is rounded up and the sale total down. The request carries the total
  the client showed, and any difference is rejected with
  `vendor_price_changed`, so a player is never charged a price they did not
  see.
- A purchase checks the expected character revision, the offer, and a quantity
  within one stack, debits the wallet, and places the items through carried
  acquisition, all in one item transaction.
- A sale takes all or part of one stack from the permanent inventory, the
  equipped Bag, or the Secure Container. Equipped, banked, and Recovery Storage
  items, protected and insured items, quest items, items without value,
  non-empty Bags, and categories the vendor does not buy are refused. A sold
  whole stack is destroyed with an `item_destructions` row with reason
  `vendor_sale`; a partial sale reduces the stack. The wallet is credited in the
  same transaction.
- Carried acquisition places items that no client slot names: it tops up
  existing policy-free stacks in the permanent inventory and then the equipped
  Bag, then fills empty matching specialized Bag slots, permanent inventory
  slots, and general Bag slots, never the Secure Container. When the quantity
  does not fit, the transaction rejects with `inventory_full` and changes
  nothing. The 140 percent hard cap still applies.
- Purchases and sales are idempotent by operation id like every other item
  mutation, so a retry never charges or pays twice. Synthetic development bots
  cannot trade.

## Resource Nodes And Gathering

- A resource node is a world actor of kind `resource_node` with exactly one
  `gathering` capability whose `profileId` names a resource profile in the
  actor catalog: required tool kind, yield definition and quantity, cycle time,
  charges, and respawn delay. The Iron Vein yields one Iron Ore every 3 seconds
  with a pickaxe, has 5 charges, and returns 30 seconds after it depletes. The
  Oak Tree yields one Oak Log at the same pace with a Woodcutting Axe, the
  `woodcutting_axe` tool kind.
- Charges are live Shard state in `WorldActorStore`, shared by every gatherer.
  Each node keeps remaining and reserved charges and a generation that advances
  on every depletion, so a stale completion or respawn never touches a newer
  cycle. A worker restart restores every node with full charges.
- A `CapabilityAction` `StartGathering` passes the usual interaction checks, and
  the gathering handler rejects a depleted node with `resource_node_depleted`.
  The realtime loop then registers one gathering activity per player and sends
  `GatheringState` `Started` with the cycle time.
- The gathering pass runs every tick after the lease revalidation. A due cycle
  reserves one charge and queues a `gather_resource` call to GameService behind
  the player's other authority work. GameService checks that an item of the
  required tool kind is equipped in the `tool` slot, otherwise
  `gathering_tool_required` with a message that names the kind, such as
  `Woodcutting Axe required in the Tool slot.`, which the worker passes on in
  `GatheringState` `Stopped`. It places the yield through carried acquisition.
- A committed yield consumes the charge, applies the committed carry state, and
  sends `GatheringState` `Yielded` with the item-state revision. The last charge
  makes the node inactive for every viewer, stops everyone gathering there with
  `resource_node_depleted`, and schedules its respawn in `WorldActorTimerQueue`.
  The respawn restores the charges and makes the node active again.
- Moving more than 0.1 m from where gathering started stops it with
  `gathering_interrupted`, and a closed interaction with
  `world_interaction_closed`. A rejected yield releases the charge and stops
  with the rejection code. An uncertain GameService outcome retries up to three
  times with the same operation id, so a lost answer never grants twice, and a
  rejection that ends the item session disconnects the player.
- Synthetic development bots cannot gather.

## Target-Based Combat

SimulationWorker loads the combat rules from `SimulationWorker:CombatRulesPath`
before binding UDP. The default is `WorldData/Combat/combat-rules.json` in the
worker output, which the build copies from `content/combat/combat-rules.json`.
The loader rejects unknown and duplicate properties, another format version,
values out of range, and rule set ids that are not sorted, lowercase, and
unique. The rules hold the player maximum health of 100, the respawn delay of 5
seconds, the combat timeout of 10 seconds, regeneration of 2 percent of the
maximum health per second, the equipment lockout of 1 second, the arrow batch
interval of 5 seconds, and the Shard rule sets. Weapon values come from the item
catalog and Mob values from the actor catalog.

- The Shard rule set `mvp-open-risk` allows attacks on other characters.
  `JoinAccepted` carries the rule as `ShardRules`.
- A `CombatIntent` `AttackTarget` selects a target that the peer sees, that is a
  combatant, alive, and not the attacker itself, and that is a character only
  when the rule set allows it. `ClearTarget` clears the target. The target stays
  selected until it dies or leaves, or the rule set no longer allows it.
- On every tick, a character with a target attacks on its weapon's cooldown when
  it stands still, the target's bounds are within the weapon range, the line of
  sight from eye height reaches the target's center, a weapon is equipped in
  `main_hand`, and the equipped arrows belong to the weapon's ammunition family.
  Otherwise the owner sees the attack status that holds the attack. Line of sight
  costs a raycast, so it is checked when an attack is due and five times per
  second otherwise.
- An attack rolls damage between the weapon's minimum and maximum and adds the
  arrow's bonus percent, rounded half up. The Training Shortbow deals 10 to 18
  every 2.4 seconds at up to 10 m, and a Bronze Arrow adds 5 percent. The arrow
  flies at the weapon family's projectile speed of 30 m/s and hits at its impact
  tick when the target is still alive. It is spent either way.
- A different item in any equipment slot holds attacks for the lockout, and an
  item or corpse operation that may change equipment holds them while it runs.
  A changed arrow count keeps the same item and does not count.
- A shot counts against the committed arrow stack at once, so the owner sees the
  live count in `OwnerCombatState`. The spent arrows commit as one batch through
  `ammunition-consumptions` 5 seconds after the first uncommitted shot, before
  any other item or corpse operation of the character, before a leave, and
  before a disconnected session is released, all through the character's
  authority lane. A committed batch sends the owner `OwnerItemQuantityChanged`.
  An uncertain batch retries with the same operation id up to 8 attempts, with a
  backoff from 0.5 seconds that doubles up to 10 seconds; after that its arrows
  stay with the player. A rejected batch loses its shots, and a rejection that
  ends the item session disconnects the player. An item intent or an NPC or
  vendor action whose expected revision only arrow batches advanced is sent with
  the newest revision instead. Corpse operations carry no expected revision.
- A hit that brings health to 0 kills at once, so a later arrow of the same tick
  neither hits nor takes the kill, and a character killed by an arrow does not
  shoot in that tick any more. The killed character stops moving, its
  world interaction and corpse view close with `combat_character_dead`, its
  waiting operations are rejected with that code, and so are its new item,
  corpse, world, and combat intents. The death commits through `player-deaths`
  with a death event id that the worker creates once and retries with backoff
  until it commits, together with the shots that are not committed yet and the
  killer. After the respawn delay, and once the death committed, the character
  respawns at the World spawn with full health, and every viewer receives its
  respawn.
- A combatant is in combat until 10 seconds after its last attack or hit. A
  living character out of combat regains 2 percent of its maximum health, at
  least 1 point, every second.
- Every damageable Mob with a combat profile is a combatant, and a Mob with a
  brain and a locomotion profile also thinks and walks, as
  [Mob Brains](#mob-brains) describes. A dead Mob rolls its loot profile, where each
  entry drops with its chance in basis points and a quantity between its bounds.
  A non-empty roll becomes the Mob's corpse: a live corpse in worker memory, or
  for a boss a durable corpse in GameService that retries with the same death
  event id until it commits. The Mob then leaves every view, a Mob without a
  corpse at the end of its death tick, after its death reached the viewers, and
  its spawn
  instance returns after its respawn profile's delay, 30 seconds for the Feral
  Wolf and 300 seconds for the Feral Alpha, with a new runtime and entity id. An
  empty roll leaves no corpse.
- A disconnect does not cancel combat work. The session stays until the death
  and the last arrow batch committed and is released afterwards, and a leave
  waits for the same work. A worker that stops commits the uncommitted shots it
  can still name exactly, as a best effort, including the shots behind a batch
  whose answer arrived while it stopped.
- `CombatantState` follows every spawn of a combatant, and every changed
  combatant reaches its viewers once at the end of the tick. Combat events reach
  every peer that sees the source or the target, and `OwnerCombatState` reaches
  the owner.
- Synthetic development bots are no combatants, because they have no durable
  character that could die. An attack on one returns
  `combat_target_not_attackable`.

## Mob Brains

A Mob with a brain profile and a locomotion profile runs a brain in
SimulationWorker. Nothing gets its own task, timer, or thread:

- The activity tiers decide who works. A dormant Mob neither thinks nor moves; a
  coarse check on the activity profile's dormant rate makes it active when a
  player comes within the activation range, 48 m for `mob.standard`, and dormant
  again when none is within the dormancy range, 64 m. A Mob that fights, walks
  home, or died stays active whoever is near. One grid of player positions per
  tick answers these checks and detection, instead of comparing every Mob with
  every player.
- An active Mob thinks at the activity profile's active rate, 10 per second,
  staggered by entity id, and a moving Mob steps every tick with the player motor
  and collision at its locomotion profile's speed and capsule. Mobs do not
  collide with players or with each other.
- The brain is a state machine: `Idle` stands at the spawn, `Patrol` walks the
  spawn's patrol path with its waits, `Chase` walks toward the target, `Attack`
  stands still and bites on the combat profile's cooldown, `Evade` walks home,
  and `Dead` waits for the corpse and despawn.
- An aggressive brain detects the nearest living player within its detection
  range, 8 m for the wolves and 10 m for the Feral Alpha, with line of sight.
  Damage starts a fight for every brain. When the target dies or becomes
  invalid, the Mob takes the attacker with the most damage that is still alive,
  then the nearest detected player, and evades otherwise.
- A chasing Mob walks to half its attack range from the target and plans again
  when the target moved more than 1.5 m from the planned goal. In range with
  line of sight it stands, faces the target, and combat lets it bite: the bite
  lands at once, rolls the combat profile's damage, and may kill the character
  with the Mob as the recorded killer.
- The leash and the give-up time end a fight. A Mob that is farther than its
  leash range, 30 m, from where the fight started, or that neither bit nor was
  hit for its give-up time, 10 seconds, evades: it clears its target, cannot be
  damaged, walks back, and on arrival has full health and returns to its
  routine. A Mob that cannot walk home after three routes is placed there.
- Routes come from the shared `MovementPathPlanner`, one planner per locomotion
  profile, through one queue that plans at most 8 routes per tick. A Mob has at
  most one waiting request. Spawn points and patrol points are projected onto
  the ground under them, and a Mob starts to move only once the collision under
  it is loaded; walking Mobs keep their collision loaded like players.
- A moving Mob travels in the unreliable `SimulationSnapshot` like a remote
  player, and the snapshots change neither the state nor the interaction
  revision. The first tick it stands still sends one reliable `WorldActorState`
  with its rest pose, which advances the state revision but never the
  interaction revision.

## Server-Authoritative Movement

- Clients send input sequences and click-to-move paths, never accepted
  positions. Each input names the followed path by id, and a batch carries the
  waypoints of the newest input's path until the owner snapshot reports it.
- SimulationWorker runs the shared movement code at a fixed 30 Hz by default.
- It accepts only newer input sequences. A new path must keep its waypoints in
  the World bounds and stay within 256 meters from the authoritative position
  through every waypoint; a path that breaks the limits is adopted as already
  ended, so client and worker agree that the character stands. An input that
  names a path the worker never received is a protocol error.
- The worker steps the character exactly once per input, in sequence order, as
  the client prediction does. Inputs wait in a short queue that absorbs uneven
  packet timing; a tick applies the oldest one, and a second one while more than
  one still waits. Stepping once per tick with the newest input instead made
  the worker take more or fewer steps than the client whenever packets arrived
  unevenly, and the local player was corrected back and forth by whole steps.
- When the next input is late, the tick still steps along the current path, at
  most three times in a row, about 100 ms. The late input then takes over that
  step, or the steps are taken again with it when it names another path. A
  longer gap pauses the character on the worker until the inputs arrive, and the
  queue then catches up at two steps per tick. Inputs lost from every batch are
  filled with the path before them, up to 15; a longer gap skips to the newest
  input.
- A step budget keeps the average at one step per tick, with 1/64 of slack for a
  client clock that runs slightly fast, so a client cannot move faster by
  sending more inputs. A client may have at most 16 inputs waiting.
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
- Unity predicts with the same source, reconciles only from owner snapshots,
  and interpolates remote state at its delivered cadence.

## Collision Data And Streaming

- Each `content/worlds/<WorldId>/collision.json` contains neutral collision
  source for that World.
- `tools/WorldCollisionCompiler` emits versioned binary chunks and a manifest
  below `src/Shared/WorldData/Generated/WorldCollision/<WorldId>`.
- Each chunk has a SHA-256 checksum.
- The manifest has a deterministic complete collision revision.
- SimulationWorker validates format, World id, checksums, coordinates, and
  revision before binding UDP. It recomputes the revision from the manifest
  chunk list and rejects a manifest whose stored revision differs.
- Unity validates and loads the same data, with the same revision check, before
  prediction.
- Collision data stays within plus or minus 1,000,000 world units, chunk sizes
  stay between 1 and 4096, and one shape may span at most 65,536 chunks. Chunk
  coordinates are clamped to plus or minus 2^24, so .NET and Unity map every
  position to the same chunk, and a query wider than the loaded set scans the
  loaded chunks instead of every cell in its range.
- The character motor derives its substep count from the full displacement
  length, so no substep, diagonal or not, exceeds the configured limit.
- Shared simulation math wraps every float intermediate in an explicit
  `(float)` cast. Unity's Mono runtime keeps float intermediates in double
  precision and rounds only stored values, while .NET rounds every operation,
  so without the casts Unity prediction drifted from the worker. Movement is
  now bit-identical in both runtimes. Sine, cosine, and arctangent come from the
  platform C runtime in double precision and are rounded to float, so a libm
  difference between platforms only matters in the rare case where it crosses
  a float rounding boundary.
- Static shapes are assigned to every intersected chunk and deduplicated by
  stable id during queries. A chunk with more than 32 shapes indexes them in a
  planar grid of about 2 m cells, so terrain chunks with thousands of triangles
  stay cheap to query.
- Position-driven load and larger unload radii provide streaming hysteresis.
- A composite collision world combines static chunks and mutable dynamic shapes.

Collision format version 2 holds oriented boxes and one-sided triangles. The
bake turns non-convex MeshColliders into triangles and TerrainColliders into
heightfields. The shared compiler triangulates heightfields, two triangles per
cell except for holes, so Unity and .NET produce identical chunks. A triangle
collides only from its front, so a tunnel can run below walkable terrain.
Development World 1 uses only boxes. Development World 2 adds the terrain and
tunnel of its highlands. Replicated dynamic transforms are not
implemented.

## Item Catalog, Persistence, Account APIs, And Live Mutation

The item foundation is implemented. Offline account mutations, shared live encumbrance, and the authoritative in-world mutation
boundary plus persistent Unity inventory state and temporary presentation are
available together with durable player-death partition, corpse restoration,
concurrent inspection, and authoritative looting:

- `content/items` is the strict neutral authoring source: `catalog.json` for the
  catalog vocabulary, tool kinds, and economy settings, one
  `definitions/<definition-id>.json` file per item, named after its id, and one
  `vendors/<vendor-id>.json` file per vendor profile.
- `tools/ItemCatalogCompiler` rejects unknown or duplicate JSON properties,
  missing values, invalid identifiers and references, duplicate ids and Bag
  slots, negative or decimal weights, invalid stack limits, and impossible Bag,
  equipment, policy, or Secure Container combinations.
- The compiler emits sorted deterministic runtime content under
  `src/Shared/WorldData/Generated/Items`, including one catalog revision and structural
  fingerprints for every item definition and Secure Container tier.
- The checked-in catalog has 31 fantasy definitions, fourteen equipment
  slots including a stack-holding ammunition slot, the potion, food, material,
  and ammunition tags, the `pickaxe` and `woodcutting_axe` tool kinds, one Bag layout with general and
  specialized slots, the four-slot base Secure Container tier shown as Warded
  Pouch, one vendor profile, and the economy settings: the currency display name
  Gold and a starting balance of 100.
- Every definition has a `value` in whole currency units, where 0 means no
  vendor trades it, and optional tool kinds, which only items for the `tool`
  equipment slot may have. A vendor profile lists at most 32 ordered offers, its
  purchase and sale prices in basis points of value, and the categories it buys.
  The compiler rejects offers without value, with default policies, or of quest
  items, and rejects vendor content whose sale rate is not below every purchase
  rate, so buying and selling back never creates currency. Values, tool kinds,
  and vendors are not structural, so changing them needs no item migration.
- Framework-neutral pure rules cover stack compatibility, specialized slot tag
  acceptance, equipment compatibility, Secure Container eligibility, empty and
  non-empty Bag destinations, Bag containment-cycle rejection, unitless integer
  stack weight, exact 140 percent admission, and the fixed-point linear
  encumbrance multiplier. The base character capacity, `2000`, is catalog
  content.
- `MMORPG > Tools > Item Catalog` edits definitions through an Editor-only
  assembly, invokes the strict shared compiler, shows display-only and structural
  changes, locks baked ids, and writes authoring plus deterministic bake output
  transactionally.
- `MMORPG > Tools > Inventory Item Grants` lists initialized local
  characters with their balance and active definitions, grants individual
  stacks, gives currency through the character wallet, and applies
  deterministic inventory, equipment, stack, encumbrance, Secure Container, and
  Recovery packages. It can also seed an empty offline character and submit the
  normal system-death adapter to create a durable five-minute corpse at a chosen
  local Shard position. Its short-lived Development commands are loopback-only,
  require offline targets, use authoritative transaction services, and add no
  gameplay HTTP surface.
- The client presentation catalog is bundled through Unity Addressables. It maps
  stable definition ids to optional icons, localization keys, fallback text,
  and optional prefab presentation keys with its own deterministic revision
  tied to the exact gameplay catalog revision.
- GameService bundles the checked-in runtime gameplay catalog, revalidates its
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
- A companion migration removes the obsolete row-level 140 percent
  check from `character_item_states`. The transaction kernel remains the only
  hard-cap authority so an authoritative death can persist retained Secure
  Container weight after its equipped Bag capacity bonus is removed.
- Startup idempotently backfills every active character with the catalog's
  character settings, today base carry capacity `2000`, 20 permanent inventory
  slots, and 40 bank slots, plus the account-entitled Secure Container slots and
  one unbounded Recovery Storage identity.
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
  bank, Secure Container, Recovery deliveries, revisions, encumbrance state, and
  the currency balance from one repeatable-read snapshot.
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
  quest-grant abandonment cleanup, player-death partition, corpse expiry, vendor
  purchase and sale, and gathering yields.
- Equip moves a whole stack only when it fits the equipment slot limit, which is
  the smaller of the slot maximum quantity and the definition stack size. Merge
  accepts an equipped target up to that limit and an equipped source, and split
  accepts an equipped source, so arrows top up and partly leave the ammunition
  slot. Equip onto an occupied slot is a swap in the same transaction: it locks
  both items, checks the new item as for an empty slot apart from the occupant it
  replaces, and moves the occupant into the new item's source slot under the
  unequip placement rules, with deferred unique constraints for the exchange.
  The expected character revision makes sure the swap replaces the occupant the
  client saw. Equipping into a slot that an equipped item blocks, or with an
  item that would block an occupied slot, is rejected with
  `equipment_slot_blocked`. A corpse equipment slot keeps the same quantity
  limit. `loot_corpse_item_to_equipment` loots a whole corpse item into an
  equipment slot under the same checks: the occupant the worker names must still
  hold the slot, an equipped stack of the same stackable definition only takes a
  top-up and is rejected when the stacks cannot merge, so a full equipped stack
  never lands in the corpse, and any other occupant swaps into the corpse slot the loot left
  under the deposit placement and policy rules. `grant_mob_loot` grants into an
  empty container slot or, with an equipment slot instead, an empty equipment
  slot under a derived item id, or tops up the stack the worker names in that slot
  when the two merge within the slot limit. It never swaps, because the worker
  cannot take live Mob loot back, so a named stack of another item is rejected
  with `item_slot_occupied` or `equipment_slot_occupied`.
- Every path into a character's equipment slot, including corpse Bag swaps and
  cross-character Bag aggregate swaps, compares the definition's mirrored
  `required_character_level` with `characters.level` and rejects a lower level
  with `equipment_level_requirement_not_met`.
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
- A retry of a committed SimulationWorker operation replays its stored result
  even when the live session has ended since, so a lost response never turns a
  committed change into a reported failure.
- Committed operations recompute carried weight with indexed lookups of the
  character's carried containers and equipped Bag, and the death partition
  selects its source items the same way. A partial index on
  `item_instances(equipped_character_id, equipment_slot_id)` supports both.
- GameService startup bootstraps only characters that have no item state, each
  in its own short transaction, and then recomputes carry state in batches of
  500 rows that it locks first, so a concurrent item transaction can never be
  overwritten with a stale weight and startup scales with the number of
  accounts.
- Two kinds of rejection are returned without being stored, so the same id can
  run again: `item_transaction_timeout`, which also covers lock timeouts,
  statement timeouts, deadlocks, and serialization failures, and every
  rejection of a retried system operation. The retried system operations are
  player death, persistent Mob corpse creation, live Mob loot grants, corpse
  expiry, and Recovery expiry. A live Mob loot grant therefore stays claimable
  after a rejected attempt until one claim commits.
- Player-chosen operation ids, from account routes and from SimulationWorker
  requests other than the retried system operations, must be random version 4
  UUIDs or the command returns `item_operation_id_invalid` without claiming the
  id. Every derived system id is a version 5 UUID, so a player cannot claim an
  id that a system operation needs.
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
  Player reads return only unclaimed deliveries. A claimed delivery is kept as
  history for the audit retention period and then removed by maintenance.
- Secure Container tier changes update every active character on the account.
  An account actor may only select the catalog base tier. A larger tier is
  granted only by a trusted entitlement or purchase source with system
  authority.
  Removed slots are drained in descending order to deterministic
  `secure_capacity_reduction` Recovery deliveries before the slots are removed.
  Character bootstrap and tier changes share one account-entitlement advisory
  key so a concurrently created character receives a coherent tier.
- `ItemPolicyRules` independently evaluates trade, auction, vendor sale, player
  destruction, death disposition, insurance eligibility, and stacking
  capability. Protected and insured items block every transfer capability.
- Policy and quest commands run only through the NPC capability path or as a
  system actor. Insurance removal changes policy lifecycle without
  replacing the item. Quest abandon removes only active protected items with the
  exact quest-grant source id, and reaccept suppresses duplicate active grants.
  Abandoning an item held in Recovery Storage also removes its slot row and any
  unclaimed delivery left without items, so no empty delivery remains.
- Account mutation requests derive authority from the authenticated principal
  and use an offline-required actor. Character and item-state rows are locked
  before checking active simulation sessions, using the same character lock as
  simulation admission. Active ownership returns
  `item_offline_access_required` without partial mutation.
- Character-specific reads and writes return `Cache-Control: no-store` and
  `Pragma: no-cache`. Domain failures use RFC Problem Details with stable `code`
  values. GameService never returns icon bytes, Unity references, or client
  presentation entries.
- Admission returns one item-state-row-fenced carry tuple with the accepted
  session lease. Heartbeat returns the current committed tuple with the renewed
  lease. Reconnect reads the tuple again under the admission lock.
- SimulationWorker stores carry state by exact simulation-session identity and
  applies only increasing item-state revisions. Same-revision conflicts
  invalidate the local lease instead of accepting contradictory weight or
  capacity.
- GameSimulation, at revision `movement-simulation-v5` and with carry state
  since `v3`, owns the immutable carry state and deterministic encumbrance
  calculation used by worker movement and Unity prediction. Walking keeps full
  speed through exactly 100 percent load, then the movement multiplier falls
  linearly to `0.20` at the exact 140 percent hard cap.
- The realtime protocol retains carry state on join, later committed
  carry updates, and bounded item-operation intents and results on the reliable
  ordered control path. Supported operations are relocate, equip, unequip,
  split stack, merge stacks, atomic ordinary container-slot swap, allowed
  destruction, complete Recovery Storage claim, typed NPC insurance and quest
  item-lifecycle actions, and vendor purchase and sale with offer, quantity, and
  expected price. `GatheringState` reports gathering start, each yield, and the
  stop reason. The corpse contract carries
  chunked corpse presence and view state, accepted destination slot tags, open,
  close, refresh, full and partial loot or deposit, atomic ordinary slot and Bag
  aggregate swaps, operation result, delta, and view-closure messages.
  Packet builders measure encoded UTF-8 size and preserve the existing `1200`
  byte limit.
- SimulationWorker accepts item intents only from an exact joined player on
  channel 0, processes at most one per peer at a time, and bounds each pending
  queue to eight operations. Synthetic development bots cannot mutate durable
  items.
- Bank and Recovery Storage custody require the matching access flag. The worker
  sets it when the operation starts while the player has an open interaction
  with a world actor whose `bank` or `recovery_storage` capability is available.
  The interaction closes when the player leaves its range or loses line of
  sight, which ends the access. Opening either capability only confirms it, so
  the client can present the storage. Secure Container custody has no city
  requirement. Insurance and quest lifecycle actions pass their flags only
  through their own NPC capability, and vendor trades pass the vendor profile
  of the validated `vendor` capability, which must name an active vendor of the
  mirrored catalog. A vendor command without that live vendor access is
  rejected with `vendor_access_required`.
- The worker service-authenticated endpoint carries the exact account,
  character, simulation session token, worker id, runtime id, and Shard. The
  authenticated worker id must match the body. GameService locks and validates
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
  the event id with different data returns `death_event_conflict`. The event
  row is removed together with its closed corpse after the closed-corpse
  retention period. Every death request carries the expected character
  revision and the partition always advances it, so a replay that outlives the
  event row is rejected as a revision conflict instead of partitioning twice.
- One death transaction leaves currency and its ledger, bank, existing Recovery
  Storage, and Secure Container contents unchanged. Protected items and otherwise-lootable
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
  open, unexpired corpses for its exact runtime and Shard assignment. GameService
  returns database time with the snapshot. The worker advances that time with a
  monotonic clock and retains the generic loot-crate presentation key without
  owning or copying item custody.
- SimulationWorker exposes corpse presence only within configured interest
  range. One player can inspect one corpse at a time, while any number of
  players may inspect the same corpse. The worker revalidates its authoritative
  three-dimensional position and cached absolute lifetime before every open,
  refresh, or mutation. GameService repeats exact-session, Shard, and lifetime
  authority inside the transaction boundary.
- Corpse reads use short read-only snapshots. Full and partial transfers,
  internal corpse moves, ordinary occupied-slot swaps, and atomic Bag aggregate
  swaps reuse `ItemTransactionService` with targeted item, container, and
  Bag-root revisions. Internal moves can split, merge, or swap across the three
  corpse sections without advancing character item-state or carry. The corpse
  Bag owns the corpse Bag section, so it never leaves its equipment slot through
  an internal move or swap, even when empty; only an atomic Bag aggregate swap
  replaces it. Corpse equipment destinations carry canonical equipment-slot ids
  and validate definition compatibility. Unrelated corpse changes may commit concurrently,
  competing custody has one stable winner, and no PostgreSQL transaction remains
  open across a client network wait.
- The worker broadcasts each committed targeted delta or replacement to every
  current viewer. An open or refresh always gives its requester a complete
  snapshot, while the other viewers receive the delta from the newer revision.
  Its runtime store accepts only monotonic corpse revisions. A completion that
  arrives after its peer disconnected never releases the corpse reservation of
  a new peer that reuses the LiteNetLib peer id; disconnect cleanup releases
  every reservation of the old peer first.
  Expiry, invalidation, and missing-corpse results close affected views with
  stable codes, while stale item or quantity conflicts trigger refresh. Taking
  the last item of a live Mob corpse closes every view with `corpse_emptied`
  instead of `corpse_invalidated`, also when the presence broadcast notices the
  emptied corpse first. The emptied corpse stays inactive in worker memory until
  its deadline, so a replayed creation cannot restore its loot.
  [Corpse View Closures](REALTIME_PROTOCOL.md#corpse-view-closures) lists the
  codes.
- Every worker mutation response, from item operations, ammunition batches,
  corpse operations, live Mob loot grants, and player deaths, carries
  `characterState`: the acting character's item-state revision, carry tuple,
  and equipment, read right after the commit. A later commit can make it newer
  than the operation's own revision but never older, and SimulationWorker
  rejects a response whose state is older, belongs to another character, or has
  a duplicate equipment slot or item. Stored operation results stay unchanged,
  so a replay returns the same result with the current state.
- SimulationWorker counts arrows against the equipped ammunition stack and
  commits them in batches through `ammunition-consumptions`. The batch id is a
  random version 4 operation id, so a retry replays the stored result. The batch
  carries the expected character and item revisions and the quantity, and the
  stack must be ammunition that the character has equipped, otherwise the batch
  returns `422` `ammunition_not_equipped`. A batch that spends the whole stack
  deletes it and empties the ammunition slot.
- A player-death request may carry `ammunitionConsumption`, the shots that were
  not committed yet, which the death transaction consumes before it partitions
  the remaining stack into the corpse, and `killer`, either `mob` with an actor
  definition id or `character` with a character id. `death_events` stores the
  killer, with a character id in canonical form. Both fields are optional, and
  a death without them keeps its previous request fingerprint.
- SimulationWorker updates its carry-state store only from committed GameService
  results. A newer tuple is sent reliably before it affects movement. Conflicting
  same-revision data or stale live authority causes a targeted refresh response
  or safe disconnect.
- Unity keeps one persistent definition-id catalog index, presentation and icon
  cache, complete and focused snapshot state, monotonic observed revisions,
  structured errors, and one operation journal. It correlates operation
  results and refreshes authoritative HTTP state to at least the committed revision
  without optimistically changing item custody or quantity.
- Unity also owns immutable corpse presence and view state, complete chunk
  assembly, monotonic delta application, one pending corpse operation, and
  refresh-on-stale behavior. A worker closure that arrives before the result of
  the pending operation ends the view but keeps the operation correlated, so the
  late result settles it without a disconnect and never reopens the closed view.
  A player corpse shows the body lying in the last pose of `Death` and a Mob
  corpse a replaceable generic capsule, both at the server identity and
  transform. A click on a corpse walks into range and opens
  it, and the
  same typed drag layer submits bidirectional full or partial transfers,
  corpse-internal moves, compatible stack merges, ordinary occupied-slot swaps,
  and Bag swaps. Corpse equipment buttons display the canonical slot label and
  id instead of an untyped number.
- The runtime uGUI panel is replaceable presentation over that state. It exposes
  Permanent inventory, equipment, equipped Bag, Secure Container, Bank, and
  Recovery Storage plus every current live mutation. Typed reusable drag sources
  and targets submit relocation, equipment, split, merge, atomic ordinary swap,
  and atomic Recovery claim operations only after drop validation. `B` shows
  character storage, `C` shows equipment plus character storage, and `I` shows
  the complete Development view with contextual Bank, Recovery, and an active
  corpse. Item clicks
  select only the split and allowed-destruction controls. Bank and Recovery
  reads are globally inspectable by the owning account.
- SimulationWorker normally derives Bank and Recovery Storage access from the
  open NPC interaction. Its environment-specific Development configuration can
  explicitly grant global Bank and Recovery access for local item testing.
  Enabling that option outside Development is rejected at startup, and insurance
  access always requires the insurance NPC capability. Every mutation still requires the joined worker path and GameService's
  exact session, runtime, assignment, transaction, item-rule, and capacity
  validation.
- Gameplay and presentation catalogs load once and must share a source revision.
  Server snapshots must match the same bundled gameplay revision or the client
  reports `item_catalog_update_required` and refuses stale item state.
- `--dev-items-package <characterId> <packageId>` is a process-local Development
  command, not an HTTP route. It applies one package from
  `content/development/item-packages.json` through `ItemTransactionService`,
  requires a new empty offline character, restricts PostgreSQL to loopback and a
  non-production-like database name, refuses replay after the first item or
  Recovery delivery exists, and rejects a package that misses its exact expected
  load.
- `--dev-items-corpse <characterId> <shardId> <x> <y> <z>` is the matching
  process-local corpse fixture. It applies the corpse seed package to an empty
  source when necessary, then uses the normal durable death transaction. The
  source must be offline and the resulting corpse keeps its database deadline
  across worker restart.
- `--dev-items-currency <characterId> <positiveAmount>` is the matching
  process-local currency grant described in
  [Character Currency](#character-currency).

The shared pure rules have no HTTP, PostgreSQL, UnityEngine, or SimulationWorker
runtime dependency. The Editor assembly is isolated from runtime WorldData
assemblies.
GameService exposes authenticated owned-character reads, offline account item
write routes, and one service-authenticated active-session item route. It exposes
no development grant route. The development item packages are applied by a
guarded local process command rather than a remotely callable service. SimulationWorker has no
inventory database access and holds no item collection. Unity is not an item-rule
authority.

## Item And Interaction Error Codes

Error codes are stable contracts for programs; messages are for people and may
change. GameService returns the item codes, SimulationWorker adds the realtime
and interaction codes, and Unity adds `item_catalog_update_required`.

| Area | Codes |
| --- | --- |
| Authority and session | `item_authority_required`, `item_offline_access_required`, `item_simulation_operation_invalid`, `wrong_simulation_worker`, `worker_runtime_changed`, `simulation_session_invalid` |
| Operations | `item_operation_id_invalid`, `item_operation_conflict`, `item_transaction_timeout`, `item_command_payload_too_large`, `item_operation_queue_full`, `item_state_diverged` |
| Item state | `item_not_found`, `item_not_owned`, `item_state_conflict`, `item_quantity_changed`, `bag_state_changed` |
| Placement | `item_slot_occupied`, `item_slot_incompatible`, `equipment_slot_occupied`, `equipment_slot_incompatible`, `equipment_slot_blocked`, `equipment_level_requirement_not_met`, `secure_container_item_forbidden`, `bag_not_empty`, `carry_weight_limit_exceeded` |
| Stacks | `item_stack_incompatible`, `item_stack_limit_exceeded` |
| Policy, quest, and currency | `item_policy_restricted`, `item_policy_not_found`, `item_destroy_forbidden`, `quest_grant_invalid`, `currency_insufficient`, `currency_limit_exceeded` |
| Vendors and acquisition | `vendor_access_required`, `vendor_unavailable`, `vendor_offer_unavailable`, `vendor_price_changed`, `vendor_will_not_buy`, `inventory_full` |
| Gathering | `gathering_tool_required`, `gathering_yield_invalid`, `gathering_action_invalid`, `gathering_unavailable`, `gathering_interrupted`, `gathering_failed`, `resource_node_depleted`, `world_interaction_closed` |
| Bank and Recovery | `bank_access_required`, `recovery_access_required`, `recovery_delivery_not_found`, `recovery_delivery_not_expired` |
| Death and corpses | `death_event_conflict`, `mob_corpse_invalid`, `mob_loot_grant_invalid`, `corpse_not_found`, `corpse_not_expired`, `corpse_state_changed`, `corpse_expired`, `corpse_invalidated`, `item_already_looted`, `corpse_out_of_range`, `corpse_view_not_open`, `corpse_interaction_active` |
| World interaction | `world_actor_not_found`, `world_actor_unavailable`, `world_interaction_invalid`, `world_interaction_active`, `world_interaction_out_of_range`, `world_interaction_line_of_sight_blocked`, `world_interaction_target_changed`, `world_interaction_capability_unavailable`, `world_interaction_capability_deferred`, `world_interaction_session_invalid`, `npc_lifecycle_action_invalid`, `quest_turn_in_unavailable` |
| Unity client | `item_catalog_update_required` |

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
unbounded GameService request fan-out. HTTP calls map timeout, connection,
invalid-response, authentication, and domain failures to structured results.
`auth_service_timeout` and `auth_service_unavailable` are unknown outcomes,
because GameService may have committed the operation before the response was
lost, so item, corpse, and live Mob loot results with those codes always ask the
client to refresh. Live Mob loot claims send the current committed item-state
revision from the carry store, not the revision from the time of the join.

## Metrics And Logs

Both server processes export metrics, logs, and traces through OpenTelemetry
over OTLP when `Telemetry:OtlpEndpoint` is set. The local Collector, Prometheus,
Loki, Tempo, and Grafana stack, the dashboards, the alert rules, the
configuration, and the playtest workflow are described in
[Monitoring](MONITORING.md). Without the setting nothing leaves the process and
the meters below only feed the periodic status logs.

The meter `Mmorpg.SimulationWorker.Realtime` exposes:

- Active peers and entities.
- Joined real players, joined synthetic bots, and unauthenticated peers as
  separate gauges.
- Sent and received packets and bytes, and the snapshot payload bytes among the
  sent bytes.
- Quota rejections, total dropped snapshots, and the subset dropped by
  aggregate snapshot backpressure.
- Accepted and rejected joins.
- Spawn and despawn packet counts.
- Snapshot entity record counts.

The meter `Mmorpg.SimulationWorker.WorldActors` exposes low-cardinality NPC,
Mob, and resource node population, event-driven, dormant, and active tier
population, active interaction count, accepted and rejected interaction totals,
interaction latency, depleted resource nodes, active gatherers, and gathering
cycles by the `outcome` `yielded`, `rejected`, or `retried`. It does not label
actor, spawn, player, character, session, or entity identities.

The meter `Mmorpg.SimulationWorker.Combat` exposes attacks, hits, and deaths by
the `kind` `player` or `mob`, arrow batches by the `outcome` `committed`,
`retried`, `rejected`, or `session_ended`, dropped shots, player death
partitions by the `outcome` `committed`, `retried`, `rejected`, or
`session_ended`, the combat phase duration, active combatants, shots that are
not committed yet, the Mob phase duration, engaged and moving Mobs, Mob routes
by the `outcome` `reached`, `closest`, or `no_path`, and routes that wait for
the planning budget.

The meter `Mmorpg.GameService.Items` exposes low-cardinality transaction
latency and outcome, lock wait, conflict, stale revision, timeout, death
partition, policy action, cleanup, corpse count, and Recovery backlog
measurements under `game_service.items.*`. The transaction counter and duration
carry the outcome `committed`, `rejected`, `failed` for a transaction that threw,
or `cancelled` when the caller cancelled it; the periodic totals count only
committed and rejected transactions. Their operation kind label includes
`vendor_purchase`, `vendor_sale`, and `gather_resource`. The meter
`Mmorpg.GameService.Redis` exposes session cache, Redis failure, and distributed
rate-limit counters under `game_service.redis.*`. The meter
`Mmorpg.GameService.Placement` reports, per Shard and labeled only by
`shard.id`, whether the Shard is online and its active
players and capacity, refreshed every `Simulation:MetricsIntervalSeconds` from
the same query as the public shard list. A failed refresh drops the values
instead of keeping stale ones. The meter `Mmorpg.SimulationWorker.Corpses`
separates active live and durable counts by the fixed `persistence` dimension.
None of these meters uses item, account, character, operation, corpse, session,
actor, or runtime ids as labels.

Every GameService request has one correlation id: the caller's valid
`X-Correlation-ID`, otherwise the W3C trace id of the request. The response
header carries it, every log entry written during the request carries it as the
`CorrelationId` scope value, and the request span carries it as
`mmorpg.correlation_id`. SimulationWorker sends the trace id of its operation as
`X-Correlation-ID` together with the W3C `traceparent` header, so one worker
operation, its GameService request, and that request's PostgreSQL commands form
one trace. Item transaction logs include correlation and operation ids, classify
expected contention below warning, promote significant lifecycle commits, and
exclude credentials, tokens, and command bodies. Periodic GameService summaries
report cumulative transaction, contention, lifecycle, corpse, Recovery, and
cleanup counts.

Periodic structured logs expose the same totals. A worker status line also
reports real players, synthetic bots, unauthenticated peers, process CPU
normalized to total logical-core capacity, single-core-equivalent CPU, working
set, packet and payload rates including the snapshot payload rate, snapshot
drops, quota rejections, and the sample window. Metrics deliberately avoid
account, character, session, entity, and peer identifiers as labels.

The performance meter `Mmorpg.SimulationWorker.Performance` records network
poll, completed-operation, join-queue-delay, join-finalization, simulation-tick,
tick-lag, collision-streaming, movement, interest, and snapshot-broadcast
durations plus fixed-tick resynchronizations and overruns, which count ticks
that took longer than the tick interval. It also publishes the current
completed-operation backlog as a gauge. The periodic metrics log reports
interval averages, approximate p95 and p99 upper bounds, and maximum durations.
It also reports the current and interval-maximum completed-operation backlog so
admission congestion is distinguishable from UDP loss. Snapshot broadcast is
the complete snapshot pipeline and includes the separately reported interest
phase. The same interval log reports process allocation, GC collection counts,
managed heap size and fragmentation, live managed memory, distinct visibility
groups, encoded snapshot packet count, sent snapshot packet count, and tick
overruns. Recording any of these measurements on the realtime loop does not
allocate, with or without the OpenTelemetry exporter attached.

These operational values remain server-side. They are not added to the realtime
gameplay protocol and are not broadcast to Unity clients. The Unity F2 panel
derives its values only from local rendering, prediction, and received traffic.

SimulationWorker filters routine successful `GameServiceClient` HTTP pipeline
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
- Local shared-secret mode authenticates any valid worker identifier and leaves
  topology authorization to the registry. When a per-worker credential map is
  configured, it becomes an exact allowlist and shared fallback is disabled.
- Secrets are compared in fixed time.
- Central middleware converts unhandled failures into RFC Problem Details.
- Responses carry `X-Correlation-ID`, including error responses produced by
  the exception handler. A request the client aborted is logged at debug level
  and is not reported as a server error.
- Token-bearing responses use `Cache-Control: no-store` and `Pragma: no-cache`.
- No debug HTTP endpoint is registered in the current service surface.

### Redis Account-session Acceleration

GameService uses Redis as a bounded cache-aside accelerator for successful
account-session validation. Token hashes, never bearer tokens, identify cache
entries. Every entry expires no later than both the configured cache TTL and the
durable PostgreSQL session expiry. A cache miss, corrupt value, tombstone, or
Redis read failure falls back to PostgreSQL.

Logout, manual revocation, and login replacement write a session-id tombstone
before committing the PostgreSQL revocation, then delete the cached token key.
The tombstone lives at least as long as any token cache entry. If GameService
cannot store the required tombstone, it returns
`503 session_cache_invalidation_unavailable` and does not commit the durable
revocation. This prevents a successfully revoked session from remaining usable
through stale cache state.

Login and registration also use a Redis fixed-window rate limit keyed by a hash
of the remote address and policy name. The existing process-local ASP.NET rate
limit remains active as a fallback if Redis is unavailable. Disconnected Redis
commands fail fast instead of accumulating in a client backlog, and repeated
warning logs are throttled per operation. Cumulative cache, fallback,
invalidation, Redis-failure, and distributed-rate-limit counters are logged at
the configured metrics interval.

## Health And Configuration

GameService liveness confirms the process loop. Readiness runs PostgreSQL
`select 1` and Redis `PING` within configured timeouts and returns HTTP 503 if an
obligatory dependency fails.

SimulationWorker `--health-check-only` checks GameService readiness, Redis, and
UDP port availability and exits 0 only when all checks pass.

All application settings fail fast. Checked-in non-secret defaults live in each
service's `Config` folder. The ignored root `.env` contains local credentials
and connection strings, not worker topology or World selection. Explicit
environment variables and command-line options have higher precedence.

SimulationWorker requires one valid `WorldData/Worlds/<WorldId>/world.json` in its
output folder for its configured `WorldId`, built from
`content/worlds/<WorldId>/world.json`. The manifest supplies the client scene, movement
bounds, and authoritative spawn. Actor and collision data resolve below the same World directory. This
prevents a shard restart on another World from silently retaining coordinates
from the previous map. Missing manifests, invalid bounds, out-of-bounds spawns,
or invalid runtime content fail before UDP admission.

GameService seeds World definitions from those manifests but does not select a
World for a shard. The worker proposes its desired `WorldId` in the heartbeat.
GameService validates that the World exists and atomically persists a first
binding or safe offline rebind while preserving assignment, ticket, session,
corpse, Fleet, and runtime-fencing boundaries. Unknown Worlds and blocked
rebinds are definitive worker authority failures.

`Items:Operations` bounds item statement and lock timeouts, canonical command
and HTTP request sizes, metrics and maintenance intervals, cleanup batch size,
and audit plus closed-corpse retention. Lock timeout cannot exceed transaction
timeout, and closed-corpse retention cannot be shorter than operation audit
retention. Invalid values stop GameService at startup.

## Persistence And Migrations

Migrations run under a PostgreSQL advisory transaction lock. Each migration id
is inserted only after its SQL succeeds. The topology migration preserves older
data while separating World content from Shard and SimulationWorker runtime
metadata. A later migration enforces one active simulation session per account.
Previously shipped migration ids and their source-schema references retain their
historical names because changing an applied migration would break upgrade
compatibility. The resulting current schema uses only the canonical topology
names listed above.

The development World identity migration replaces the legacy
`local-world-1` content identifier with `development-world-1` without changing
the shard id or runtime ownership. It is an identity upgrade for the same map,
not a live World rebind.

Partial indexes support completed operation retention, closed-corpse
retention, and unclaimed Recovery expiry. A bounded hosted maintenance service
uses deterministic system-authority operations to destroy expired Recovery
items with durable audit, removes empty closed corpses only after retention,
removes claimed Recovery deliveries after the audit retention period, and
deletes old operation rows only when no destruction or death record references
them. Every database unit uses statement and lock limits plus `skip locked`
batch selection where concurrent maintenance is permitted.

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
- Catalog tags and equipment slot ids are limited to 64 characters and Bag
  slots to 16 accepted tags, the realtime protocol limits. A test keeps the
  `ItemCatalogFormat` limits within `RealtimeProtocolLimits`.
- A movement determinism golden test runs one shared scenario in .NET and in
  Unity EditMode and pins `GameSimulationCompatibility.Revision` together with
  the same fingerprint in both, so a movement change without a revision bump,
  or a runtime that diverges, fails. A coverage test keeps the click paths
  blocked by a wall until the path ends, sliding along it, climbing and dropping
  off a rotated ramp, blocked by a rotated pillar, and stepping up while
  encumbered. A second fingerprint pins the route planner in both runtimes, and
  the motor follows every planned route to its end.
- Committed collision manifests are checked against their recomputed
  revision.
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
  Details, offline session fencing, offline bank and Recovery rejection, system
  delivery, successful kernel claims, and hard-cap claim rollback.
- Shared carry and movement tests for exact custody contribution, base and Bag
  capacity, lower-capacity Bag rejection,
  fixed-point movement reference points, monotonic active-session propagation,
  protocol validation, and reconnect restoration.
- In-world mutation protocol, HTTP client, worker socket, configuration, and isolated
  PostgreSQL tests for bounded intent decoding, committed results, every exact
  identity mismatch, stale assignment, duplicate intent replay, account-versus-
  worker races, bank and Recovery access, Secure Container carry changes, and
  reconnect restoration.
- Development item package tests cover package file validation, one real
  PostgreSQL package flow and readback, one-shot rejection, stale expected
  loads, that the checked-in packages fit the compiled catalog, and the
  Recovery delivery revision
  exposed to Unity. Unity EditMode tests cover catalog cache reuse, revision
  mismatch, snapshot validation and coherence, operation correlation,
  specialized and Secure Container targets, non-empty Bag rules, split weight,
  and the hard cap. PlayMode tests cover persistent controller and uGUI creation.
- Death partition tests cover migration idempotency and constraints, exact live service
  authentication, death-event replay, total item custody conservation, currency
  and Secure retention, protected and insurance precedence, insured equipment
  snapshots, Bag-root and child ordering, involuntary capacity-loss overflow and
  hard-cap remediation, cross-Shard restart restoration, database-timed
  empty-corpse lifetime, idempotent audited expiry, and a custody-versus-expiry
  race. Worker unit tests cover death and restoration response validation plus
  monotonic database-time expiry in the runtime store.
- Corpse transfer PostgreSQL races cover unrelated concurrent loot, same-item winners,
  partial stack conservation, Bag-versus-child and Bag-versus-Bag locking, the
  exact 140 percent hard cap, and equal competition by the dead character.
  HTTP, worker, protocol, and Unity coverage verifies exact authority, no idle
  transaction after mutation, viewer deltas, stable close errors, dynamic MTU
  packing, monotonic corpse state, fixture creation, typed drag payloads, and
  persistent presentation bootstrap.
- World actor coverage verifies deterministic actor compilation, strict malformed
  runtime rejection, identity scope separation, assignment reconstruction,
  interest presence without capability leakage, central Mob scheduling,
  composed capabilities and typed deferred dispatch, exact range and line of
  sight authority, rate and session rejection, per-player summaries, lease
  exclusion with corpses, closure causes, protocol round trips and bounds, Unity
  monotonic state and targeting, and Editor import, export, preview, compile,
  and verify through the shared compiler.
- NPC lifecycle coverage verifies exact-session insurance and quest lifecycle
  dispatch, atomic pricing, policy removal, protected grant lineage,
  abandonment, reacceptance, and safe source labels.
- Mob corpse coverage verifies content-controlled normal and boss corpse settings,
  worker-memory restart loss and expiry, deterministic grant replay with one
  persistent player item, durable boss restoration with unchanged custody and
  deadline, shared expiry, realtime presence integration, and Unity Actor Studio
  round trips.
- Operations coverage verifies bounded configuration, identity-free metric
  dimensions, pre-database payload rejection, stable lock timeout mapping,
  audited Recovery expiry and retention, `4,000` centrally scheduled actors,
  `128` concurrent NPC leases, eight simultaneous corpse claims, forty repeated
  Bag swaps, and a clean `100` bot Release network baseline.
- Vendor and gathering coverage verifies the catalog values, vendor profiles,
  and the arbitrage rule, pricing rounding and overflow, resource profiles and
  their item references, wallet credits and debits with ledger conservation,
  the starting balance, purchases, partial and whole stack sales, every
  rejection, concurrent sales of one stack, carried acquisition order and
  `inventory_full`, gathering with and without the tool and at the hard cap,
  the Development currency command, golden traces of purchase, sale, partial
  sale, and gathering, the worker's resource node charges, gathering cycles,
  depletion, respawn, movement and closed-interaction stops, uncertain retries,
  and one full-stack flow that buys a pickaxe, mines ore, sells it, and buys a
  potion.
- Combat coverage verifies the combat rules loader, damage rounding, tick
  conversion, loadout resolution, target bounds, loot rolls, the Shard rule set
  from the heartbeat, a wolf that dies, leaves a corpse, and respawns, the attack
  statuses for range and line of sight, the Shard rule for attacks on
  characters, arrow batches and the owner's arrow count, a player death that
  commits once and respawns at the spawn, shots that commit before a leave and
  after a disconnect, the shots behind a batch that finished while the worker
  stopped, the equipment lockout including the first weapon, arrows restocked
  by an item operation, a dead character's rejected operations, revision
  rebasing of item intents and vendor actions after arrow batches, the last
  arrow, the lethal arrow that alone takes the kill, a wolf without a corpse
  that despawns after its death reached the viewer, and bots that are no
  targets.
- Mob brain coverage verifies the player grid, the activity tiers with busy
  Mobs, the planning budget, and over real UDP a wolf that detects, chases, and
  bites a player while it travels in movement snapshots and comes to rest, a
  wolf that kills a player as the recorded killer, the leash and the evade home
  with full health, a defensive wolf that fights only back, and a crowd of 60
  wolves that all reach their target through the shared planning budget.

## Not Yet Implemented

- HTTP item grant routes. Development grants use the guarded process-local
  command, and gameplay grants come only from NPC quest, Mob loot, vendor
  purchase, and gathering paths.
- Profession experience and levels, gathering success chances, other
  professions, durable resource node state, limited vendor stock, dynamic
  prices, buyback, and player trading.
- Consume and use intents.
- Final inventory visual design, drag-and-drop polish, accessibility, and policy
  detail presentation. The current uGUI is temporary presentation only.
- Final combat and corpse visuals, a threat table, Mob abilities beyond one
  attack, and Mob groups that assist each other. Unity shows combat with
  temporary presentation.
- Zones, cross-zone handoff, or layers.
- Multiple workers cooperating on one shard.
- Production scheduler or fleet autoscaler.
- Alert notifications to a chat or paging service, and a shared or staging
  deployment of the monitoring stack.
- Durable unique actor state, the dialogue, crafting, and trainer capability
  business operations.
- Rigid-body collision and replicated dynamic collision transforms.

The item rules, including the planned and open ones, are documented in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md).
The implemented actor and interaction foundation and its deferred boundaries
are documented in
[NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md).
