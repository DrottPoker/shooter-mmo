# Monitoring

This document owns how GameService and SimulationWorker report what they do: the
telemetry configuration, the catalog of meters, metrics, log categories, and
traces, the local monitoring stack that collects them, the dashboards and
alerts, what to watch during a playtest, the cost on the realtime loop, and
security.

## Overview

```text
GameService ------\                       /--> Prometheus (metrics, alert rules)
                   OTLP gRPC :4317 -->  OpenTelemetry Collector --> Loki (logs)
SimulationWorker -/                       \--> Tempo (traces)
                                                 |
                                Grafana :3000 (dashboards, Explore, alerts)
```

- Both processes use the OpenTelemetry .NET SDK through the shared project
  `src/Server/Telemetry` (`Mmorpg.Server.Telemetry`). They push metrics, traces,
  and logs over OTLP gRPC to one OpenTelemetry Collector.
- The collector is the only receiver, so the services never depend on a storage
  backend. It forwards metrics to Prometheus through Prometheus' native OTLP
  endpoint, traces to Tempo, and logs to Loki. A staging or production
  environment can replace any backend by changing only
  `observability/otel-collector/config.yaml`.
- Grafana reads all three stores and loads the dashboards in
  `observability/grafana/dashboards` from the repository.
- Pushed metrics carry no staleness markers, so Prometheus runs with a 30
  second lookback: the values of a stopped process disappear from instant
  queries and panels 30 seconds after its last export instead of five minutes.
- Prometheus evaluates the alert rules in `observability/prometheus/rules`.
  Firing alerts show in Grafana under Alerting and on both dashboards. They are
  not routed to a notifier yet.

Export is off until `Telemetry:OtlpEndpoint` is set. Without it nothing leaves
the processes: the meters still feed the periodic status logs on the console,
and tests and ordinary runs are unaffected. These operational values stay
server-side: they are not part of the realtime gameplay protocol, and the Unity
Debug menu (F2) derives its values only from local rendering, prediction, and
received traffic.

## Configuration

Both processes read the same `Telemetry` section. Defaults live in each
service's `Config/appsettings.json`; `.env.example` sets the endpoint for local
development.

| Setting | Default | Behavior |
| --- | --- | --- |
| `Telemetry:OtlpEndpoint` | empty | OTLP gRPC endpoint of the collector, such as `http://127.0.0.1:4317`. Empty disables export. Only scheme, host, and port are accepted. |
| `Telemetry:MetricExportIntervalSeconds` | `10` | Metric export interval, 1 to 300 seconds. The local Prometheus forgets a series 30 seconds after its last sample, so keep this at 15 seconds or less with the local stack. |
| `Telemetry:TraceSampleRatio` | `1.0` | Share of new traces that are kept, 0 to 1. A trace started by a sampled caller is always kept, so a worker operation and the GameService request and database commands it causes stay together. |
| `Telemetry:ServiceInstanceId` | host name or worker id | Overrides the Prometheus `instance` label, 1 to 128 letters, digits, `-`, `_`, `.`, or `:`. GameService defaults to the lowercase host name and SimulationWorker to its `SimulationWorkerId`, so a restarted process continues its series. Two GameService replicas on one host must set different values. |
| `Simulation:MetricsIntervalSeconds` | `15` | GameService only: how often the Shard gauges re-read the shard list, 1 to 3600 seconds. |

The OpenTelemetry resource of each process carries `service.namespace=mmorpg`,
`service.name` (`game-service` or `simulation-worker`), `service.version`,
`service.instance.id`, and `deployment.environment.name` (the ASP.NET Core or
.NET host environment). SimulationWorker adds `mmorpg.fleet.id`,
`mmorpg.node.id`, `mmorpg.shard.id`, `mmorpg.world.id`, and
`mmorpg.worker.runtime.id`. Prometheus turns `service.namespace/service.name`
into the `job` label (`mmorpg/game-service`, `mmorpg/simulation-worker`),
`service.instance.id` into `instance`, and promotes the environment, fleet,
node, Shard, and World attributes to labels.

## Run The Monitoring Stack

Start the stack next to PostgreSQL and Redis:

```powershell
docker compose -f docker-compose.observability.yml up -d --wait
```

Expected result: five containers of the project `shooter-mmo-observability` run,
and Grafana answers on `http://127.0.0.1:3000` without a login. The stack
publishes only loopback ports:

| Port | Service |
| --- | --- |
| `127.0.0.1:4317` | OpenTelemetry Collector, OTLP gRPC |
| `127.0.0.1:4318` | OpenTelemetry Collector, OTLP HTTP |
| `127.0.0.1:9090` | Prometheus, including its alert list under `/alerts` |
| `127.0.0.1:3000` | Grafana |

Enable export in the services. `.env.example` already contains the line; add it
to an older `.env` if it is missing:

```text
Telemetry__OtlpEndpoint=http://127.0.0.1:4317
```

