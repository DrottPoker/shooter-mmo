# Monitoring

Last updated: 2026-10-16

This document describes how GameService and SimulationWorker report what they do,
the local monitoring stack that collects it, the starting dashboards and alerts,
and what to watch during a playtest. The commands to start the stack are also
listed in [Local Development](LOCAL_DEVELOPMENT.md#monitoring-stack).

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
- The collector is the only receiver. It forwards metrics to Prometheus through
  Prometheus' native OTLP endpoint, traces to Tempo, and logs to Loki. A staging
  or production environment can replace any backend by changing only
  `observability/otel-collector/config.yaml`.
- Grafana reads all three stores and loads the dashboards in
  `observability/grafana/dashboards` from the repository.
- Pushed metrics carry no staleness markers, so Prometheus runs with a 30
  second lookback: the values of a stopped process disappear from instant
  queries and panels 30 seconds after its last export instead of five minutes.
- Prometheus evaluates the alert rules in `observability/prometheus/rules`.
  Firing alerts show in Grafana under Alerting and on both dashboards. They are
  not routed to a notifier yet; routing comes with the staging environment.

Export is off until `Telemetry:OtlpEndpoint` is set. Without it nothing leaves
the processes: the meters still feed the periodic status logs on the console,
and tests and ordinary runs are unaffected.

## Configuration

Both processes read the same `Telemetry` section. Defaults live in each
service's `Config/appsettings.json`; `.env.example` sets the endpoint for local
development.

| Setting | Default | Behavior |
| --- | --- | --- |
| `Telemetry:OtlpEndpoint` | empty | OTLP gRPC endpoint of the collector, such as `http://127.0.0.1:4317`. Empty disables export. Only scheme, host, and port are accepted. |
| `Telemetry:MetricExportIntervalSeconds` | `10` | Metric export interval, 1 to 300 seconds. The local Prometheus forgets a series 30 seconds after its last sample, so keep this at 15 seconds or less with the local stack. |
| `Telemetry:TraceSampleRatio` | `1.0` | Share of new traces that are kept, 0 to 1. A trace started by a sampled caller is always kept, so a worker operation and the GameService request and database commands it causes stay together. |
| `Telemetry:ServiceInstanceId` | host name or worker id | Overrides the Prometheus `instance` label. GameService defaults to the lowercase host name and SimulationWorker to its `SimulationWorkerId`, so a restarted process continues its series. Two GameService replicas on one host must set different values. |
| `Simulation:MetricsIntervalSeconds` | `15` | GameService only: how often the shard gauges re-read the shard list, 1 to 3600 seconds. |

The OpenTelemetry resource of each process carries `service.namespace=mmorpg`,
`service.name` (`game-service` or `simulation-worker`), `service.version`,
`service.instance.id`, and `deployment.environment.name` (the ASP.NET Core or
.NET host environment). SimulationWorker adds `mmorpg.fleet.id`,
`mmorpg.node.id`, `mmorpg.shard.id`, `mmorpg.world.id`, and
`mmorpg.worker.runtime.id`. Prometheus turns `service.namespace/service.name`
into the `job` label (`mmorpg/game-service`, `mmorpg/simulation-worker`),
`service.instance.id` into `instance`, and promotes the environment, fleet,
node, Shard, and World attributes to labels.

## Metrics

Metrics names below are the instrument names. Prometheus replaces dots with
underscores and adds `_milliseconds` for millisecond units, `_total` for
counters, and `_bucket`, `_sum`, and `_count` for histograms, so
`simulation_worker.simulation.tick.duration` is queried as
`simulation_worker_simulation_tick_duration_milliseconds_bucket`.

### SimulationWorker

| Meter | What it measures |
| --- | --- |
| `Mmorpg.SimulationWorker.Performance` | Fixed-tick duration, tick lag, overruns (ticks longer than the tick interval), resynchronizations, the duration of each tick phase and of network polling and completion processing, join queue delay and finalization, the completed-operation and corpse-mutation backlogs, and snapshot visibility groups, encoded and sent packets, and deferred entity updates. |
| `Mmorpg.SimulationWorker.Realtime` | Joined real players, synthetic bots, unauthenticated peers, peers, and entities; sent and received packets and bytes; snapshot payload bytes; quota rejections; snapshot drops and the backpressure share; accepted and rejected joins; rejected connections; spawn, despawn, and snapshot entity records. |
| `Mmorpg.SimulationWorker.WorldActors` | NPC and Mob population by activity tier, open interaction leases, accepted and rejected interactions, and interaction latency. |
| `Mmorpg.SimulationWorker.Corpses` | Active live and durable corpses held by the worker. |

### GameService

| Meter | What it measures |
| --- | --- |
| `Mmorpg.GameService.Placement` | Per Shard, as the public shard list reports it: online (1 or 0), active players, and connection capacity, labeled by `shard.id`. |
| `Mmorpg.GameService.Items` | Item transaction count and duration by operation kind and outcome, lock waits, conflicts by kind, death partitions, policy actions, retention cleanup, open and closed corpses, and the Recovery backlog. |
| `Mmorpg.GameService.Redis` | Session cache results, Redis failures, and distributed authentication rate-limit decisions. |

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
  operation, item, corpse, and actor ids are never labels. Shard ids are, because
  Shards are a small configured set.
- A GameService module declares every meter it creates in
  `IGameServiceModule.MeterNames`; the worker lists its meters in
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
  an unknown one.

## Logs

- With export enabled, every log entry goes to Loki with its structured template
  values, the formatted message, its logger category (`scope_name`), severity,
  and the trace and span id of the current operation. Console logging is
  unchanged.
- Every GameService request runs inside a `CorrelationId` log scope, so every
  entry written during the request carries it, whichever module writes it.
- The correlation id is the caller's valid `X-Correlation-ID` header, otherwise the
  W3C trace id of the request. SimulationWorker sends its operation's trace id
  as `X-Correlation-ID`. In practice the response header, the log entries of both
  processes, and the trace share one id.
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

## Traces

Spans exist where a request crosses a process or reaches the database:

- GameService: one server span per HTTP request except `/health/*`, with the
  Npgsql command spans of that request below it. Npgsql only creates spans
  inside a traced request or operation, so background polling and maintenance
  do not create root traces.
- SimulationWorker: one span per GameService call, named after the operation,
  such as `GameService.ConsumeJoinTicket` or `GameService.MutateSimulationItems`,
  with the HTTP client span and the GameService request below it. A failed call
  carries `mmorpg.error.code` and an error status. `simulation.join` groups the
  ticket consumption and any release of a rejected join, and
  `simulation.session.heartbeat_cycle` groups one heartbeat round of all active
  sessions.
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
  actors, interactions, corpses, worker CPU, memory, allocation rate, GC, and
  recent warnings and errors of both services.
- **MMORPG GameService** (`/d/mmorpg-game-service`): Shards online, players,
  request rate, 5xx share, request and database p95 at the top. Below them:
  players and capacity per Shard, requests, p95 latency and errors by route,
  rate limits, session cache and Redis, PostgreSQL latency and connection pool,
  item transactions by outcome and kind, contention, corpses and Recovery,
  lifecycle operations, process CPU, memory, thread pool, GC, and GameService
  warnings and errors.

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
| `GameServiceErrorRate` | critical | More than 1 percent of GameService requests return 5xx for five minutes. |
| `GameServiceSlowRoute` | warning | A route's p95 latency is above 500 ms for five minutes. |
| `DatabasePoolSaturated` | warning | GameService uses more than 90 percent of its PostgreSQL pool for two minutes. |
| `ItemTransactionFailures` | warning | An item transaction failed with an unexpected error in the last five minutes. |
| `ItemTransactionTimeouts` | warning | An item transaction hit its lock or transaction timeout in the last five minutes. |
| `RedisFailures` | warning | GameService could not reach Redis in the last five minutes. |

`observability/prometheus/tests/playtest.rules.test.yml` holds promtool unit
tests for the availability, tick, error-rate, and pool rules.
`tools/Verify-Observability.ps1` validates the collector and Prometheus
configuration, runs those tests, and checks the dashboard JSON with the pinned
images. CI runs it on every push and pull request in the `Observability
configuration` job.

## During A Playtest

1. Start the stack and both services with export enabled, as in
   [Local Development](LOCAL_DEVELOPMENT.md#monitoring-stack).
2. Keep **MMORPG Playtest** open. The top row answers whether players can play:
   Shards online must be at least 1, tick p99 well below 33.3 ms, overruns 0,
   GameService 5xx at 0 percent, and no firing alert.
3. When players report a hitch, check tick duration, tick phases, and GC for the
   same minute. A phase that grows with the player count points at the system to
   profile; a GC spike points at allocations.
4. When players report failed actions, open **MMORPG GameService**, find the route
   with errors or latency, and open its warnings in the log panel. Follow the
   trace id from a log entry to the trace to see the worker call, the GameService
   request, and the database commands with their durations.
5. When a single player reports a problem with a correlation id or the time it
   happened, search the logs for that id or time window in Explore.

## Cost On The Realtime Loop

- Counters on the hot path are `Interlocked` fields read by observable
  instruments, so the export thread reads them every 10 seconds and the loop
  pays nothing extra.
- Histograms record through `System.Diagnostics.Metrics` without tags. With the
  OpenTelemetry SDK attached that is a lock-free bucket update without
  allocation, which `RealtimeMetricsAllocationTests` enforces. Adding the export
  also removed an existing per-sample allocation in the interval percentile
  accumulator.
- The SDK collects, batches, and exports on its own threads. Spans and log
  export never run inside the fixed tick.

## Security

- The local stack binds every published port to `127.0.0.1`. Grafana runs with
  anonymous admin access and no login form, which is only acceptable on a
  developer machine. A shared or staging deployment needs authentication, TLS,
  and network restrictions first.
- Traces and logs never contain tokens, passwords, or request bodies. Npgsql spans
  contain SQL text with parameter placeholders, not parameter values. The
  connection pool is labeled `postgres` instead of the connection string.

## Not Yet Implemented

- Alert notifications to a chat or paging service.
- A staging or production deployment of the stack, with authentication,
  retention, and backups.
- Unity client telemetry; client diagnostics stay in the F2 panel.