Then start GameService and SimulationWorker as
[Local Development](LOCAL_DEVELOPMENT.md#backend-services) describes. The
[production-like stack](DEPLOYMENT.md#telemetry) exports to this stack through
an overlay that joins its network, so it needs no setting in `.env`.

Expected result: both processes log `Exporting metrics, traces, and logs over
OTLP to http://127.0.0.1:4317/.` at startup. Without the setting they log
`Telemetry export is disabled. Set Telemetry:OtlpEndpoint to export metrics,
traces, and logs.` and nothing leaves the process.

Manual check:

1. Open `http://127.0.0.1:3000`. The home page is the **MMORPG Playtest**
   dashboard. Within about 20 seconds `Shards online` shows `1` and the tick
   panels show values far below the dashed 33.3 ms budget line.
2. Start population, for example with
   [Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md) or the Unity client. The
   `Players` or `Bots` stat, bandwidth, and packets rise within one refresh.
3. Register an account, as
   [Backend API Smoke Test](MANUAL_TESTING.md#backend-api-smoke-test) does,
   and copy the `X-Correlation-ID` response header. In Grafana Explore, choose
   Loki and run
   `{service_name="game-service"} | CorrelationId="<header value>"`. The account
   log entry appears; open it and follow **View trace** to the request with its
   PostgreSQL commands in Tempo.
4. Stop SimulationWorker. Within about three minutes `Shards online` drops to
   `0`, and `ShardOffline` and then `SimulationWorkerDown` appear in the
   **Firing alerts** table and under Alerting in Grafana. Start the worker again
   and both alerts resolve within about a minute.

Expected result: every step behaves as described.

Validate the stack configuration and run the alert rule tests without starting
anything:

```powershell
& ./tools/Verify-Observability.ps1
```

Expected result: `Observability verification passed: collector, Prometheus,
alert tests, and 2 dashboards.`

Stop the stack and keep its data, or remove the data too:

```powershell
docker compose -f docker-compose.observability.yml down
docker compose -f docker-compose.observability.yml down -v
```

Expected result: the containers stop; the second command also deletes the
stored metrics, logs, traces, and Grafana state.

Dashboards are provisioned read-only from `observability/grafana/dashboards`.
Edit the JSON there and run
`docker compose -f docker-compose.observability.yml restart grafana` to load a
change at once.

## Metrics

Metric names below are the instrument names. Prometheus replaces dots with
underscores and adds `_milliseconds` for millisecond units, `_total` for
counters, and `_bucket`, `_sum`, and `_count` for histograms, so
`simulation_worker.simulation.tick.duration` is queried as
`simulation_worker_simulation_tick_duration_milliseconds_bucket`. No instrument
uses an item, account, character, operation, corpse, session, actor, spawn,
entity, peer, or runtime id as a label.

### SimulationWorker

`SimulationWorkerTelemetry.MeterNames` lists the worker's meters:

| Meter | What it measures |
| --- | --- |
| `Mmorpg.SimulationWorker.Realtime` | Joined real players, synthetic bots, unauthenticated peers, peers, and entities; sent and received packets and bytes, and messages by message kind; UDP datagrams sent and received, their bytes, the sealing overhead, and the datagrams dropped by reason; snapshot payload bytes; quota rejections; snapshot drops and the backpressure share; accepted and rejected joins; rejected connections; peers that the worker disconnected by reason; spawn, despawn, and snapshot entity records, spawns that could not be built, and entities that waited to enter a view; the entity ids used. |
| `Mmorpg.SimulationWorker.Performance` | Fixed-tick duration, tick lag, overruns (ticks longer than the tick interval), resynchronizations, the duration of every tick phase, of the parts of the phases that time themselves, and of every step of a loop iteration, join queue delay and finalization, the completed-operation and corpse-mutation backlogs, the recipients of remote snapshots, encoded and sent snapshot packets, and deferred remote records, and the realtime loop's failures. |
| `Mmorpg.SimulationWorker.WorldActors` | NPC, Mob, and resource node population, population by activity tier, open interaction leases, accepted and rejected interactions, interaction latency, depleted resource nodes, active gatherers, and gathering cycles by outcome. |
| `Mmorpg.SimulationWorker.Corpses` | Active live and durable corpses held by the worker. |
| `Mmorpg.SimulationWorker.Combat` | Attacks, hits, and deaths by combatant kind, arrow batches and player death partitions by outcome, dropped shots, the combat and Mob phase durations, active combatants, shots that are not committed yet, engaged and moving Mobs, and Mob routes by outcome and in the planning queue. |
| `Mmorpg.SimulationWorker.GameService` | Whether GameService answers the registry heartbeat, its changes, the age of the last successful registry heartbeat, the durable actions refused while it did not answer, and the operations given up at the retry horizon. |

`RealtimeMetricNamesTests` pins every instrument name of these meters.

The instruments of `Mmorpg.SimulationWorker.Realtime`:

| Instrument | Type | Meaning |
| --- | --- | --- |
| `simulation_worker.realtime.peers` | gauge | Connected peers. |
| `simulation_worker.realtime.entities` | gauge | Simulated player entities. |
| `simulation_worker.realtime.entity_ids.used` | gauge | The entity ids that the worker gave out since it started, for players and world actors alike. An id is never given out twice, so the number only grows, up to 4,294,967,295; [Entity Ids](#entity-ids) says what happens then and what `SimulationEntityIdsHalfUsed` asks for. |
| `simulation_worker.realtime.players.real` | gauge | Joined real players. |
| `simulation_worker.realtime.players.synthetic` | gauge | Joined synthetic development bots. |
| `simulation_worker.realtime.peers.unauthenticated` | gauge | Peers that have not joined yet. |
| `simulation_worker.realtime.received.packets`, `.received.bytes` | counter | Received packets and bytes. |
| `simulation_worker.realtime.sent.packets`, `.sent.bytes` | counter | Sent packets and bytes. |
| `simulation_worker.realtime.sent.messages`, `.received.messages` | counter | Sent and received messages by kind, labeled `message` with the `RealtimeMessageType` name, such as `SimulationSnapshot` or `MovementInputBatch`, and only kinds with a message. A packet without a readable type, empty or with an invalid header, counts under `0`. |
| `simulation_worker.realtime.datagrams.sent`, `.datagrams.received` | counter | UDP datagrams that the transport's sealing layer sent and received: every message, merged or alone, and LiteNetLib's own packets, such as acknowledgements and pings. Received datagrams count also when the layer dropped them. |
| `simulation_worker.realtime.datagrams.sent.bytes`, `.datagrams.received.bytes` | counter | Every byte of those datagrams, the seal included. |
| `simulation_worker.realtime.datagrams.overhead.bytes` | counter | The bytes that the seal added in both directions: 25 per sealed datagram and 1 per clear one, as [Transport Security](REALTIME_PROTOCOL.md#transport-security) describes. |
| `simulation_worker.realtime.datagrams.dropped` | counter | Received datagrams that the layer dropped unanswered, labeled `reason`: `malformed`, `unauthenticated` (another key, altered bytes, or no keys for the endpoint), `replayed`, `clear_after_sealing`, and `clear_not_allowed` (a clear packet before the keys on another channel than the join's). A steady value above zero means a broken client or traffic on the path. |
| `simulation_worker.realtime.snapshot.bytes` | counter | Snapshot payload bytes among the sent bytes. |
| `simulation_worker.realtime.quota_rejected.packets` | counter | Packets rejected by a per-peer quota. |
| `simulation_worker.realtime.connections.rejected` | counter | Connection requests that the admission guard refused. |
| `simulation_worker.realtime.peers.disconnected` | counter | Peers that the worker disconnected by itself, labeled `reason`: `reliable_queue_full` when the peer's reliable queues held more than `Admission:MaxReliableQueuedPackets` packets, which a client that stops acknowledging reaches in about a second. Every one also writes a warning line with the queued count, so a player who was cut off can be found. Entities enter a view only while the connection's queue has room for them, as [Interest Management](SERVICE_FEATURES.md#interest-management) describes, so a crowd never reaches it, however slow the client acknowledges; a value above zero means reliable traffic of another kind that no budget bounds. |
| `simulation_worker.realtime.snapshot_dropped.packets` | counter | Dropped snapshot packets. |
| `simulation_worker.realtime.snapshot_backpressure_dropped.packets` | counter | The snapshot packets among them that the aggregate snapshot budget dropped. |
| `simulation_worker.realtime.joins.accepted`, `.joins.rejected` | counter | Accepted and rejected joins. |
| `simulation_worker.realtime.spawn.packets`, `.despawn.packets` | counter | Spawn and despawn packets. |
| `simulation_worker.realtime.spawn.failures` | counter | Spawns that could not be built for one connection, which then does not see that entity, as [Interest Management](SERVICE_FEATURES.md#interest-management) describes. Every one also writes an error line with the entity and the peer, so any value above zero needs a look. |
| `simulation_worker.realtime.interest.entries_deferred` | counter | Entities within a connection's enter radius that had to wait for a later refresh, labeled `cause`: `burst` when more entities waited than one burst of entries holds, and `queue` when the connection's reliable queue had no room for them. A big crowd or a burst of joins raises `burst`, and a client that acknowledges slowly raises `queue`, which tells the two apart; a quiet world raises neither. |
| `simulation_worker.realtime.snapshot.entity_records` | counter | Entity records in remote snapshots. |

The instruments of `Mmorpg.SimulationWorker.Performance`:

| Instrument | Type | Meaning |
| --- | --- | --- |
| `simulation_worker.simulation.tick.duration` | histogram, ms | Duration of one fixed tick. |
| `simulation_worker.simulation.tick.lag` | histogram, ms | How late a tick started against its schedule. |
| `simulation_worker.simulation.tick.overruns` | counter | Ticks that took longer than the tick interval. |
| `simulation_worker.simulation.tick.resynchronizations` | counter | Times the clock skipped time after five catch-up ticks. |
| `simulation_worker.simulation.tick_phase.duration` | histogram, ms | Every tick phase, labeled `phase` with the name it registered under, such as `collision streaming`, `movement`, `mobs`, `combat`, `interest`, or `snapshots`. A phase is timed only on the ticks on which it did its work: a periodic phase that was not due, such as the snapshots on every second tick, and a run that threw are no samples. At 30 ticks and 15 snapshots per second the snapshots run on the even ticks and `appearances`, `interest`, and `far states`, the newest states that viewers beyond the detail radius missed, on the odd ones, as [Realtime Server Structure And Threading Contract](PROJECT_ARCHITECTURE.md#realtime-server-structure-and-threading-contract) describes, so the two kinds of tick can be told apart by their phases. |
| `simulation_worker.simulation.tick_phase_part.duration` | histogram, ms | The parts of the tick phases that time where their own time goes, labeled `part` with `<phase>: <part>`, one sample per run of the phase. `interest`: `index` (the interest index brought to the entities' positions), `refresh` (the visibility of the connections whose refresh is due), and `spawns and despawns` (the spawns and despawns those refreshes send). `snapshots`: `records` (the entity records and the owner states), `owner snapshots` (gathering the recipients and their owner snapshots), and, summed over the recipients, `remote plans` (the scheduler's plan and its completion), `remote encoding` (the chunk packets), and `remote sends` (handing them to the transport). `corpse presence`: `collection` (the active corpses, sorted, and the views of expired ones closed), `selection` (every peer's corpses in range and its loot groups, compared with what it got last), and `sends` (the presences that changed, built and sent). |
| `simulation_worker.simulation.collision_streaming.duration`, `.movement.duration`, `.interest.duration`, `.snapshot_broadcast.duration` | histogram, ms | Four tick phases timed inside themselves, kept for the dashboards and the load tooling. The interest duration is the `interest` phase's index and refresh with their spawns and despawns, on the tick before each snapshot tick; the snapshot broadcast is the snapshot pipeline of the snapshot tick without it. |
| `simulation_worker.simulation.collision_streaming.pending_steps` | counter | Steps that a player or Mob did not take because collision chunks around it still loaded on the streaming loader, as [Collision Data And Streaming](SERVICE_FEATURES.md#collision-data-and-streaming) describes. With the default radii it stays at zero in play; a value above zero means the loader fell behind the movers. |
| `simulation_worker.loop.step.duration` | histogram, ms | Every step of a loop iteration outside the tick, labeled `step`: `network poll`, `completed operations`, `peer supervision`, `orphan sweep`, and `send flush`. |
| `simulation_worker.loop.network_poll.duration`, `.completed_operations.duration` | histogram, ms | Transport polling and completion processing of one loop iteration, also in `simulation_worker.loop.step.duration`. |
| `simulation_worker.loop.completed_operations.backlog` | gauge | Completed authority operations that wait for the loop. |
| `simulation_worker.join.queue_delay.duration`, `.finalization.duration` | histogram, ms | How long a join waited and how long its finalization took. |
| `simulation_worker.corpse.mutation.backlog`, `.active_aggregates` | gauge | Corpse mutations waiting in the per-corpse coordinator, and the corpses with a mutation in flight. |
| `simulation_worker.simulation.snapshot.remote_recipients`, `.packets_encoded`, `.packets_sent` | counter | Recipients that got a remote snapshot, the owner and remote snapshot packets built, and the snapshot packets sent; a packet built but not sent was dropped by a byte budget. |
| `simulation_worker.simulation.snapshot.entity_updates_deferred` | counter | Due remote entity records that a recipient's byte budget deferred to the next snapshot; a record deferred again counts again. |
| `simulation_worker.loop.failures` | counter | Exceptions that the realtime loop caught, as [Realtime Server Structure And Threading Contract](PROJECT_ARCHITECTURE.md#realtime-server-structure-and-threading-contract) describes, labeled `step`, the tick phase or loop step, such as `movement`, `snapshots`, or `network poll`, and `scope`: `step` when the whole run of the step was skipped, `player` when only one player left the Shard, and `peer` when only one peer was disconnected or one connection request refused. Every failure also writes one error line, so any value above zero needs a look. |

The instruments of `Mmorpg.SimulationWorker.WorldActors` and
`Mmorpg.SimulationWorker.Corpses`:

| Instrument | Type | Meaning |
| --- | --- | --- |
| `simulation_worker.world_actors.npc`, `.mob`, `.resource_node` | gauge | Actors of each kind in the worker's World; resource nodes available or depleted. |
| `simulation_worker.world_actors.event_driven`, `.dormant`, `.active` | gauge | Actors in each activity tier. |
| `simulation_worker.world_interactions.active` | gauge | Open interaction leases. |
| `simulation_worker.world_interactions.accepted`, `.rejected` | counter | Accepted and rejected interaction requests. |
| `simulation_worker.world_interactions.duration` | histogram, ms | Interaction latency. |
| `simulation_worker.resource_nodes.depleted` | gauge | Resource nodes that are depleted and wait for their respawn. |
| `simulation_worker.gathering.active` | gauge | Players with a running gathering activity. |
| `simulation_worker.gathering.cycles` | counter | Finished gathering cycles, labeled `outcome`: `yielded` when GameService committed the yield, `retried` when its outcome was uncertain and the cycle repeats with the same operation id, and `rejected` otherwise. |
| `simulation_worker.corpses.active` | gauge | Corpses the worker holds, labeled `persistence`: `live` or `durable`. |

The instruments of `Mmorpg.SimulationWorker.Combat`:

| Instrument | Type | Meaning |
| --- | --- | --- |
| `simulation_worker.combat.attacks` | counter | Released attacks, labeled `kind`: `player` or `mob`. |
| `simulation_worker.combat.hits` | counter | Hits that dealt damage, labeled `kind` of the target. |
| `simulation_worker.combat.deaths` | counter | Combatants that died, labeled `kind`. |
| `simulation_worker.combat.ammunition_batches` | counter | Finished arrow batches, labeled `outcome`: `committed`, `retried` when the outcome was uncertain and the batch repeats with the same operation id, `rejected`, or `session_ended` when the rejection ended the item session. |
| `simulation_worker.combat.dropped_shots` | counter | Shots that never committed because their batch was rejected, the session ended, or the arrow stack changed under a death. |
| `simulation_worker.combat.death_partitions` | counter | Finished player death partition attempts, labeled `outcome` like the arrow batches, and `abandoned` for a death given up at the retry horizon. |
| `simulation_worker.combat.phase.duration` | histogram, ms | Duration of the combat phase of one tick. |
| `simulation_worker.combat.active_combatants` | gauge | Characters and Mobs that can fight. |
| `simulation_worker.combat.pending_shots` | gauge | Shots counted against arrow stacks that GameService has not committed yet. |
| `simulation_worker.combat.mob_phase.duration` | histogram, ms | Duration of the Mob brain and movement phase of one tick, which runs before the combat phase. |
| `simulation_worker.combat.engaged_mobs` | gauge | Mobs that chase, attack, or walk home. |
| `simulation_worker.combat.moving_mobs` | gauge | Mobs whose pose changed in the latest tick. |
| `simulation_worker.combat.mob_path_plans` | counter | Planned Mob routes, labeled `outcome`: `reached`, `closest` when the goal cannot be reached and the route ends at the closest point, or `no_path`. |
| `simulation_worker.combat.mob_path_queue` | gauge | Mob routes that wait for the planning budget of 8 routes per tick. |

The instruments of `Mmorpg.SimulationWorker.GameService`, which
[GameService Outage](SERVICE_FEATURES.md#gameservice-outage) explains:

| Instrument | Type | Meaning |
| --- | --- | --- |
| `simulation_worker.game_service.available` | gauge | 1 while GameService answers the registry heartbeat, 0 while the worker counts it as unavailable. |
| `simulation_worker.game_service.availability_changes` | counter | Changes of that state, labeled `state`: `unavailable` when an outage began, `available` when it ended. |
| `simulation_worker.game_service.heartbeat_age` | gauge, s | Seconds since the last successful registry heartbeat; nothing before the first one. The worker stops in order once it passes `GameServiceOutageToleranceSeconds`, 120 by default. |
| `simulation_worker.game_service.refused_intents` | counter | Durable client actions refused with `game_service_unavailable`, labeled `kind`: `item_operation`, `corpse_interaction`, `world_interaction`, `quest_intent`, `gathering`, or `appearance`. |
| `simulation_worker.game_service.abandoned_operations` | counter | Operations given up at the retry horizon, labeled `kind`: `player_death`, `quest_kill_credit`, or `mob_corpse`. Every one also writes an error line, so any value above zero needs a look. |

### GameService

A GameService module declares every meter it creates in
`IGameServiceModule.MeterNames`, and Platform, which is no module, declares its
own in `PlatformMeters.Names`:

| Meter | What it measures |
| --- | --- |
| `Mmorpg.GameService.Accounts` | The password hashing lane: hashes in flight, the queue, how long a hash waited and took, and the requests it refused with `auth_busy`. |
| `Mmorpg.GameService.Placement` | Per Shard, as the public shard list reports it: online (1 or 0), active players, and connection capacity, labeled by `shard.id`. |
| `Mmorpg.GameService.Items` | Item transaction count and duration by operation kind and outcome, lock waits, conflicts by kind, death partitions, policy actions, Recovery expiry, open and closed corpses, and the Recovery backlog. |
| `Mmorpg.GameService.Retention` | The retention host of Platform, per retention task: the rows deleted, the time of each pass, the failed passes by reason, and the lag. |
| `Mmorpg.GameService.Audit` | The economy audit of Platform: when its last run ended, how long it took, its runs by outcome, and the violations that each check found. |
| `Mmorpg.GameService.Redis` | Session cache results, Redis failures, and distributed authentication rate-limit decisions. |

| Instrument | Type | Meaning |
| --- | --- | --- |
| `game_service.placement.shard.online`, `.shard.players`, `.shard.capacity` | gauge | Per Shard, labeled `shard.id`, refreshed every `Simulation:MetricsIntervalSeconds` from the same query as the public shard list. A failed refresh drops the values instead of keeping stale ones. Shard ids are labels because Shards are a small configured set. |
| `game_service.items.transactions` | counter | Item transactions, labeled `operation.kind` with the persisted operation kind from [Operation Kinds](GAME_SERVICE_MODULES.md#operation-kinds) and `outcome`: `committed`, `rejected`, `failed` for a transaction that threw, or `cancelled` when the caller cancelled it. |
| `game_service.items.transactions.duration` | histogram, ms | Transaction duration with the same labels. |
| `game_service.items.locks.wait.duration` | histogram, ms | Lock waits, labeled `scope`: `operation_claim`, `character_state`, `character_event`, `mutation_scope`, or `other`. |
| `game_service.items.conflicts` | counter | Rejections by `kind`: `operation` for a reused operation id, `stale_revision`, or `timeout`. |
| `game_service.items.death_partitions` | counter | Committed player death partitions. |
| `game_service.items.policy_actions` | counter | Policy, quest, and death operations, labeled `action` and `outcome`. |
| `game_service.items.cleanup` | counter | Unclaimed Recovery deliveries that the maintenance expired, labeled `target`: `recovery_delivery`. What retention trims counts in `game_service.retention.deleted`. |
| `game_service.items.corpses` | gauge | Durable corpses, labeled `state`: `open` or `closed`. |
| `game_service.items.recovery.backlog` | gauge | Unclaimed Recovery deliveries. |
| `game_service.accounts.password_hashing.in_flight` | gauge | Password hashes running on the lane's threads. |
| `game_service.accounts.password_hashing.queue_length` | gauge | Registrations and logins waiting in the lane's queue. |
| `game_service.accounts.password_hashing.wait.duration` | histogram, ms | How long a hash waited in the queue before a lane thread took it. |
| `game_service.accounts.password_hashing.duration` | histogram, ms | How long one hash or check took on its lane thread; the lane's queue limit follows it. |
| `game_service.accounts.password_hashing.refusals` | counter | Requests refused with `auth_busy` because they would have waited longer than `Auth:PasswordHashing:MaxWaitSeconds`, labeled `operation`: `hash` for a registration or `verify` for a login. |
| `game_service.redis.session_cache.operations` | counter | Session cache results, labeled `result`: `hit`, `miss`, `tombstone_bypass`, `database_fallback`, `store`, or `invalidation`. |
| `game_service.redis.failures` | counter | Failed Redis operations, labeled `operation`, such as `session_cache_revoke` for revocation tombstones that could not be stored and `session_cache_revoke_retry` for a first attempt that failed for a transient reason and was sent again. |
| `game_service.redis.authentication_rate_limit.operations` | counter | Distributed rate-limit decisions, labeled `result`: `allowed`, `rejected`, or `local_fallback`. |
| `game_service.retention.deleted` | counter | Rows that a retention task deleted, labeled `task` with the task's name from [Retention](GAME_SERVICE_MODULES.md#retention), such as `item_operations` or `account_sessions`. Rows that go along by cascade, such as an operation's journal rows, are not counted. |
| `game_service.retention.duration` | histogram, ms | The time one task spent in one pass, all its batches together, labeled `task`. |
| `game_service.retention.failures` | counter | Passes in which a task's batch failed, labeled `task` and `reason`: `lock_timeout` for a lock that other sessions held longer than the batch's one-second lock timeout, `statement_timeout` for a statement that outlasted the batch's statement timeout, or `error` for anything else, such as a lost connection. A task fails at most once per pass, and waits for the next one while the others go on. One failure now and then is routine; a task that keeps failing is the `RetentionTaskFailing` alert. |
| `game_service.audit.runs` | counter | Runs of the scheduled [economy audit](GAME_SERVICE_MODULES.md#economy-audit), labeled `outcome`: `clean`, `violations`, or `failed` when nothing was found but the run or a check could not run. |
| `game_service.audit.last_run_timestamp` | gauge, s | The Unix time at which the latest run ended; nothing before the first run. |
| `game_service.audit.last_run_duration` | gauge, s | How long the latest run took, all its checks in one snapshot. |
| `game_service.audit.violations` | gauge | The violations that each check found in its latest run that ran, labeled `check` with the check's id from [Audit Checks](GAME_SERVICE_MODULES.md#audit-checks). A check that could not run keeps its count from before. Any value above 0 is a broken economy. |
| `game_service.retention.lag` | gauge, s | How long the oldest due row of a task had been due when the task's latest pass began, labeled `task`; 0 when nothing was due. While retention keeps up it stays within about one `Retention:IntervalSeconds`; a lag that grows from pass to pass means a task falls behind its table. A task whose pass failed before it measured a lag has no series: the oldest due row's age is what the failed batch would have read, so it is unknown, and a kept value would show a stuck task as a healthy one. The series returns with the task's next pass that measures one, and `game_service.retention.failures` and `RetentionTaskFailing` cover the time in between. `item_operations` leaves out the operations of deaths, which become due only when `closed_corpses` takes their death event along, as [Retention](GAME_SERVICE_MODULES.md#retention) describes. |

The periodic item summary log counts only committed and rejected transactions.
Vendor trades and gathering yields show in the transaction instruments by their
operation kinds `vendor_purchase`, `vendor_sale`, and `gather_resource`, and
quest actions by `accept_quest`, `abandon_quest`, `regrant_quest_items`,
`talk_to_quest_npc`, `deliver_quest_items`, `turn_in_quest`, and
`credit_quest_kill`.
GameService has no instrument of single currency changes; the economy audit
judges the balances and the ledger as a whole.

### Built-in .NET Metrics

- `System.Runtime` in both processes: CPU time, working set, GC collections,
  pause time, heap sizes, allocation, thread pool, and lock contention.
- ASP.NET Core in GameService: `http.server.request.duration` by route, method,
  and status, active requests, Kestrel connections, routing, rate limiting,
  authentication, and authorization.
- `System.Net.Http` in both processes: `http.client.request.duration`, which in
  SimulationWorker is the latency of every call to GameService.
- `Npgsql` in GameService: `db.client.operation.duration` and the connection
  pool (`db.client.connection.count` by state and `db.client.connection.max`),
  with the pool named `postgres`.

### Rules For New Metrics

- Labels stay low-cardinality. Account, character, session, entity, peer,
  operation, item, corpse, and actor ids are never labels. Shard ids are,
  because Shards are a small configured set.
- A GameService module declares every meter it creates in
  `IGameServiceModule.MeterNames`, Platform in `PlatformMeters.Names`; the
  worker lists its meters in
  `SimulationWorkerTelemetry.MeterNames`. A guard test in each process fails
  when a class owns a `Meter` that is not exported, and every metric class
  publishes its meter name as `public const string MeterName`.
- Millisecond histograms of the project's meters use the bucket bounds in
  `ServerTelemetry.MillisecondHistogramBoundaries`, which resolve the 33.3 ms
  fixed-tick budget below one millisecond and reach item transactions' 10
  second timeout.
- Hot-path recording must not allocate. Record through counters, histograms
  without tags, or `Interlocked` fields read by observable instruments.
  `RealtimeMetricsAllocationTests` measures the worker's hot-path methods with the
  production views and an exporter attached and requires zero allocated bytes.
- The dashboards and alert rules may only use instruments that exist: the tests
  `DashboardsAndAlertsReferenceOnlyPublished*Metrics` read every
  `simulation_worker_*` and `game_service_*` name in `observability/` and fail on
  an unknown one, and `MeterInstrumentNamesTests` pins every `game_service.*`
  instrument that they read.

## Logs

- With export enabled, every log entry goes to Loki with its structured template
  values, the formatted message, its logger category (`scope_name`), severity,
  and the trace and span id of the current operation. Console logging is
  unchanged.
- Loki stream labels are `service_name`, `service_namespace`,
  `service_instance_id`, and `deployment_environment_name`. Everything else,
  including `CorrelationId`, `trace_id`, `severity_text`, `severity_number`, and
  `scope_name`, is structured metadata that LogQL filters with `| key="value"`.

Useful queries in Grafana Explore with the Loki data source:

```logql
{service_namespace="mmorpg"} | severity_number >= 13
{service_name="game-service"} | CorrelationId="<id from a response header>"
{service_name="simulation-worker"} |= "Worker status"
```

### Correlation Ids

Every GameService request has one correlation id: the caller's valid
`X-Correlation-ID`, otherwise the W3C trace id of the request. The response
header carries it, also on error responses, every log entry written during the
request carries it as the `CorrelationId` scope value, whichever module writes
it, together with the `ClientAddress` scope value, the address that
[The Client's Address](SERVICE_FEATURES.md#the-clients-address) decides, and
the request span carries it as `mmorpg.correlation_id`. SimulationWorker
sends the trace id of its operation as `X-Correlation-ID` together with the W3C
`traceparent` header, so one worker operation, its GameService request, and that
request's PostgreSQL commands form one trace, and the response header, the log
entries of both processes, and the trace share one id.

### Log Categories

- GameService components log under their namespaces, such as
  `Mmorpg.GameService.Accounts`, `Mmorpg.GameService.Accounts.Redis`,
  `Mmorpg.GameService.Placement`, `Mmorpg.GameService.Items`, and
  `Mmorpg.GameService.Platform.Database`. The logout entry uses the explicit
  category `Mmorpg.GameService.Auth`. No configuration filters on these
  categories.
- The realtime components of SimulationWorker log through
  `Mmorpg.SimulationWorker.Realtime.RealtimeSimulationService`, so their
  category and templates stay stable; other worker services log under their own
  types.
- Some server messages start with a prefix that names their area, `[AUTH]` in
  GameService and `[SIMULATION]` in SimulationWorker. The Unity client's console
  categories are described in
  [Client Diagnostics](UNITY_CLIENT_ARCHITECTURE.md#client-diagnostics).

### Periodic And Routine Logs

- SimulationWorker writes three lines every `NetworkMetricsLogSeconds`. The
  network line reports the realtime meter's totals. The status line reports real
  players, synthetic bots, unauthenticated peers, connected peers, entities,
  process CPU normalized to total logical-core capacity, single-core-equivalent
  CPU, working set, packet and payload rates including the snapshot payload
  rate, the received and sent datagrams per second with the sealing overhead in
  KiB per second and the dropped datagrams per second, snapshot drops, quota
  rejections, and the sample window. The
  performance line reports, for the interval, averages, approximate p95 and p99
  upper bounds, and maximum durations of every performance histogram, the
  current and interval-maximum completed-operation and corpse-mutation backlogs,
  so admission congestion is distinguishable from UDP loss, process allocation,
  GC collection counts, managed heap size and fragmentation, live managed
  memory, the recipients of remote snapshots, encoded and sent snapshot packets, tick
  overruns, the loop failures of whole steps and of single players or
  peers, the share of the interval that the loop thread was busy, and the
  garbage collection pauses of the interval: their time, their number by the
  generation of the collection that paused the process, the longest one, the
  pause time that fell inside ticks and the ticks it fell into, and the
  overruns that stay over the tick interval without their pauses. The worker
  counts the pauses one by one from the runtime's own GC events, from the
  suspension of the threads to their restart; a pause without a collection
  start of its own, such as the last one of a background collection, counts as
  generation 2. At its start the worker logs how its garbage collector runs,
  such as `workstation, concurrent, latency mode Interactive`, the default of
  the worker, which sets nothing about it, and the priority of the realtime
  loop thread, `SimulationWorker:Realtime:LoopThreadPriority`, with the clock
  that measures the thread's CPU time per tick: on Windows the thread's cycle
  count from `QueryThreadCycleTime`, turned into time at the rate of the
  processor's time stamp counter, which the worker measures with short spins
  at its start and raises whenever a tick of at least 2 ms ran faster; on
  64-bit Linux `CLOCK_THREAD_CPUTIME_ID`; elsewhere none, and the CPU fields
  below stay empty. The two reads around a tick cost 0.7 to 1 microsecond on
  Windows.
- With `SimulationWorker:PerformanceReportPath` set, environment form
  `SimulationWorker__PerformanceReportPath`, the worker also appends each
  interval of these three lines to that file as one JSON object per line,
  written from the same captured values: the interval's start, end, and
  length; players, bots, peers, and entities; CPU and working set; the tick
  and every timed phase with samples, average, p95 and p99 bucket bounds, and
  maximum, among them `phases`, every tick phase that did its work in the
  interval by its name in tick order, `phaseParts`, the parts of the phases
  that time themselves by `<phase>: <part>`, as
  `simulation_worker.simulation.tick_phase_part.duration` lists them, and
  `loopSteps`, every step of a loop iteration outside the tick by its name;
  for the tick and every tick phase, `gcPausedSamples`, the samples that a
  garbage collection pause fell into, `gcPauseMs`, the pause time inside
  them, and `p99WithoutGcPausesMs` and `maximumWithoutGcPausesMs`, the p99
  bucket and the longest sample once each sample's own pauses are taken out
  of it, so a sample that is long only by a pause shows as such;
  `slowestTick`, the interval's slowest tick with `ms`, `cpuMs`, the loop
  thread's CPU time inside it, its `gcPauseMs`, the `spawnPackets` and
  `despawnPackets` it sent, and the milliseconds of every phase and phase part
  that ran in it, in `phases` and `phaseParts`; `loopBusyPercent`, the share of
  the interval that the loop thread spent in its ticks and loop steps, beside
  the process CPU, which also holds the transport's threads; `tickCpuMs`, the
  loop thread's CPU time summed over the interval's ticks, and
  `tickNotRunning`, per tick the time the loop thread did not run, the tick's
  wall time minus its CPU time, such as when other work on the machine
  preempted it, with samples, average, bucket bounds, and maximum, so the
  interval's lost time is its samples times its average; `gcPauseMs`, the
  garbage collection pauses of the interval, and `gcPauses`,
  `gcGeneration0Pauses`, `gcGeneration1Pauses`, `gcGeneration2Pauses`, and
  `gcLongestPauseMs`, the pauses one by one; `collisionPendingSteps`, the
  steps that movers skipped because their collision chunks still loaded;
  tick overruns, `tickOverrunsWithoutGcPauses`, the overruns that stay over
  the tick interval without their pauses, and
  resynchronizations; the loop failures,
  `loopStepFailures` and `loopIsolatedFailures`; the backlogs; the interval's
  snapshot drops, deferrals, quota rejections, packets and bytes in and out,
  snapshot bytes, joins, spawns, and despawns; `datagramsIn`,
  `datagramBytesIn`, `datagramsOut`, and `datagramBytesOut`, the UDP datagrams
  of the interval and every byte of them, `sealingOverheadBytes`, the bytes that
  the seal added in both directions, and `droppedDatagrams`; `sentByMessage` and
  `receivedByMessage`, the messages and bytes of every message kind with a
  message in the interval, keyed by the `RealtimeMessageType` name in type
  order, such as
  `"sentByMessage":{"EntitySpawn":{"messages":310,"bytes":21390}}`;
  allocation, GC collections per generation, and the managed heap; and the
  Mob numbers of the combat and world actor meters, the living Mobs,
  `engagedMobs`, `movingMobs`, and
  `mobPathQueue` as their current gauges and `mobPathPlans`, the Mob routes
  planned in the interval; and `durableCorpses` and `liveCorpses`, the active
  corpses at the end of the interval. A failed write is logged as a warning
  and never stops the worker; without the setting nothing is written. The file
  is diagnostics for the load tooling, as
  [Stack Stress Testing](STACK_STRESS_TESTING.md#measurements) describes, not a
  contract for anything else.
- GameService logs a cumulative item summary of transaction, contention,
  lifecycle, corpse, Recovery, and Recovery expiry counts every
  `Items:Operations:MetricsIntervalSeconds`, and the Redis counters every
  `RedisAcceleration:MetricsIntervalSeconds`. Item transaction logs include the
  correlation and operation ids, classify expected contention below warning,
  promote significant lifecycle commits, and exclude credentials, tokens, and
  command bodies.
- The scheduled economy audit logs one line per run at `Information` with the
  checks that ran, its time, its violations, and the checks that could not run;
  an `Error` line for every check that found a violation, with its count, what
  it examined, its description, and its examples by id; a `Warning` line for
  every check that could not run and for a run that stopped early; and, while
  `Audit:Enabled` is off, one `Information` line at start that says so.
- The retention host logs a pass that deleted rows at `Information`, with its
  rows and time and the rows of every task, a pass that used its budget with
  rows still due at `Warning`, naming the tasks, a batch that gave up on a lock
  or statement timeout at `Warning`, and a task that failed at `Error`; every
  failed batch is also counted in `game_service.retention.failures`.
- A start against a database that a newer release migrated, which the build
  runs on while nothing else is pending, logs one `Warning` that names the
  migrations that the build does not know, as
  [Rolling Back A Release](GAME_SERVICE_MODULES.md#rolling-back-a-release)
  describes. The scheduled economy audit then logs a warning at each interval
  instead of running, and records no audit run.
- SimulationWorker filters routine successful `GameServiceClient` HTTP pipeline
  messages below `Warning`. Domain failures and HTTP warnings remain visible,
  but high-frequency bot join and release requests do not drown out worker
  status, performance, lifecycle, or error logs.
- SimulationWorker writes one warning when GameService stops answering its
  registry heartbeat and one information line with the length of the outage
  when it answers again; each failed registry heartbeat attempt, every two
  seconds during an outage, also writes a warning, and the stop after the
  outage tolerance writes one critical line.
- Routine UDP connect and disconnect events and successful synthetic bot joins
  and leaves are logged at `Debug`. Successful real-player lifecycle events stay
  at `Information`, and every admission failure stays visible. The synthetic bot
  population appears in the status line.
- GameService throttles repeated Redis warning logs per operation, and the
  password hashing lane logs a refusal with `auth_busy` as a warning at most
  every 30 seconds, with the queue, the hashes in flight, and the measured time
  of a hash.

## Traces

Spans exist where a request crosses a process or reaches the database:

- GameService: one server span per HTTP request except `/health/*`, with the
  Npgsql command spans of that request below it. Npgsql only creates spans
  inside a traced request or operation, so background polling and maintenance
  do not create root traces.
- SimulationWorker: the activity source `Mmorpg.SimulationWorker` starts one
  span per GameService call, named after the operation, such as
  `GameService.ConsumeJoinTicket` or `GameService.MutateSimulationItems`, with
  the HTTP client span and the GameService request below it. A failed call
  carries `mmorpg.error.code` and an error status. `simulation.join` groups the
  ticket consumption and any release of a rejected join, and
  `simulation.session.heartbeat_cycle` groups one heartbeat round of all active
  sessions: its `GameService.HeartbeatSimulationSessions` calls, one per batch
  of sessions, and their retries.
- The fixed tick never starts a span.

In Grafana, a log entry with a trace id links to the trace in Tempo, and a trace
links back to the log entries with the same trace id.

## Dashboards

Both dashboards are provisioned from the repository and refresh every 10
seconds. Changes made in the Grafana UI cannot be saved; edit the JSON in
`observability/grafana/dashboards` instead, then restart Grafana or wait for it
to reload the files.

- **MMORPG Playtest** (`/d/mmorpg-playtest`, the Grafana home page): players,
  bots, Shards online, tick p99 against the budget, overruns, GameService 5xx
  share, and the firing alerts at the top. Below them: tick duration
  percentiles with the 33.3 ms budget line, tick phases, tick lag, overruns and
  resynchronizations, backlogs, population, bandwidth including snapshot bytes,
  packets, drops and rejections, joins, worker calls to GameService, world
  actors, interactions, corpses, gathering (active gatherers, depleted nodes,
  and gathering cycles per minute by outcome), combat (combatants, engaged and
  moving Mobs, attacks, hits, and deaths per minute by kind, and Mob routes per
  minute by outcome), arrows and deaths (pending shots, arrow batches and player
  death partitions per minute by outcome, and dropped shots), worker CPU,
  memory, allocation rate, GC, and recent warnings and errors of both services.
- **MMORPG GameService** (`/d/mmorpg-game-service`): Shards online, players,
  request rate, 5xx share, request and database p95 at the top. Below them:
  players and capacity per Shard, requests, p95 latency and errors by route,
  rate limits, session cache and Redis, PostgreSQL latency and connection pool,
  item transactions by outcome and kind, contention, corpses and Recovery,
  lifecycle operations, the rows that retention deleted and its lag by task,
  process CPU, memory, thread pool, GC, and GameService warnings and errors.

## Alerts

The rules in `observability/prometheus/rules/playtest.rules.yml` cover what
affects a playtest. `critical` means players are affected now; `warning` means
look before it becomes critical.

| Alert | Severity | Fires when |
| --- | --- | --- |
| `GameServiceDown` | critical | GameService sent no telemetry for two minutes. |
| `SimulationWorkerDown` | critical | No SimulationWorker sent telemetry for two minutes. |
| `ShardOffline` | critical | A Shard is offline in the shard list for one minute. |
| `ShardNearCapacity` | warning | A Shard is above 90 percent of its player capacity for five minutes. |
| `SimulationTickOverruns` | warning | More than 1 percent of a worker's ticks exceed the tick interval for two minutes. |
| `SimulationTickResynchronized` | warning | A worker fell more than five ticks behind and skipped time. |
| `SnapshotDrops` | warning | More than 5 percent of a worker's snapshot packets are dropped for five minutes. |
| `CompletedOperationBacklog` | warning | More than 50 completed authority operations wait for the loop for one minute. |
| `JoinRejections` | warning | More than 20 percent of a worker's joins are rejected for five minutes. |
| `SimulationEntityIdsHalfUsed` | warning | A worker gave out more than half of its entity ids; [Entity Ids](#entity-ids) says what to do. |
| `GameServiceErrorRate` | critical | More than 1 percent of GameService requests return 5xx for five minutes. |
| `GameServiceSlowRoute` | warning | A route's p95 latency is above 500 ms for five minutes. |
| `DatabasePoolSaturated` | warning | GameService uses more than 90 percent of its PostgreSQL pool for two minutes. |
| `ItemTransactionFailures` | warning | An item transaction failed with an unexpected error in the last five minutes. |
| `ItemTransactionTimeouts` | warning | An item transaction hit its lock or transaction timeout in the last five minutes. |
| `RedisFailures` | warning | GameService could not reach Redis in the last five minutes. |
| `EconomyAuditViolations` | critical | A check of the economy audit found a violation in its latest run: an item lost or in two places, an invalid stack or slot, or gold that does not match its ledger. The GameService log names the examples by id. |
| `EconomyAuditFailed` | warning | A run of the economy audit could not run every check in the last two hours, such as one that hit its statement timeout. |
| `RetentionTaskFailing` | warning | A retention task failed at least five times in the last ten minutes and has kept doing so for five minutes. A pass runs every minute by default, so a task that fails in each pass shows here while a lone lock timeout does not. The GameService log names the task and why. |
| `RetentionLagHigh` | warning | The oldest due row of a retention task had waited more than 15 minutes when its latest pass began, and this has held for ten minutes, so the task falls behind its table. The lag stays within about one `Retention:IntervalSeconds` while a task keeps up, 60 seconds by default, so raise the threshold in the rule with the interval. A task whose last pass failed has no lag series, which `RetentionTaskFailing` covers. |

`observability/prometheus/tests/playtest.rules.test.yml` holds promtool unit
tests for the availability, tick, error-rate, entity id, pool, economy audit, and
retention rules.
`tools/Verify-Observability.ps1` validates the collector and Prometheus
configuration, runs those tests, and checks the dashboard JSON with the pinned
images. CI runs it in the `Observability configuration` job on every pull
request and on every push to `development` and `main`.

### Entity Ids

A worker gives every player and every world actor a network entity id from a
counter that starts at 1 when the worker starts. An id is never given out
twice, not even after its entity left, so a client never mistakes a new entity
for an old one: a joining player, a reconnect that replaces an entity, and a
respawning Mob each take the next one. A remote snapshot record carries the id
in 32 bits, so the counter stops at 4,294,967,295, and
`simulation_worker.realtime.entity_ids.used` shows how far it is. Even 100 new
ids a second, a rate of joins and respawns far above any playtest, use all of
them only after 497 days. `SimulationEntityIdsHalfUsed` fires at half of them,
which at that rate is after 248 days, or sooner when a join, respawn, or
reconnect loop burns ids; the join and spawn counters show which.

When every id is used, no player can join and no Mob can respawn: a join fails
with `internal_error`, and the worker writes this critical line whenever an id
was asked for, at most once a minute:

```text
[SIMULATION] All 4294967295 simulation entity ids are used, so no player can join and no Mob can respawn on this worker.
```

Restart the worker, which starts the counter at 1 again. A stop saves the
remaining players in one last checkpoint and ends their sessions, as
[Realtime UDP Transport](SERVICE_FEATURES.md#realtime-udp-transport) describes,
and they join again. Restart it at a quiet hour once the alert fires, so the
line never appears.

## During A Playtest

1. Start the stack and both services with export enabled, as in
   [Run The Monitoring Stack](#run-the-monitoring-stack).
2. Keep **MMORPG Playtest** open. The top row answers whether players can play:
   Shards online must be at least 1, tick p99 well below 33.3 ms, overruns 0,
   GameService 5xx at 0 percent, and no firing alert.
3. When players report a hitch, check tick duration, tick phases, and GC for the
   same minute. A phase that grows with the player count points at the system to
   profile; a GC spike points at allocations.
4. When players report failed actions, open **MMORPG GameService**, find the
   route with errors or latency, and open its warnings in the log panel. Follow
   the trace id from a log entry to the trace to see the worker call, the
   GameService request, and the database commands with their durations.
5. When a single player reports a problem with a correlation id or the time it
   happened, search the logs for that id or time window in Explore.

Expected result: the dashboards, logs, and traces lead from a reported symptom
to the system, request, or database command that caused it.

## Cost On The Realtime Loop

- Counters on the hot path are `Interlocked` fields read by observable
  instruments, so the export thread reads them every export interval and the
  loop pays nothing extra.
- Histograms record through `System.Diagnostics.Metrics` without tags. With the
  OpenTelemetry SDK attached that is a lock-free bucket update without
  allocation, which `RealtimeMetricsAllocationTests` enforces. The interval
  percentile accumulator of the periodic performance log does not allocate per
  sample either, and it counts with `Interlocked` operations, so the loop never
  waits for the reporter that takes an interval.
- The garbage collection pause inside the tick and each phase comes from the
  process's total pause time, read before and after them. The slowest tick's
  breakdown is a fixed array per interval that the loop fills and the reporter
  copies without a lock, trying again when the loop wrote meanwhile. The
  single pauses come from the runtime's GC events on the runtime's own event
  thread, which the loop never touches. The snapshot phase reads the clock at
  the boundaries of its parts, about six times per recipient.
- The SDK collects, batches, and exports on its own threads. Spans and log
  export never run inside the fixed tick.

## Security

- The local stack binds every published port to `127.0.0.1`. Grafana runs with
  anonymous admin access and no login form, which is only acceptable on a
  developer machine. A shared or staging deployment needs authentication, TLS,
  and network restrictions first.
- Traces and logs never contain tokens, passwords, request bodies, or the
  transport keys, join proofs, and nonces of the realtime transport. Npgsql
  spans contain SQL text with parameter placeholders, not parameter values. The
  connection pool is labeled `postgres` instead of the connection string.

## Not Yet Implemented

- Alert notifications to a chat or paging service.
- A staging or production deployment of the stack, with authentication,
  retention, and backups.
- Unity client telemetry; client diagnostics stay in the Debug menu (F2).
- (planned) GameService economy instruments: currency created and destroyed by
  source kind and direction, to watch inflation, vendor transactions by
  outcome, and gathered quantities by resource profile.
