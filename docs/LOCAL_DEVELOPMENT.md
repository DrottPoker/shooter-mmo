# Local Development

Last updated: 2026-10-19

## Requirements

- .NET SDK 10.0.301 or a newer 10.0.3xx feature band. `global.json` rolls
  forward only within .NET 10, so .NET 10.0.1xx or 10.0.2xx and .NET 11 SDKs
  are not selected.
- Docker Desktop.
- Unity Editor for the client project.
- Optional: Unity CLI for Editor automation and coding-agent access, described
  in [Unity Editor Automation With Unity CLI](#unity-editor-automation-with-unity-cli).

## Local Environment File

Create the ignored local environment file before running Compose or either
backend service:

```powershell
Copy-Item .env.example .env
```

Replace every `replace-with-...` placeholder in `.env`. The file contains local
credentials and connection strings, including the shared
`SIMULATION_WORKER_SERVICE_SECRET`, but does not select worker, fleet, node,
shard, or World identities. Those identities live together in
`src/Server/SimulationWorker/Config/appsettings.json`. GameService owns only the available
fleet, node, and shard topology. Both services search the working directory and
their output directory, and the parents of each up to the repository root, for
`.env`, so starting a service from its `bin` folder also finds the root file.
Lines may start with `export`, unquoted values may end with a ` #` comment, and
a malformed line is reported by line number without echoing its content.
Explicit process environment variables and command-line values can still
override service configuration for deployments. Configuration files are read
once at startup; restart a service after changing them.

SimulationWorker loads `WorldData/Worlds/<WorldId>/world.json` from its output folder and
derives `world-actors.json` and the `Collision` chunk folder below the same World
directory. The build copies `content/worlds/<WorldId>/world.json` and the
generated actor and collision data from `src/Shared/WorldData/Generated` into
that layout for both development Worlds, in build and publish output. Startup
validates identity, bounds, spawn, actor revisions, references,
ordering, collision checksums, and content semantics before UDP admission
begins. `SimulationWorker__WorldDataPath` can point a deployment at a different
canonical Worlds root.

SimulationWorker also loads the compiled item catalog from
`WorldData/Items/core.item-catalog.json` in its output folder, which the build
copies from `src/Shared/WorldData/Generated/Items`. It reads combat data and Mob
loot from that catalog and reports its revision in every heartbeat. GameService
places characters only on a worker whose revision equals the catalog it
mirrors, otherwise a join returns `simulation_worker_item_catalog_mismatch`.
After a catalog change, rebuild and restart both services.
`SimulationWorker__ItemCatalogPath` can point a deployment at another copy of
the same catalog.

SimulationWorker loads its combat rules from `WorldData/Combat/combat-rules.json`
in its output folder, which the build copies from
`content/combat/combat-rules.json`. After a change to that file, rebuild and
restart SimulationWorker. The worker stops with `shard_rule_set_unknown` when
the rule set that GameService names for its Shard is not in the file.
`SimulationWorker__CombatRulesPath` can point a deployment at another copy.

GameService reads `Items:CatalogPath` from its configuration. The checked-in
default points to `src/Shared/WorldData/Items/core.item-catalog.json`, which the GameService
project copies from the deterministic WorldData runtime catalog into build and
publish output. Use `Items__CatalogPath` only when a deployment places that
checked-in catalog at another path.

If the PostgreSQL Docker volume already exists, changing `POSTGRES_PASSWORD` does
not change the password stored inside PostgreSQL. Either keep the current local
password in both `.env` entries or update the database role interactively:

```powershell
docker exec -it shooter_mmo_postgres psql -U shooter_mmo -d shooter_mmo
```

Then run `\password shooter_mmo` inside `psql`. Deleting the Compose volume also
recreates the credentials, but permanently removes local database data.

## Repository Quality Checks

Restore locked dependencies and run the standard backend quality gate:

```powershell
dotnet restore Mmorpg.slnx --locked-mode
& .\tools\Verify-DependencyPolicy.ps1
dotnet format Mmorpg.slnx --verify-no-changes --no-restore
dotnet build Mmorpg.slnx --configuration Release --no-restore
dotnet run --project tools/ItemCatalogCompiler --configuration Release --no-build -- --verify
dotnet run --project tools/WorldCollisionCompiler --configuration Release --no-build -- --verify
dotnet run --project tools/WorldActorCompiler --configuration Release --no-build -- --verify
dotnet run --project tools/ProtocolGenerator --configuration Release --no-build -- --verify
& .\tools\Verify-Observability.ps1
dotnet test Mmorpg.slnx --configuration Release --no-build
```

Expected result:

- Restore accepts every committed `packages.lock.json` file.
- Dependency policy verification confirms exact Unity and NuGet versions in
  project, props, and targets files, one `xunit.v3` version across the test
  projects, local package paths, Unity lock coverage, and the absence of tracked
  client builds.
- Format reports no files that need changes.
- Build completes with zero warnings and zero errors.
- Item catalog verification reports eleven definitions, one base Secure
  Container tier, and the deterministic checked-in revision.
- Protocol verification reports realtime protocol version 17 with 28 messages
  and finds every generated file current.
- Observability verification validates the collector and Prometheus
  configuration, passes the alert rule tests, and parses both dashboards. It
  needs Docker and starts nothing that keeps running. CI runs the same script
  in its `Observability configuration` job.
- Unit tests pass.
- PostgreSQL integration tests are skipped unless their dedicated connection is
  configured. The Redis integration test is skipped unless
  `MMORPG_TEST_REDIS` points to a dedicated Redis test instance. See
  [Standard Quality Checks](../README.md#standard-quality-checks) for the full
  integration run with both test containers.

Tests read configuration and content from repository paths, never from their
output folder. GameService, SimulationWorker, and ActiveSimulationBots all ship
appsettings files at the same output paths, so `tests/Directory.Build.props`
sets `CopyConflictingTransitiveContent` to `false` for every test project and
links the empty `{}` placeholders in `tests/TestOutput` to those paths.
`TestBuildOutputTests` fails if a service copy ever replaces a placeholder. The
test projects and their rules are described in [Backend Tests](../tests/README.md).

NuGet and GitHub Actions updates are proposed by Dependabot against
`development` on the first day of each month. Unity Package Manager updates
remain explicit because Dependabot does not own `Packages/manifest.json`. Update
Unity packages in a focused branch, let Unity rewrite `packages-lock.json`, run
both Unity suites, and run the backend quality gate before merging. `LiteNetLib`
must stay aligned between
`src/Server/SimulationWorker/Mmorpg.SimulationWorker.csproj`,
`tools/SimulationBotClient/Mmorpg.Tools.SimulationBotClient.csproj`, and the
Unity manifest package `com.revenantx.litenetlib`. `Verify-DependencyPolicy.ps1`
fails when the versions differ, and Dependabot ignores LiteNetLib so the three
are always upgraded together by hand.

A Dependabot NuGet pull request rewrites only the `packages.lock.json` of the
projects that reference the package directly. Every project that reaches the
package through a `ProjectReference`, such as the test projects, keeps its old
lock file, so CI fails at the locked restore with `NU1004`. Refresh all lock
files on the pull request branch before review, then run the backend quality
gate:

```powershell
gh pr checkout <number>
dotnet restore Mmorpg.slnx --force-evaluate
dotnet restore Mmorpg.slnx --locked-mode
git add -- '*packages.lock.json'
git commit -m "Refresh NuGet lock files"
git push
```

Expected result: the locked restore succeeds and CI turns green on the pushed
commit. Dependabot stops rebasing a pull request once it carries a commit of its
own, so comment `@dependabot recreate` and refresh again if the pull request
falls behind `development`. Dependabot refreshes an open pull request in the
directories it was created for, so after a restructure it cannot update older
pull requests. Close them and let the next monthly run, or a manual check under
`Insights > Dependency graph > Dependabot`, propose them again.

`src/Client/Directory.Build.props` stops the repository build
settings from applying to the project files Unity generates, and those files
and their NuGet lock file are ignored by Git.

## Realtime Protocol Generation

The realtime protocol is described once in
`src/Shared/GameProtocol/Schema~/realtime-protocol.json`, and
`tools/ProtocolGenerator` writes `src/Shared/GameProtocol/Runtime/Generated`.
[Realtime Protocol](REALTIME_PROTOCOL.md) explains the schema and the rules.
After a schema change, regenerate and verify from any repository directory:

```powershell
dotnet run --project tools/ProtocolGenerator --configuration Release
dotnet run --project tools/ProtocolGenerator --configuration Release --no-build -- --verify
```

Expected result: both commands report realtime protocol version 17 with 28
messages and 16 generated files, and the second prints
`Realtime protocol version 17 verified: 28 messages, 16 generated files.`
The first command rewrites only changed files and removes files that the schema
no longer produces. The second fails and names the file when a generated file
is stale, missing, or not produced by the schema. Commit the schema,
`realtime-protocol.lock.json`, the generated `.cs` files, and their `.meta`
files together.

When generation reports that the wire contract of the current protocol version changed,
the schema change alters bytes or acceptance. Increase `protocol.version` in
the schema and generate again; the lock then records the new version next to
the old ones. No Unity
Editor step is required: the generator writes the `.meta` files with stable
GUIDs, and Unity compiles the new code when it next refreshes.

## Item Catalog Authoring And Verification

The neutral source catalog is `content/items`: `catalog.json` defines stable
category, tag, tool kind, equipment-slot, and Secure Container tier content and
the `economy` block with the currency name and the starting balance of new
characters, `definitions/<definition-id>.json` holds one item with its Bag
layout, location eligibility, policy defaults, `value`, and `toolKinds`, and
`vendors/<vendor-id>.json` holds one vendor profile with its offers, prices, and
the categories it buys. The generated runtime catalog is
`src/Shared/WorldData/Generated/Items/core.item-catalog.json`.

The catalog uses unitless non-negative integer `unitWeight`,
`carryCapacityBonus`, and `baseCarryCapacity` fields. Do not add physical-unit or
decimal weight fields. The baseline scale is ammunition `1`, a one-handed weapon
about `100`, and base character capacity `2000`. Item values are whole currency
units; a value of `0` means vendors never trade the item.

The canonical content workflow is available in Unity at
`MMORPG > Tools > Item Catalog`:

1. Search the definition list by id, name, category, or tag, and narrow it with
   the category, tag, and `Problems` filters. Hold Ctrl or Shift to select
   several definitions.
2. Edit gameplay fields and the separate client presentation fields in the same
   window. With several definitions selected, category, weight, stack size,
   tags, equipment compatibility, eligibility, and policies change for all of
   them at once, and a field whose values differ shows a dash. Id, display name,
   Bag layout, and presentation are edited one definition at a time. Baked
   definition ids are read-only and cannot be deleted or reused.
3. The shared compiler checks the catalog inside Unity while you edit. A field
   with a problem turns red, the list marks its definition, and choosing a
   problem in the panel below selects the definition and its field.
4. Undo and redo with Ctrl+Z and Ctrl+Y or the Edit menu; every edit is one step
   in `Edit > Undo History`. Unsaved edits survive script reloads, and closing
   the window asks whether to save them.
5. Optionally choose an icon Sprite below `Assets/Content/Items/Icons`, with one
   Sprite per asset file. The folder is an Addressables folder entry, so the
   tool stores the icon's Addressables address, such as
   `Items/Icons/test-ammunition.png`, not the image data, in the presentation
   JSON. An item may remain iconless until approved client art is available.
6. Select `Validate` to run the strict command-line compiler without writing
   files.
7. Select `Save` to write `catalog.json`, one file per definition, one file per
   vendor profile, and the presentation content without replacing the runtime
   gameplay catalog. The files of removed draft definitions and vendor profiles
   are deleted.
8. Select `Save And Bake` to validate and atomically replace authoring, runtime,
   and presentation JSON. Structural changes require explicit confirmation.

Each definition also has `Value` and `Tool Kinds` fields. The `Definitions` and
`Vendors` toolbar toggles switch the window between item definitions and vendor
profiles. Vendors mode lists the profiles with `New` and `Remove`, and edits the
vendor id, display name, `Selling Price (basis points)` that players pay,
`Buying Price (basis points)` that players receive, `Buys Categories`, and the
ordered offers with `Add Offer`, `Up`, `Down`, and `Remove`. Live problems are
mapped to the vendor and its field. The `economy` block and the tool kind
identities are edited by hand in `catalog.json`. Values, tool kinds, and vendor
profiles never change a structural fingerprint, so they need no item migration
or local database reset.

The gameplay source remains `content/items`. The Editor-only assembly does not
duplicate its rules: live checks call the shared compiler in `Mmorpg.WorldData`,
and `Validate` and both save actions invoke `tools/ItemCatalogCompiler`, which
remains the validation and structural-fingerprint authority used by backend
development and CI.

Client-only presentation content is bundled through Addressables at
`src/Client/Assets/Content/Items/Presentation/item-presentation-catalog.json`.
It maps every stable gameplay definition id to an optional icon address,
localization key, fallback display name, and optional prefab presentation key.
The file records the exact gameplay source revision and a separate deterministic
presentation revision. Runtime lookup validates the pair once and caches it.
Inventory responses therefore need definition ids and instance state, not icon
files or complete definition data.

After an intentional authoring change, compile the runtime catalog:

```powershell
dotnet run --project tools/ItemCatalogCompiler
```

Then verify the checked-in result:

```powershell
dotnet run --project tools/ItemCatalogCompiler -- --verify
```

Expected result: both commands report catalog `core`, eleven definitions, one
base Secure Container tier, and the same SHA-256 catalog revision. `--verify`
returns exit code 1 for malformed authoring, a definition file whose name does
not match its id, invalid rules, or stale runtime JSON.

Inspect `structuralFingerprint` for every changed definition and Secure
Container tier. A display-only edit changes the catalog revision without
changing an item structural fingerprint. Weight, stack, tag, equipment,
location, policy-default, Bag-layout, or tier-capacity changes alter the
relevant structural fingerprint. GameService now permits a structural revision
only when no live item instance or affected Secure Container entitlement depends
on it. Otherwise startup fails with the affected stable id and requires an
explicit data migration before the new catalog can become current. Display-only
changes remain safe because they preserve the structural fingerprint.

The complete authoring contract is documented in `content/items/README.md`, and
the general content rules in `content/README.md`. No Unity Editor action is required for
command-line item catalog compilation or verification.

If `Validate`, `Save`, or `Save And Bake` fails, read the field paths in the
window error. Validation uses temporary candidate files. A failed validation
writes nothing, and a failed multi-file bake restores every previous catalog
file. Correct or discard the draft, select `Reload` to return to checked-in
content when appropriate, and run the command-line `--verify` command above.

## Item Persistence Verification

Start the isolated temporary PostgreSQL database and run only the item persistence
integration suite:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$values = @{}
Get-Content .env | ForEach-Object {
  if ($_ -match '^([^#=]+)=(.*)$') {
    $values[$matches[1]] = $matches[2]
  }
}
$env:MMORPG_TEST_POSTGRES = `
  "Host=127.0.0.1;Port=55432;" + `
  "Database=$($values['TEST_POSTGRES_DB']);" + `
  "Username=$($values['TEST_POSTGRES_USER']);" + `
  "Password=$($values['TEST_POSTGRES_PASSWORD'])"
dotnet test tests/Server/Mmorpg.GameService.Items.Tests `
  --configuration Release `
  --filter "FullyQualifiedName~ItemPersistenceIntegrationTests"
```

Expected result: eleven tests pass. They reset only the dedicated database whose
name contains `test`, then verify the complete catalog mirror, legacy backfill,
atomic new-character bootstrap, uniqueness and location constraints, planned
custody shapes, explicit delete behavior, and structural catalog startup fence.

To exercise concurrent migration initialization as well, run:

```powershell
dotnet test tests/Integration/Mmorpg.Integration.Tests `
  --configuration Release `
  --filter "FullyQualifiedName~ConcurrentMigrationInitializationAppliesEachMigrationOnce"
```

Expected result: the test passes with every immutable migration id applied
exactly once, one current catalog revision, and no duplicate topology or item
seed rows.

For an existing local development database, start GameService normally. Expect
one log entry if the new migration or a new catalog revision is applied. Inspect
the resulting foundation without exposing database credentials:

```powershell
docker compose exec postgres sh -lc 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "select catalog_id, revision, is_current from item_catalog_revisions order by applied_at;"'
docker compose exec postgres sh -lc 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "select count(*) as active_characters_missing_item_state from characters c left join character_item_states s on s.character_id = c.id where c.deleted_at is null and s.character_id is null;"'
```

Expected result: catalog `core` has exactly one row with `is_current = true`,
and `active_characters_missing_item_state` is `0`. No Unity Editor action is
required for this verification.

Stop the isolated test database when finished:

```powershell
docker compose -f docker-compose.test.yml down
```

## Item Read Model Verification

Start the isolated PostgreSQL database, configure the dedicated test connection
as shown in [Item Persistence Verification](#item-persistence-verification), then
run the read-model suite:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$values = @{}
Get-Content .env | ForEach-Object {
  if ($_ -match '^([^#=]+)=(.*)$') {
    $values[$matches[1]] = $matches[2]
  }
}
$env:MMORPG_TEST_POSTGRES = `
  "Host=127.0.0.1;Port=55432;" + `
  "Database=$($values['TEST_POSTGRES_DB']);" + `
  "Username=$($values['TEST_POSTGRES_USER']);" + `
  "Password=$($values['TEST_POSTGRES_PASSWORD'])"
dotnet test tests/Server/Mmorpg.GameService.Items.Tests `
  --configuration Release `
  --filter "FullyQualifiedName~ItemReadModelIntegrationTests"
docker compose -f docker-compose.test.yml down
```

Expected result: seven tests pass. They verify the current catalog graph,
cross-account denial, complete empty state, stable slot ordering, all owned
snapshot sections, fixed-point encumbrance, definition-id deduplication,
secret exclusion, and read-only behavior. Fixture items are written only by
test-project helpers to the dedicated test database. GameService exposes no
grant route.

To exercise the two real HTTP reads, start local PostgreSQL and Redis with
`docker compose up -d --wait`, then run `dotnet run --project src/Server/GameService`. In a
second PowerShell terminal, create a disposable account and character:

```powershell
$suffix = [Guid]::NewGuid().ToString("N").Substring(0, 16)
$registration = Invoke-RestMethod `
  -Method Post `
  -Uri http://localhost:5000/api/accounts/register `
  -ContentType application/json `
  -Body (@{
    email = "phase4-$suffix@example.test"
    username = "phase4_$suffix"
    password = "TestPass123!"
  } | ConvertTo-Json)
$headers = @{ Authorization = "Bearer $($registration.sessionToken)" }
$character = Invoke-RestMethod `
  -Method Post `
  -Uri http://localhost:5000/api/characters/ `
  -Headers $headers `
  -ContentType application/json `
  -Body (@{ name = "Item Tester $($suffix.Substring(0, 8))" } | ConvertTo-Json)
$catalog = Invoke-RestMethod `
  -Uri http://localhost:5000/api/item-catalog `
  -Headers $headers
$snapshot = Invoke-RestMethod `
  -Uri "http://localhost:5000/api/characters/$($character.id)/item-state" `
  -Headers $headers
$catalog.definitions.Count
$snapshot.permanentInventory.slots.Count
$snapshot.bank.slots.Count
$snapshot.secureContainer.contents.slots.Count
$snapshot.currency
```

Expected result: the values are `11`, `20`, `40`, `4`, and `100`. The snapshot
is empty but complete, reports capacity `2000` and the catalog's starting
balance of `100` Gold, and contains no operation payload, credentials, client
icon, or repeated definition metadata. No Unity Editor action is required for
this verification.

## Item Transaction Kernel Verification

The kernel has no HTTP or Unity flow of its own. Verify it against
the isolated PostgreSQL database:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$values = @{}
Get-Content .env | ForEach-Object {
  if ($_ -match '^([^#=]+)=(.*)$') {
    $values[$matches[1]] = $matches[2]
  }
}
$env:MMORPG_TEST_POSTGRES = `
  "Host=127.0.0.1;Port=55432;" + `
  "Database=$($values['TEST_POSTGRES_DB']);" + `
  "Username=$($values['TEST_POSTGRES_USER']);" + `
  "Password=$($values['TEST_POSTGRES_PASSWORD'])"
dotnet test tests/Server/Mmorpg.GameService.Items.Tests `
  --configuration Release `
  --filter "FullyQualifiedName~ItemConcurrencyIntegrationTests"
Remove-Item Env:MMORPG_TEST_POSTGRES
docker compose -f docker-compose.test.yml down
```

Expected result: seventeen tests pass. The suite executes every internal command
and verifies account authorization, effective policy lineage, specific and
deterministic slots, equipment, empty and non-empty Bag rules, Recovery add and
claim, account-wide Secure Container tier changes, unitless carried-state
recomputation, exact hard-cap rollback, canonical operation replay, competing
item, slot, equipment, and quantity races, the shared Bag aggregate lock, and
failed-swap rollback. The dedicated database is reset between tests.

No Unity Editor action is required for this verification.

## Item Policy And Offline Item API Verification

Run the policy and HTTP integration suites against the isolated test
database:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$values = @{}
Get-Content .env | ForEach-Object {
  if ($_ -match '^([^#=]+)=(.*)$') {
    $values[$matches[1]] = $matches[2]
  }
}
$env:MMORPG_TEST_POSTGRES = `
  "Host=127.0.0.1;Port=55432;" + `
  "Database=$($values['TEST_POSTGRES_DB']);" + `
  "Username=$($values['TEST_POSTGRES_USER']);" + `
  "Password=$($values['TEST_POSTGRES_PASSWORD'])"
dotnet test tests/Server/Mmorpg.GameService.Items.Tests `
  --configuration Release `
  --filter "FullyQualifiedName~ItemPolicyLifecycleIntegrationTests|FullyQualifiedName~ItemApiIntegrationTests"
Remove-Item Env:MMORPG_TEST_POSTGRES
docker compose -f docker-compose.test.yml down
```

Expected result: nine tests pass. They verify insurance application and
removal, exact quest-grant cleanup and reaccept, catalog ETag and `304`, no-store
headers, owner scoping, Secure Container tier access, stable Problem Details,
offline session fencing, stable split, merge, and destroy validation, offline
bank and Recovery rejection, system delivery, successful kernel bank claim, and
hard-cap claim rollback. The test host uses the real account
session authentication handler and item endpoints over loopback HTTP.

To inspect the account HTTP contract manually, start local PostgreSQL and
Redis with `docker compose up -d --wait`, run
`dotnet run --project src/Server/GameService`, and use the registration commands from
[Item Read Model Verification](#item-read-model-verification) to
create `$headers` and `$character`. The `-SkipHttpErrorCheck` switch needs
PowerShell 7 (`pwsh`), so run this snippet in `pwsh`, not Windows PowerShell 5.1.
Then run:

```powershell
$catalogResponse = Invoke-WebRequest `
  -Uri http://localhost:5000/api/item-catalog `
  -Headers $headers
$etag = $catalogResponse.Headers.ETag
$conditionalHeaders = $headers.Clone()
$conditionalHeaders['If-None-Match'] = $etag
$unchangedResponse = Invoke-WebRequest `
  -SkipHttpErrorCheck `
  -Uri http://localhost:5000/api/item-catalog `
  -Headers $conditionalHeaders
$itemStateResponse = Invoke-WebRequest `
  -Uri "http://localhost:5000/api/characters/$($character.id)/item-state" `
  -Headers $headers
$bankResponse = Invoke-WebRequest `
  -Uri "http://localhost:5000/api/characters/$($character.id)/bank" `
  -Headers $headers
$recoveryResponse = Invoke-WebRequest `
  -Uri "http://localhost:5000/api/characters/$($character.id)/recovery" `
  -Headers $headers
$itemState = $itemStateResponse.Content | ConvertFrom-Json
$tierResponse = Invoke-WebRequest `
  -Method Post `
  -Uri http://localhost:5000/api/items/secure-container-tier `
  -Headers $headers `
  -ContentType application/json `
  -Body (@{
    operationId = [Guid]::NewGuid()
    expectedEntitlementRevision = $itemState.secureContainer.entitlementRevision
    tierId = $itemState.secureContainer.tierId
    characterRevisions = @(@{
      characterId = $character.id
      revision = $itemState.itemStateRevision
    })
  } | ConvertTo-Json -Depth 5)
@(
  $catalogResponse.StatusCode
  $unchangedResponse.StatusCode
  $itemStateResponse.StatusCode
  $bankResponse.StatusCode
  $recoveryResponse.StatusCode
  $tierResponse.StatusCode
)
$itemStateResponse.Headers.'Cache-Control'
$bankResponse.Headers.'Cache-Control'
$recoveryResponse.Headers.'Cache-Control'
```

Expected result: the status sequence is `200`, `304`, `200`, `200`, `200`,
`200`. The catalog has an ETag and the unchanged response has no body. Every
character response reports `no-store`. The same-tier request succeeds
idempotently while the character is offline. Item relocation, split, merge,
destruction, Recovery claims, active-session rejection, and policy lifecycle are
covered by the isolated integration suite until gameplay grants create real
items for manual interaction.

No Unity Editor action is required for this verification.

## Carry State And Encumbrance Verification

Run the carry-state unit, protocol, worker, and isolated PostgreSQL coverage
against the dedicated test database:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$values = @{}
Get-Content .env | ForEach-Object {
  if ($_ -match '^([^#=]+)=(.*)$') {
    $values[$matches[1]] = $matches[2]
  }
}
$env:MMORPG_TEST_POSTGRES = `
  "Host=127.0.0.1;Port=55432;" + `
  "Database=$($values['TEST_POSTGRES_DB']);" + `
  "Username=$($values['TEST_POSTGRES_USER']);" + `
  "Password=$($values['TEST_POSTGRES_PASSWORD'])"
dotnet test Mmorpg.slnx `
  --configuration Release `
  --filter "FullyQualifiedName~PlayerMovementSimulationTests|FullyQualifiedName~CarryStateStoreTests|FullyQualifiedName~SimulationSessionReleaseServiceTests|FullyQualifiedName~AuthoritativePlayerMovementTests|FullyQualifiedName~RealtimeProtocolTests|FullyQualifiedName~RealtimeSimulationServiceTests|FullyQualifiedName~CarryStateAndEquipmentAreFencedIntoJoinHeartbeatAndReconnect|FullyQualifiedName~CarryStateCountsEveryCarriedCustodyExactlyOnceAndExcludesExternalCustody|FullyQualifiedName~SwappingToLowerCapacityBagRejectsTheCompleteAggregateAboveHardCap"
Remove-Item Env:MMORPG_TEST_POSTGRES
docker compose -f docker-compose.test.yml down
```

Expected result: every selected test passes. The suite verifies base capacity
from the catalog, equipped Bag capacity, exact custody contribution, exclusion of bank,
Recovery Storage, and corpse custody, lower-capacity Bag rollback, full
movement through 100 percent, the linear multiplier through `0.20` at 140 percent,
monotonic active-session propagation, protocol validation, and reconnect
restoration.

Run all Unity tests through the repository script:

```powershell
powershell -ExecutionPolicy Bypass -File tools/Run-UnityTests.ps1
```

Expected result: all EditMode and PlayMode tests pass. The EditMode suite runs
the same shared GameSimulation reference points and verifies monotonic client
carry-state updates.

For a manual default-state and reconnect check:

1. Start PostgreSQL and Redis with `docker compose up -d --wait`.
2. Run `dotnet run --project src/Server/GameService` in one terminal.
3. Run `dotnet run --project src/Server/SimulationWorker` in a second terminal. Confirm
   startup reports realtime protocol version `16` and simulation revision
   `movement-simulation-v6`.
4. Open `src/Client` in Unity `6000.5.4f1`, open LoginMenu,
   enter Play Mode, register or log in, select a character and shard, and join.
5. Press F2 in the active World scene. A new empty character shows weight
   `0 / 2000`, movement `100%`, and its committed item-state revision.
6. Leave to CharacterSelect and join the same character again. The carry tuple
   and revision are restored and movement remains available.
7. Exit Play Mode and stop both backend processes and Compose services.

No Inspector, scene, prefab, package, input-action, or build-setting change is
required for this verification.

## In-World Item Mutation Verification

Run the in-world mutation protocol, worker, HTTP client, and isolated PostgreSQL
coverage:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$values = @{}
Get-Content .env | ForEach-Object {
  if ($_ -match '^([^#=]+)=(.*)$') {
    $values[$matches[1]] = $matches[2]
  }
}
$env:MMORPG_TEST_POSTGRES = `
  "Host=127.0.0.1;Port=55432;" + `
  "Database=$($values['TEST_POSTGRES_DB']);" + `
  "Username=$($values['TEST_POSTGRES_USER']);" + `
  "Password=$($values['TEST_POSTGRES_PASSWORD'])"
dotnet test Mmorpg.slnx `
  --configuration Release `
  --filter "FullyQualifiedName~SimulationItemMutationIntegrationTests|FullyQualifiedName~DuplicateReliableItemIntentsReplayOneCommittedCarryRevision|FullyQualifiedName~ItemOperationIntentsRoundTripEveryPhaseEightMutationKind|FullyQualifiedName~ItemOperationResultRoundTripsCommittedRevisionsAndRefreshSignal|FullyQualifiedName~ItemOperationIntentRejectsUnboundedRecoveryClaims|FullyQualifiedName~SimulationItemMutationSendsExactLiveAuthorityAndValidatesCommittedCarry"
Remove-Item Env:MMORPG_TEST_POSTGRES
docker compose -f docker-compose.test.yml down
```

Expected result: every selected test passes. The integration suite rejects every
wrong account, character, session, token, worker, runtime, Shard, active account
session, and stale assignment. It also verifies service authentication,
idempotent duplicate intents, account-versus-worker race safety, bank and
Recovery access, world-available Secure Container mutation, carry propagation,
heartbeat, and reconnect restoration.

Run all Unity tests:

```powershell
powershell -ExecutionPolicy Bypass -File tools/Run-UnityTests.ps1
```

Expected result: all EditMode and PlayMode tests pass. EditMode tests
round-trip typed item intents and committed results through the same protocol
source compiled by the backend and Unity.

For a manual runtime smoke check:

1. Start PostgreSQL and Redis with `docker compose up -d --wait`.
2. Run `dotnet run --project src/Server/GameService` in one terminal.
3. Run `dotnet run --project src/Server/SimulationWorker` in a second terminal. Confirm it
   reports the current realtime protocol version and loads the configured World
   without a configuration error.
4. Open `src/Client` in Unity `6000.5.4f1`, open LoginMenu,
   enter Play Mode, log in, select a character and shard, and join the mapped
   development World scene.
5. Press F2 and confirm movement and the admitted carry tuple remain available.
   This step verifies the in-world mutation boundary. The inventory panel is
   documented in [Unity Inventory Verification](#unity-inventory-verification).
6. Leave and reconnect. Confirm the same committed carry revision is restored.
7. Exit Play Mode and stop both backend processes and Compose services.

No Inspector, scene, prefab, package, input-action, or build-setting changes are
required for this verification. No manual Unity Editor setup is required.

## Unity Inventory Verification

Run the dedicated fixture and client-foundation coverage against the isolated
test database:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$env:MMORPG_TEST_POSTGRES = `
  (Get-Content .env | Where-Object {
    $_ -like "MMORPG_TEST_POSTGRES=*"
  }).Split("=", 2)[1]
dotnet test tests/Server/Mmorpg.GameService.Items.Tests `
  --configuration Release `
  --filter "FullyQualifiedName~DevelopmentItemPackage|FullyQualifiedName~DevelopmentItemTool|FullyQualifiedName~ItemReadModelIntegrationTests"
Remove-Item Env:MMORPG_TEST_POSTGRES
docker compose -f docker-compose.test.yml down
powershell -ExecutionPolicy Bypass -File tools/Run-UnityTests.ps1
```

Expected result: every selected backend test passes, including one real
PostgreSQL transaction-service fixture flow, Editor-tool list and grant flows,
exact 140 percent package weight, the package check against the catalog before
the first grant, hard-cap rejection, readback, Recovery revision, and replay
refusal. Both Unity suites pass. EditMode coverage
verifies the shared Editor menu root and machine-readable tool response, the
Editor tool's Release build before every command, its refusal to run anything
after a failed or timed out build, the exception message shown first for a
GameService startup failure, the end of the whole process tree on cancel or
timeout, the presentation catalog cache, catalog mismatch, complete and
focused snapshots, monotonic and divergent revisions, operation correlation,
specialized targets, Secure Container eligibility, non-empty Bag rejection,
split weight, and the 140 percent hard cap, plus typed valid and invalid
drag-and-drop behavior and the panel presentation: paper doll positions, carry
meter levels, blocked equipment slots, catalog labels, and tooltips. PlayMode
verifies the persistent controller and runtime uGUI root, and draws a committed
snapshot in the complete view without a GameService.

### Give A Character Development Items

The Unity Editor window is the primary workflow. It never writes directly to
PostgreSQL. Before every command it builds GameService incrementally with
`dotnet build src/Server/GameService/Mmorpg.GameService.csproj --configuration Release`,
so a merge or local edit can never leave it running older code against a newer
database. The build runs without build servers and with one in-process MSBuild
node, so nothing outlives it. When GameService is up to date the build takes
about five seconds. The window then runs exactly the assembly that build
reports, as `dotnet src/Server/GameService/bin/Release/<framework>/Mmorpg.GameService.dll`,
so GameService runs inside that one process. Closing the window, a script
reload, the five-minute build timeout, or the 60-second command timeout ends the
running build or command together with every process it started. It starts
a guarded GameService command that uses
`ItemTransactionService`, accepts only Development, requires a loopback
PostgreSQL host with a non-production-like database name, and refuses an active
character. Individual grants are repeatable. Exact test packages require a
character with no items or Recovery deliveries.

The offline requirement protects live authority. The Editor command commits
directly through GameService and does not have a SimulationWorker result path to
advance an already joined session's carry tuple and item-state revision. Simply
removing the guard could leave the worker and Unity on stale live state. A safe
online grant tool must be a separate service-authenticated development intent
routed through the worker that currently owns the character.

1. Start normal local infrastructure and GameService. No separate Release
   build is needed, because the window builds GameService itself:

   ```powershell
   docker compose up -d --wait
   dotnet run --project src/Server/GameService
   ```

2. Open LoginMenu in Unity, enter Play Mode, register a disposable local account,
   and create a new character. Do not join a shard. Exit Play Mode. GameService
   may remain running.
3. Select `MMORPG > Tools > Inventory Item Grants` from Unity's top menu.
   The window refreshes automatically. The toolbar shows
   `Building GameService...` and then `GameService command running...`. Select
   the new offline character. The row shows account, online state, item count,
   weight, and capacity.

   If GameService does not compile, the window reports
   `GameService could not be built, so the tool did not run an older build`
   followed by the first compiler errors, and runs nothing. If GameService
   stops before it answers, for example because the local database does not
   match the source, the window shows the exception message first, such as
   `GameService failed before it returned a development item response:
   Npgsql.PostgresException (0x80004005): 42703: column "sort_order" does not
   exist`. In both cases the complete build or GameService output, including the
   stack trace, is logged as one error in the Unity Console.
4. For a custom grant, choose an item definition, quantity, and Permanent
   Inventory, Bank, or Secure Container destination. Click `Give Item`.
   Expected result: the status reports the committed grant and the character row
   immediately shows the updated item count, weight, capacity, and revision.
5. For a complete scenario, choose a package and click `Give Package`. Available
   packages are:

   - Full Inventory Test Pack
   - Equipment Test Pack
   - Stack Split And Merge Pack
   - Encumbrance 100% Pack
   - Encumbrance 140% Pack
   - Secure Container Pack
   - Recovery Delivery Pack

   Expected result: an empty offline character receives the selected
   deterministic state. The Full Inventory Test Pack reports weight `1040 / 2500`. The
   encumbrance packs report `2000 / 2000` and `2800 / 2000`. Package controls become
   unavailable once that character owns items, while individual grants remain
   available. Invalid stack, Secure Container, slot, or hard-cap requests are
   rejected by GameService without a partial item transaction.
6. To give currency, use the `Give Currency` section. It shows the selected
   character's `Balance`. Enter a positive `Amount`, which defaults to `100`,
   and click `Give Currency`. The button is available only while the character
   is offline. Expected result: the status reports
   `Granted <amount> currency to <character>, who now has <balance>.` and
   `Balance` shows the new value. The grant goes through the character wallet
   and writes a `development` row to `character_currency_ledger`, like every
   other balance change. A grant above the maximum balance of
   `1,000,000,000,000` is rejected and changes nothing. Do not set currency with
   SQL; a direct update would bypass the ledger.
7. To create a durable corpse, keep a source character offline and use the
   `Create Durable Corpse` section. Leave Shard Id as `local-shard-1`, enter a
   nearby safe World position such as `(0, 0, -1)`, and click
   `Create Durable Corpse`. An empty source first receives the corpse seed package, the
   Full Inventory Test Pack. A non-empty source uses its current item state. A source with
   only pending Recovery deliveries cannot be auto-seeded.

   Expected result: the status reports the corpse id, exact Shard, absolute
   five-minute expiry, and committed item-state revision. Restart
   SimulationWorker after creation so its fresh runtime restores the corpse.
8. Start SimulationWorker with its Development launch profile, enter Play Mode,
   log into the target account, select its character, and join the local shard:

   ```powershell
   dotnet run --project src/Server/SimulationWorker
   ```

   Expected result: startup logs
   `Development global Bank and Recovery Storage access is enabled`. Bank and
   Recovery mutations are then available at every authoritative player position.
   Restart SimulationWorker after changing the Development configuration.

No Inspector, scene, prefab, package, input-action, or build-setting edit is
required. The Editor window, gameplay catalog reference, `B`, `C`, and `I`
bindings, persistent controller, EventSystem, Canvas, and temporary uGUI
   hierarchy, corpse controller, and generic presentation are already authored
   or created by the maintained foundation.

The terminal interface remains available as a fallback or for automation. It
uses the same guardrails and transaction service. `dotnet run` builds
GameService first, so do not add `--no-build`:

```powershell
dotnet run --project src/Server/GameService --configuration Release -- `
  --dev-items-list

dotnet run --project src/Server/GameService --configuration Release -- `
  --dev-items-grant <characterId> material.iron_ore 20 bank

dotnet run --project src/Server/GameService --configuration Release -- `
  --dev-items-package <characterId> full_inventory

dotnet run --project src/Server/GameService --configuration Release -- `
  --dev-items-corpse <characterId> local-shard-1 0 0 -1

dotnet run --project src/Server/GameService --configuration Release -- `
  --dev-items-level <characterId> 5

dotnet run --project src/Server/GameService --configuration Release -- `
  --dev-items-currency <characterId> 500
```

`--dev-items-list` reports every character with its `currency` balance next to
its item count, weight, capacity, and revision.

`--dev-items-level` sets an offline character's level, from 1 up to the item
catalog's `progression.maximumCharacterLevel`. Experience does not exist yet, so
this is how an item level requirement is tested: every character starts at level
1, and GameService refuses to equip an item whose `requiredCharacterLevel` is
higher with `equipment_level_requirement_not_met`. Every shipped item requires
level 1.

`--dev-items-currency` credits a positive whole amount of currency to an offline
character through the character wallet, exactly like the `Give Currency`
section, and writes a `development` ledger row. Use it instead of changing
`characters.currency` with SQL. New characters start with the catalog's
starting balance of `100` Gold, and existing characters keep their balance.

The packages come from `content/development/item-packages.json`, which GameService
copies next to its build output. Before the first grant, the tool checks the
package against the current item catalog. A package that names an unknown
definition, exceeds a stack size, equips an item into a slot that cannot hold it
or that another equipped item blocks, or no longer reaches its exact expected load
is rejected with a message that names the package and every problem. Nothing is
granted, so the character stays empty for the corrected package.

### Manual Player Loop

1. Press `B` and verify only the right column is shown: the carry meter,
   Permanent Inventory, the equipped Traveler's Pack contents, and the Warded
   Pouch. Press `C` and verify the equipment paper doll appears on the left.
   Press `I` and verify the complete Development view adds the middle Bank,
   Recovery, and Corpse column. Pressing the active key closes that view,
   pressing another inventory key switches views, and `Escape` closes any view;
   the cursor stays free. Verify that hidden modules leave no background or
   header behind and that the right column keeps the same position and size in
   all three views.
2. Check the presentation. Every slot has the same size. Occupied slots show
   the item name, a colored top strip for the item category, and the stack
   quantity in the lower-right corner; empty slots are darker. The Traveler's
   Pack specialized slots show `Food/Potion`, `Material`, and `Ammunition`. The
   paper doll shows Tool, Head, and Bag on top, Main Hand, Body, and Off Hand in
   the middle, and Feet at the bottom. Hover any item: a tooltip shows its name,
   category, quantity, weight, equipment slots, and policies. The carry meter is
   green with the caption `Weight 1040 / 2500    Load 41.6%    Speed 100%`.
3. Drag the shortbow, jerkin, pickaxe, or ring from Permanent inventory onto a
   compatible empty equipment slot. Drag the equipped item onto an empty
   Permanent slot to unequip it. Valid targets highlight green, invalid targets
   highlight red, the dragged label trails beside the pointer without covering
   the target, and the UI waits for the committed refresh before showing the
   new location. The carry meter drops by the equipped item's own weight and
   restores it when the item is unequipped into carried storage. With the
   shortbow equipped, Off Hand turns red and shows `Blocked`, and its tooltip
   names the shortbow. Clicking a destination without dragging does not move an
   item.
4. Drag the 100 Bronze Arrows from the Bag ammunition slot onto the Ammunition
   equipment slot. The weight drops by `100`, and the equipped slot shows `100`.
   Select the equipped arrows; the footer below the right column shows the
   selection. Enter `40`, click `Split`, and drag them onto an empty Permanent slot:
   `60` stay equipped and the weight rises by `40`. Drag the `40` arrows back
   onto the equipped stack to top it up to `100`, then drag the equipped stack
   back onto the Bag ammunition slot. The weight returns to `1040 / 2500`.
5. Drag the empty Traveler's Pack from Permanent inventory into one of the
   equipped Bag's general slots and back. Open Bank and drag the second empty
   Traveler's Pack between Bank and Permanent inventory. Click the equipped
   non-empty Traveler's Pack
   only to inspect its actions, then drag it and confirm ordinary Permanent, Bag,
   and Bank destinations reject it.
6. Drag the healing potion, iron ore, and arrows out of and back into the
   supplies, material, and ammunition specialized slots. Drag the shortbow across a
   specialized slot and Secure Container target and confirm both reject it.
7. In Bank, click an iron-ore stack, enter the split quantity in the footer,
   click `Split`, then drag that selected stack onto an empty Bank slot. Drag
   the resulting stack onto its compatible peer to merge it back. Confirm source
   quantities and item identities change only after the server result and
   refresh. Drag two unlike items between compatible occupied ordinary slots and
   confirm they exchange slots in one committed refresh. Repeat against an
   incompatible specialized or Secure Container slot and confirm neither moves.
8. In Recovery, drag either item in the prepared delivery onto a Permanent or
   Bank slot. The complete delivery disappears and both original item instances
   appear in the chosen container only after the committed full refresh.
9. Starting from fixture weight `1040 / 2500`, move Bank iron ore quantity `20`
   to Permanent inventory. Weight becomes `2240 / 2500`. Move Bank iron ore
   quantity `19` to reach `3380 / 2500`, above 100 percent, where the movement
   multiplier declines linearly and the carry meter turns amber with
   `(slowed)`. Move the single healing potion to reach `3400 / 2500`, followed
   by the empty Bank Traveler's Pack to reach exactly `3500 / 2500`, load
   `140%`, and speed `20%`. The carry meter is full and red with
   `(at the carry limit)`.
10. Attempt to move the Bank arrow stack into carried storage. The local target
   is disabled. Any equivalent authoritative request is rejected by the server,
   and the committed weight remains exactly `3500 / 2500`.
11. Leave to CharacterSelect and rejoin. Press `I` and confirm every item location,
    quantity, carry value, and revision is restored without loss or duplication.
12. Reopen and refresh Bank and Recovery repeatedly. Definition labels and icon
    references remain stable. Automated EditMode coverage also asserts that both
    catalog loads return the same presentation index and icon cache.

The checked-in `src/Server/SimulationWorker/Config/appsettings.Development.json` sets
`DevelopmentItemInteractions:GlobalBankAndRecoveryAccess=true`. This is a local
testing capability, not a GameService or protocol bypass. If the same setting is
enabled outside the Development environment, SimulationWorker refuses startup.
Production requires an open interaction with an NPC that offers Bank or Recovery
Storage, and insurance always requires the insurance NPC capability.

To test the production access path, start SimulationWorker with the global
access turned off:

```powershell
$env:DevelopmentItemInteractions__GlobalBankAndRecoveryAccess = "false"
dotnet run --project src/Server/SimulationWorker
```

1. Join the shard, press `I`, and move a Bank item into Permanent inventory.
   Expected result: the move is rejected with `bank_access_required`.
2. Left-click Mira the Quartermaster and select `Open Character Bank` in the
   interaction panel. Expected result: the interaction reports that the Character
   Bank is open, and the inventory opens in the complete view with the Bank
   context. The Bank header shows `via Mira the Quartermaster`. Moving an item
   between Bank and Permanent inventory now commits.
3. Select `Open Recovery Storage` in the same interaction. Expected result: the
   Recovery context opens, and a prepared delivery can be claimed.
4. Walk away from Mira until the interaction closes, then move a Bank item again.
   Expected result: the move is rejected with `bank_access_required`.

Remove the variable with `Remove-Item Env:DevelopmentItemInteractions__GlobalBankAndRecoveryAccess`
when finished.

The catalog-update path is covered by
`MapperRejectsCatalogMismatchAndInvalidDeliveryRevision`: a mismatched server
revision is refused and the controller presents `item_catalog_update_required`
instead of rendering item slots.

World-loot keeps its reserved context identity. Corpse inspection, concurrent
loot, partial stacks, and atomic equipped-Bag swaps use the separate flow in
[Concurrent Corpse Interaction Verification](#concurrent-corpse-interaction-verification).

## Player Death And Durable Corpse Verification

Combat produces deaths in SimulationWorker, as
[Combat Verification](#combat-verification) shows in Unity. Verify the complete
durable boundary against the isolated PostgreSQL database:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$env:MMORPG_TEST_POSTGRES = `
  (Get-Content .env | Where-Object {
    $_ -like "MMORPG_TEST_POSTGRES=*"
  }).Split("=", 2)[1]
dotnet test Mmorpg.slnx `
  --configuration Release `
  --filter "FullyQualifiedName~DeathLootIntegrationTests|FullyQualifiedName~DurableCorpseStoreTests|FullyQualifiedName~GameServiceClientTests"
Remove-Item Env:MMORPG_TEST_POSTGRES
docker compose -f docker-compose.test.yml down
```

Expected result: every selected test passes. The suite verifies the migration and
exact five-minute database constraint, service authentication and live-session
fencing, one death partition under replay, total item custody conservation,
unchanged currency and Secure contents, protected and insurance precedence,
insured equipment and Bag snapshots, child-before-Bag Recovery ordering,
involuntary over-cap death after Bag capacity loss, weight-reducing remediation,
rejection of further weight gain, cross-Shard worker restoration, empty-corpse
lifetime, one audited destruction per remaining item, and a
custody-versus-expiry race with one final outcome.

No manual Unity Editor steps are required for this verification. Do not change a
scene, prefab, Inspector property, input action, package, or build setting. The
corpse interaction verification below
uses the prepared exact-session death boundary for its Development corpse
fixture, so it needs no combat kill.

## Concurrent Corpse Interaction Verification

Run the complete corpse interaction backend coverage against the isolated PostgreSQL
database, then run both Unity suites:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$env:MMORPG_TEST_POSTGRES = `
  (Get-Content .env | Where-Object {
    $_ -like "MMORPG_TEST_POSTGRES=*"
  }).Split("=", 2)[1]
dotnet test Mmorpg.slnx `
  --configuration Release `
  --filter "FullyQualifiedName~CorpseLootConcurrencyIntegrationTests|FullyQualifiedName~CorpseInteractionEndpointsEnforceAuthorityCommitAndCloseTransactions|FullyQualifiedName~CorpseFixtureSeedsEmptyCharacterAndUsesDurableDeathPipeline|FullyQualifiedName~RealtimeCorpseProtocolTests|FullyQualifiedName~CorpseRealtimeStateTests|FullyQualifiedName~SimulationCorpseInteractionServiceTests"
Remove-Item Env:MMORPG_TEST_POSTGRES
docker compose -f docker-compose.test.yml down
powershell -ExecutionPolicy Bypass -File tools/Run-UnityTests.ps1
```

Expected result: every selected backend test and both Unity suites pass. The
coverage proves unrelated concurrent commits, one winner for the same item,
partial-stack conservation in both directions, ordinary slot swaps, deposit
policy rejection, deposit-versus-loot race safety, Bag-versus-child and
Bag-versus-Bag aggregate safety, 140 percent rejection, equal rules for the dead
player, exact runtime authority, closed PostgreSQL transactions, bounded UTF-8
packets, corpse-internal move, split, merge, and swap, canonical corpse equipment
slot identity and compatibility, viewer deltas, committed-revision inventory
refresh, stable closure codes, monotonic client state, and the durable Editor
fixture path.

### Manual Two-Client Corpse Loop

No scene, prefab, Inspector, input-action, package, or build-setting edit is
required. One Development player build is required so two clients can be active
at the same time:

1. In Unity, open `File > Build Profiles`, select Windows, enable
   `Development Build`, and build the existing scene list, LoginMenu and
   CharacterSelect. Do not add or reorder scenes: World scenes are Addressables,
   and the player build builds their content first. Expected result: the
   standalone player reaches LoginMenu.
2. Start PostgreSQL, Redis, and GameService. Using the Editor client, create
   three disposable accounts with one character each: one offline corpse source
   and two looters. Apply `Full Inventory Test Pack` to both looters while they are
   offline so each has an occupied equipped Bag.
3. In `MMORPG > Tools > Inventory Item Grants`, select the offline source.
   Under `Create Durable Corpse`, keep Shard Id `local-shard-1`, use World
   Position `(0, 0, -1)`, and click `Create Durable Corpse`. Expected result:
   the tool reports a corpse id and an expiry five minutes in the future.
4. Start or restart SimulationWorker after the fixture commits. Run the built
   player and enter Play Mode in the Editor. Log the two looters into their
   separate accounts and join `local-shard-1`. Both spawn beside the generic
   corpse capsule.
5. Click the corpse capsule in both clients. Expected result: each player walks
   into range and opens the same complete
   three-section corpse view. The character column stays fixed while the
   middle Corpse column opens.
6. Drag two different corpse items into valid empty carried slots at nearly the
   same time. Expected result: both commits appear in both views with no lost or
   duplicated item. Then drag the same remaining item simultaneously. Exactly
   one client commits, and the loser receives a stable error followed by current
   authoritative state.
7. For a stack, click `Partial stack: off` so it reads `Partial stack: on`,
   enter a quantity, and drag it to a valid
   destination from one client while the other client loots from the same stack.
   Expected result: committed quantities never become negative and their total
   is conserved.
8. Drag a carried item into an empty corpse slot. Then drag two complete items
   that cannot merge onto each other in both directions. Expected result: the
   deposit appears for both viewers, each valid occupied drop swaps atomically,
   and the character column refreshes without closing the corpse. Repeat with a
   partial compatible stack deposit and verify total quantity is conserved.
9. Drag a corpse item to another empty corpse slot, split one corpse stack into
   another corpse slot, merge compatible corpse stacks, and drop two complete
   non-mergeable corpse items onto each other. Expected result: every operation
   commits as corpse-only movement, both clients receive the result, and the
   character weight and item-state revision do not change. Verify that each
   Corpse Equipment slot shows its type, such as `Head` or `Body`. A compatible
   item is accepted and an incompatible item is rejected without moving either
   item.
10. In one client, drag a corpse Bag child. At the same time in the other client,
   drag the corpse Bag root onto the occupied player Bag equipment slot.
   Expected result: only a compatible complete outcome commits. Neither Bag
   aggregate is split, and both clients converge after the broadcast delta or
   refresh.
11. Create a fresh fixture and restart SimulationWorker before its five-minute
   deadline. Rejoin the Shard. Expected result: the corpse returns at
   `(0, 0, -1)` with the original absolute expiry. It disappears and closes any
   open view at expiry rather than receiving a new five-minute lifetime.

Combat is not required for this test. The fixture enters the same durable
death, restore, interaction, transaction, and expiry boundaries that combat
calls.

## World Actor And Interaction Verification

Compile or verify the canonical actor content from any repository directory:

```powershell
dotnet run --project tools/WorldActorCompiler --configuration Release
dotnet run --project tools/WorldActorCompiler --configuration Release --no-build -- --verify
```

Expected result: each command reports World `development-world-1` with five
definitions and six runtime instances, and `development-world-2` with five
definitions and twelve runtime instances. The compiler reads
`content/actors/catalog.json`, one file per actor below
`content/actors/definitions`, and every `content/worlds/<WorldId>/actor-spawns.json`,
and writes `src/Shared/WorldData/Generated/Worlds/<WorldId>/world-actors.json`.
It checks Mob loot, vendor profile ids, and resource profile tool kinds and
yields against the compiled item catalog, so run the item catalog compiler first
after an item change.
Verify mode performs no write and fails if authoring, runtime JSON, references,
bounds, transforms, fingerprints, ordering, or the complete revision diverge, if
a World directory lacks `world.json`, `actor-spawns.json`, or `collision.json`,
or if generated actor data exists for a World that no longer exists.

Run the focused backend actor and protocol coverage, then the complete Unity
workflow:

```powershell
dotnet test Mmorpg.slnx `
  --configuration Release `
  --filter "FullyQualifiedName~WorldActor|FullyQualifiedName~RealtimeWorldActor"
powershell -ExecutionPolicy Bypass -File tools/Run-UnityTests.ps1
```

Expected result: every selected backend test and every Unity EditMode and
PlayMode test passes. The coverage includes deterministic compilation, strict
runtime validation, identity scopes, assignment reconstruction, reliable
interest presence, central Mob scheduling, composable capabilities, typed
deferred dispatch, range, line of sight, rate, revision, session and shared
corpse-lease authority, protocol bounds, monotonic Unity state, targeting, and
Editor compiler integration.

### Manual Actor Authoring And Interaction Loop

Existing canonical content requires no scene, prefab, Inspector, package, or
build-setting change. The committed presentation registry already maps every
authored archetype to a placeholder prefab. A new archetype uses the tinted
fallback, with one warning, until a prefab is added to
`Assets/Content/WorldActors/WorldActorPresentationRegistry.asset`.
Use these exact Unity Editor steps to exercise the authoring tools and
presentation mapping:

1. Open the Unity project and select `MMORPG > Tools > Content > Actor
   Studio`. Select `Mira the Quartermaster`. Expected result: the window shows
   an NPC with dialogue, vendor, quest offer, quest turn-in, crafting,
   insurance, bank, and Recovery Storage capabilities, a note that NPCs are
   always invulnerable instead of Mob settings, authoritative interaction
   bounds with a front-view preview, and `No problems found.` below the editor.
   The vendor capability `Trade` shows `Profile` `vendor.city_general_store`.
   Select `Iron Vein`. Expected result: a `Resource Node` with one `Gathering`
   capability, `Mine`, whose `Profile` is `resource.iron_vein`.
2. Click `Duplicate`. Expected result: the copy `npc.city_services.copy` is
   added and selected. Use `Add Capability`, `Move Up`, `Move Down`, and
   `Remove` on it, then enter `-1` as the Y value of `Size`. Expected result:
   `Size` turns red, the list marks the copy, and the problems panel reports
   `interactionBounds.sizeY`. Clicking the problem selects the copy and
   focuses the field.
3. Click the actor list so no text field has keyboard focus and press `Ctrl+Z`
   repeatedly. Expected result: every edit is undone in order, the copy
   disappears last, and `Ctrl+Y` redoes them. To start again from the files on
   disk, click `Reload` and confirm the discard.
4. Select `Feral Wolf`, then Ctrl-click `Feral Alpha`. Expected result: one
   editor edits both, `Corpse Persistence` shows a mixed value because the wolf
   is `Live` and the alpha is `Durable`, and the shared values show normally.
   Change `Disposition` to `Neutral`. Expected result: both Mobs change in one
   undo step. Add `City Guard` to the selection. Expected result: Mob-only
   settings still show and edit only the two Mobs. Undo with `Ctrl+Z`.
5. Select `City Guard`, change `Stable Actor Id`, and press Enter. Expected
   result: the id stays and the status names `development-world-2`, because
   that World also places the guard. An actor that only the edited World places
   is renamed together with its spawns in one undo step. Click `Delete`.
   Expected result: nothing is deleted and the status lists the spawns that
   place the guard.
6. To use authored prefabs, click `Create Presentation Registry` when it
   appears, then assign a prefab in `Replaceable Prefab` for each presentation
   archetype. The prefab must be stored below `Assets/Prefabs/WorldActors`, an
   Addressables folder entry; the field rejects any other prefab. Expected
   result: Unity creates
   `Assets/Content/WorldActors/WorldActorPresentationRegistry.asset` and stores
   each prefab as an Addressables reference. The prefab changes presentation
   only. This optional asset creation and prefab assignment are the only manual
   Unity asset steps.
7. With no pending edits, click `Preview Changes`, then `Save + Compile`, then
   `Verify Canonical`. Expected result: the preview window lists every
   canonical file, marks changed files in bold, and reports `No canonical
   changes detected.`. Saving writes the actor catalog, one file per actor, the
   spawn file, and the runtime JSON of every World deterministically in one
   transaction, and verification reports the same revisions. Removing an actor
   deletes its definition file. Without an intentional content change, the
   files stay byte-for-byte equivalent. Closing the window with unsaved edits
   asks whether to save them.
8. Open a Scene view and `MMORPG > Tools > Content > Spawn Authoring`.
   Expected result: the list shows the points `local.city_services` and
   `local.iron_vein`, the group `local.city_guards`, the area
   `local.feral_wolves`, and the patrol path `local.wolf_patrol` of
   `development-world-1`, the Scene view draws all five while `Scene Preview` is
   on, and the problems panel reports `No problems found.`. Selecting a point
   shows its prefab under `Presentation Preview`.
9. Select the group, Ctrl-click the area, and click `Frame`. Expected result:
   the Scene view frames both, one move handle sits between them, dragging it
   moves both, and one `Ctrl+Z` undoes the whole drag. The `Area` and
   `Mob Lifecycle` sections appear because the area places the Feral Wolf.
   Enter `0` in `Count`. Expected result: `Count` turns red, the list marks the
   area, and the problems panel reports the allowed range. Undo with `Ctrl+Z`.
10. Click `New Point`. Expected result: `local.new_point` appears at the Scene
    view pivot and is selected. Click the marker of another entry in the Scene
    view. Expected result: that entry is selected, and Ctrl-click adds to the
    selection. Select `local.new_point` again, click `Ground Snap`, then
    `Preview Changes`. Expected result: the point moves onto the scene collider
    below it, or the status reports that no collider was found, and the preview
    shows the exact neutral JSON of the spawn file.
11. Change the `Stable Id` of `local.wolf_patrol`. Expected result: the area's
    `Patrol Path` follows the new id in the same undo step. Select the patrol
    path together with `local.new_point` and click `Delete`. Expected result:
    the point is deleted and the patrol path stays, because the area follows
    it. Click `Reload`, confirm the discard, then click `Save + Compile` and
    `Verify Canonical`. Expected result: without edits, the files stay
    byte-for-byte equivalent and verification reports the same revisions.
12. Start GameService and SimulationWorker, join `local-shard-1`, and approach
    Mira. Expected result: actor instances appear and disappear through normal
    interest presence. For the default `development-world-2`, the worker
    startup log reports `Activated 12 world actors from revision <revision>`.
13. Left-click Mira. Expected result: the player walks to about `2.75` metres
    from her, and temporary uGUI opens only after the server accepts and lists
    the player-specific authoritative capability summary. Clicking a
    capability that is not implemented yet, such as `Talk` or `Crafting`,
    reports `world_interaction_capability_deferred`, never local success.
14. Click Mira while a wall hides her from the camera. Expected result: the
    click walks to the ground behind the pointer instead and opens nothing.
    Open within range, click the ground beyond `3.5` metres, and verify the
    server closes only that session. Pressing `Escape` also closes an active
    session.
15. Join with two clients and open Mira independently. Expected result: both
    sessions coexist. One client cannot open a corpse while its NPC interaction
    is active. After closing it with `Escape`, click a corpse and verify the existing
    corpse inspection, drag-and-drop, and authoritative mutation flow is
    unchanged.
16. Restart SimulationWorker and reconnect. Expected result: actors reconstruct
    from compiled WorldData with fresh runtime identities, normal interest
    presence returns, and no stale actor or interaction session survives.

Ordinary NPCs remain event-driven. The Feral Wolf changes only between central
dormant and active scheduling buckets based on player distance. No actor creates
a durable instance row, timer, task, database session, or HTTP poller.

Run the deterministic realtime scalability workload separately when changing
interest selection, snapshot encoding, or quota code:

```powershell
dotnet test tests/Server/Mmorpg.SimulationWorker.Tests `
  --configuration Release `
  --filter "Category=Load"
```

Expected result: 25,000 entities and 1,000 observers complete spatial selection
and snapshot encode/decode inside the five-second regression budget.

Run the external headless stack stress flow before accepting a new capacity
baseline or after changing the realtime loop, collision, interest, snapshot,
session-heartbeat, account, placement, or database behavior. Worker-only mode
uses in-memory identities to isolate SimulationWorker. Full-stack mode drives
normal GameService APIs against a guarded disposable PostgreSQL database.
Complete commands, expected results, measurements, safety checks, and cleanup
are documented in [Stack Stress Testing](STACK_STRESS_TESTING.md).

Use `tools/ActiveSimulationBots` when a long-running synthetic population must
share the normal GameService, SimulationWorker, and shard with a Unity player.
Its account-login bypass is Development-only and in memory, while every bot
still uses an exact-runtime one-time ticket and the complete UDP join, input,
snapshot, and leave flow. Configuration, startup commands, expected behavior,
capacity reservation, Ctrl+C cleanup, and the visual Unity test are documented
in [Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md).

## NPC Insurance And Quest Item Verification

The NPC lifecycle uses Mira the Quartermaster and the shared world interaction
session. No scene object, inspector assignment, or new Unity asset is required.
The default GameService lifecycle profile charges `100` currency for one-death
insurance and grants one `quest_item.sealed_letter` with lineage
`quest.local.sealed_letter`.

Prepare a local character while it is offline:

1. Open `MMORPG > Tools > Inventory Item Grants` and grant
   `weapon.training_shortbow` into a free Permanent Inventory slot.
2. A new character starts with `100` Gold, enough for one policy. For more, enter
   `500` in `Amount` under `Give Currency` in the same window and click
   `Give Currency`, or run the `--dev-items-currency <characterId> 500` command
   from [Give A Character Development Items](#give-a-character-development-items).
   Expected result: the status reports
   `Granted 500 currency to <character>, who now has <balance>.`, and
   `Balance` shows the new value.

Start GameService and SimulationWorker, join `local-shard-1`, approach Mira
and left-click her. The player walks into range before the interaction opens.

Expected result:

- The existing authoritative interaction opens and lists Insurance plus Quest
  item lifecycle actions.
- Applying insurance refreshes the same item with `Insured | source: Insurance
  NPC`. The server charges the configured price atomically, with an `insurance`
  row in `character_currency_ledger`, and rejects a second active policy.
- Explicit removal keeps the item instance and removes the insured label.
- Accepting the quest creates one protected Sealed Letter with source
  `Quest grant`. Repeated acceptance does not create a duplicate.
- Generic destroy rejects the protected quest item. NPC abandonment removes only
  that grant lineage, and reacceptance creates exactly one new required item.
- Moving beyond `3.5` metres or losing line of sight still closes the interaction
  through the shared interaction authority path.

Recovery Storage headings label `death_recovered_insured` as consumed insurance
and `death_recovered_protected` as protected-on-death. A combat death in
[Combat Verification](#combat-verification) consumes insurance the same way;
the backend death fixture verifies first-death consumption and replay
idempotency without a second client.

No manual Unity Editor setup is required for this verification. Editor actions are needed
only for the optional offline item grant used by this manual workflow.

## Vendor And Gathering Verification

Mira the Quartermaster offers the `Trade` vendor capability with the vendor
profile `vendor.city_general_store`, shown as `General Store`. It sells Bronze
Arrow for `1`, Minor Healing Potion for `25`, Cooked Trout for `10`, and Starter
Pickaxe for `50` Gold with unlimited stock. It buys every carried item with a
value except quest items for 40 percent of the value, rounded down on the total,
and has no buyback. An Iron Vein is a shared resource node: with a pickaxe in the
Tool slot it yields one Iron Ore every 3 seconds, it holds 5 ore, and a depleted
vein returns after 30 seconds. The content places them here:

- Development World 2, which `local-shard-1` uses by default: Mira at
  `(8, 0, 4)` on the central service plaza, and four Iron Veins 6 metres apart
  around `(96, 0, -124)` on the southeast expansion pad.
- Development World 1: Mira at `(2.5, 0, -1)` and one Iron Vein at `(-8, 0, 1)`,
  west of the spawn.

No manual Unity Editor setup steps are required. The placeholder prefab
`Assets/Prefabs/WorldActors/Resource_IronVein.prefab`, its entry for
`resource.iron_vein` in the world actor presentation registry, and the baked
presentation catalog are committed.

Start the local stack:

```powershell
docker compose up -d --wait
dotnet run --project src/Server/GameService
```

In a second terminal:

```powershell
dotnet run --project src/Server/SimulationWorker
```

GameService applies pending migrations when it starts. On an existing local
development database it logs these two migrations once, and the catalog mirror
receives the item values, tool kinds, and vendor:

```text
Applied database migration 202610181240_character_currency_ledger.
Applied database migration 202610181250_item_economy.
```

The local database needs no reset: values, tool kinds, and vendors never change
a structural fingerprint. Existing characters keep their balance, and the
migration backfills an `opening_balance` ledger row for every nonzero balance.

1. Open LoginMenu, enter Play Mode, log in, create a new character, and join
   `local-shard-1`. Press `B`. Expected result: the character header shows
   `100 Gold` before the item-state revision. An existing character keeps its
   balance. To add Gold, leave the shard and use `Give Currency` as described in
   [Give A Character Development Items](#give-a-character-development-items).
2. Left-click Mira and choose `Trade`. Expected result: the interaction reports
   `Trade is open while this interaction stays open.`, and the inventory opens in
   the complete view on the `Vendor` tab with `General Store` and
   `You have 100 Gold`. It lists the four offers with their unit price, such as
   `50 Gold each` for the Starter Pickaxe. Each offer has a quantity field,
   default `1`, and a button that names the total while you type, such as
   `Buy for 50 Gold`.
3. Click `Buy for 50 Gold` next to the Starter Pickaxe. Expected result: the
   vendor panel reports `Purchased.`, the pickaxe appears in carried storage,
   and the balance shows `50 Gold`.
4. Before equipping the pickaxe, left-click an Iron Vein. Expected result: the
   player walks into range and the interaction opens, but the panel reports
   `Equip a gathering tool, such as a pickaxe, to gather here.` and nothing is
   mined.
5. Press `C`, drag the pickaxe onto the `Tool` equipment slot, and click `Mine`
   in the interaction panel. Expected result: mining starts. The panel shows
   `Mine...` with a progress bar that fills every 3 seconds and a
   `Gathered <n> Iron Ore.` count. Every ore appears in carried storage after
   the committed refresh, and the carry weight rises. From now on, clicking an
   Iron Vein starts mining as soon as its interaction opens.
6. Keep mining. Expected result: after the fifth ore the vein switches to its
   depleted model for every client, mining stops, and the panel reports
   `The resource node is depleted.`. The depleted vein cannot be targeted. About
   30 seconds later it returns and can be mined again. With a second client
   joined, both clients see the same vein deplete, and a second player mining
   it stops at the same moment.
7. Start mining a vein that has ore left, then click the ground. Expected
   result: mining stops as soon as the character moves and no further ore
   arrives. The panel reports `Moving stopped gathering.` until the
   interaction closes.
8. Return to Mira, choose `Trade`, and drag the Iron Ore stack onto the `Sell`
   slot. Expected result: a quantity field shows the whole stack, and with 5 ore
   the button reads `Sell 5 Iron Ore for 20 Gold`. Enter a smaller quantity to
   sell part of the stack, or click `Cancel` to discard the sale. Click the sell
   button. Expected result: the vendor panel reports `Sold.`, the stack shrinks
   by the sold quantity, and the balance rises by 4 Gold per ore, rounded down
   on the total.
9. Currency stays with the character on death. A combat death needs a second
   client, so use the development death fixture: leave the shard, open
   `MMORPG > Tools > Inventory Item Grants`, select the character, note
   `Balance` under `Give Currency`, and click `Create Durable Corpse` as
   described in
   [Give A Character Development Items](#give-a-character-development-items).
   Expected result: the corpse is created through the durable death pipeline,
   and `Balance` is unchanged.

Inspect the newest currency ledger rows without exposing database credentials:

```powershell
docker compose exec postgres sh -lc 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "select character_id, source_kind, delta, balance_after from character_currency_ledger order by id desc limit 10;"'
```

Expected result: the test character has a `starting_balance` row of `100`, a
`vendor_purchase` row of `-50`, and one `vendor_sale` row per sale, and each
`balance_after` equals the previous balance plus `delta`.

Run the focused automated coverage:

```powershell
dotnet test Mmorpg.slnx `
  --configuration Release `
  --filter "FullyQualifiedName~Vendor|FullyQualifiedName~ResourceNode|FullyQualifiedName~Gathering|FullyQualifiedName~CurrencyCommand"
```

Expected result: every selected test passes. The shared tests cover catalog
format 5 and actor format 3 content, vendor and resource profiles, the rule that
buying and selling back never gains currency, and pricing rounding and
overflow. The SimulationWorker tests cover vendor access, resource node charges,
gathering cycles, movement and closed-interaction stops, rejected and uncertain
yields, depletion, and respawn. With the isolated PostgreSQL test variable from
[Isolated PostgreSQL Integration Tests](#isolated-postgresql-integration-tests)
set, the GameService and integration tests also run the wallet, ledger,
purchase, sale, gathering, and development currency command against a dedicated
test database, including one full-stack flow that buys a pickaxe, mines ore,
sells it, and buys a potion. Otherwise they are skipped.

For the client side, run both Unity suites with the Editor closed:

```powershell
powershell -ExecutionPolicy Bypass -File tools/Run-UnityTests.ps1
```

Expected result: every EditMode and PlayMode test passes, including
`VendorAndGatheringClientTests`, the inventory snapshot currency mapping, and
the Item Catalog vendor mode and Actor Studio resource node coverage. With the
Editor open, use the live Editor test commands in
[Unity Editor Automation With Unity CLI](#unity-editor-automation-with-unity-cli)
instead.

## Mob Corpse Verification

Mob corpses reuse the corpse presence, view, drag, inventory refresh, and
generic `corpse.remains` presentation. It adds no scene object, prefab, inspector
reference, or protocol version. Mob definitions own the persistence choice and
lifetime:

- `mob.feral_wolf`: `live`, `120` seconds.
- `mob.feral_alpha`: `durable`, `600` seconds.

Verify the content manually:

1. Open `MMORPG > Tools > Content > Actor Studio`.
2. Select `mob.feral_wolf`. Confirm `Corpse Persistence` is `Live` and
   `Corpse Lifetime Seconds` is `120`.
3. Select `mob.feral_alpha`. Confirm persistence is `Durable` and lifetime is
   `600`.
4. Select `npc.city_guard` or `npc.city_services`. Confirm NPC content does not
   carry Mob corpse settings.
5. Click `Preview Changes`, `Save + Compile`, and `Verify Canonical`.

Expected result: every action succeeds, `development-world-1` has five actor
definitions and six runtime instances, and its checked-in actor runtime revision
is `69af19babbc642584545811744ceee51c8f9727c592b2249f41fcf7855567767`.

Run the worker-memory lifecycle workflow:

```powershell
dotnet test tests/Server/Mmorpg.SimulationWorker.Tests `
  --configuration Release `
  --no-build `
  --filter "FullyQualifiedName~LiveMobCorpseStoreTests|FullyQualifiedName~MobCorpseLifecycleServiceTests"
```

Expected result: all focused tests pass. A normal wolf corpse uses the authored
120-second lifetime, disappears from a new worker store without database
cleanup, expires from the original store, and replays the same deterministic
grant id.

With the isolated PostgreSQL test variable configured in the next section, run:

```powershell
dotnet test tests/Integration/Mmorpg.Integration.Tests `
  --configuration Release `
  --no-build `
  --filter "FullyQualifiedName~MobCorpseLifecycleIntegrationTests"
```

Expected result: all three integration tests pass. Retrying one live Mob loot grant
creates one persistent player item. A durable alpha corpse restores into a new
worker store with the same database deadline, three-section custody, and loot.

No manual Unity gameplay setup is required. To kill a wolf and loot its
corpse in Unity, follow [Combat Verification](#combat-verification).

## Combat Verification

Protocol 18 adds target-based combat to SimulationWorker: attack targets,
attack timing, arrows and their batches, damage, player and Mob death, Mob loot
and respawn, player respawn, regeneration, and the Shard rule. Unity attacks
with a left click and shows the combat with temporary presentation, and Mobs
detect, chase, bite, and evade home as
[Mob Brains](SERVICE_FEATURES.md#mob-brains) describes. The worker's
socket-level tests cover the authoritative flow:

```powershell
dotnet test tests/Server/Mmorpg.SimulationWorker.Tests `
  --configuration Release `
  --filter "FullyQualifiedName~RealtimeCombatTests|FullyQualifiedName~RealtimeMobAiTests|FullyQualifiedName~MobPathPlanningQueueTests|FullyQualifiedName~CombatRulesTests|FullyQualifiedName~CombatMathTests|FullyQualifiedName~CombatLoadoutTests|FullyQualifiedName~CombatGeometryTests|FullyQualifiedName~MobLootRollerTests"
```

Expected result: every selected test passes. Over real UDP against the scripted
GameService, an archer kills a wolf that leaves a corpse and respawns, attacks
wait for range and report why, attacks on characters follow the Shard rule,
spent arrows commit in batches while the owner sees the live count, a killed
character commits its death once and respawns at the spawn, shots commit before
a leave, after a disconnect, and behind a batch that finished while the worker
stopped, an equipment change holds attacks for the lockout, only the lethal
arrow of a volley hits and takes the kill, a wolf without a corpse despawns
after its death reached the viewer, and a synthetic bot is no target. The Mob
tests show a wolf that detects, chases, and bites a player and comes to rest
next to it, a wolf that kills a player as the recorded killer, a wolf that
evades home past its leash and heals fully, a defensive wolf that fights only
back, and 60 wolves that all reach their target through the planning budget.

Play the combat loop on the local stack:

1. Start the local stack as described in [Backend Services](#backend-services).
   Expected result: SimulationWorker registers its Shard and does not stop with
   `shard_rule_set_unknown`.
2. With the character offline, open `MMORPG > Tools > Inventory Item Grants` and
   grant the `equipment` package, as described in
   [Give A Character Development Items](#give-a-character-development-items).
3. Enter Play Mode, log in, and join `local-shard-1`. Expected result: the
   Console shows `[CLIENT] Server-authoritative movement is active at 30 ticks
   per second with ... snapshots per second. Player versus player is allowed on
   this Shard.`, and the top-left combat frame shows `Health 100 / 100`, `No
   arrows equipped`, and `No target`.
4. Press `C`, equip the Training Shortbow in Main Hand and the Bronze Arrows in
   Ammunition. Expected result: the frame shows `Arrows 500`.
5. Walk to the Feral Wolf area of Development World 2, X and Z between `-190`
   and `-130`, with several clicks. Hover a wolf. Expected result: no `Click to
   interact` prompt, because a Mob is a combat target, never an interaction.
6. Left-click a wolf from more than 10 m away. Expected result: the target frame
   shows `Feral Wolf 50 / 50`, the character walks to about 9 m and stops, the
   status reads `Attacking`, arrows fly, white damage numbers rise from the
   wolf, the wolf gets a health bar, and the arrow count drops by one per shot in
   the frame and in the inventory Ammunition slot.
7. Click the ground or press `Escape` while it fights. Expected result: the
   target frame closes, the status reads `No target`, and the shooting stops.
8. Attack a wolf until it dies. Expected result: the target frame closes, the
   wolf leaves a corpse that loots as before, and a new wolf appears after 30
   seconds. Click the corpse and drag every item into the inventory. Expected
   result: after the last item the corpse disappears, and the Corpse context
   shows `The corpse is empty.` in neutral text, not a red error. A corpse left
   alone for 120 seconds closes with `The corpse expired and is no longer
   available.`, also in neutral text.
9. Unequip the bow and click a wolf. Expected result: the character does not
   walk, and the status reads `No weapon equipped`. Without arrows it reads `No
   ammunition`, and right after swapping the bow it reads `Equipment changed`
   for about 1 second.
10. Walk to within about 8 m of a wolf and stand still. Expected result: the
    wolf runs to the character and bites, the health in the frame drops, and
    the wolf's health bar shows it in combat. Walk away about 35 m. Expected
    result: the wolf turns back, the target frame shows `Returning home`, arrows
    do no damage while it runs, and it arrives at its place with full health.
11. Let the Feral Alpha at `(-120, 0, -195)` or several wolves kill the
    character. Expected result: `You died` with `Respawning in 5 s` counting
    down, clicks do nothing while dead, and after the respawn the character
    stands at the World spawn with full health and does not walk on. Its items
    are in its corpse at the place of death, and the wolves walk home. A second
    client with another character can also be attacked until it dies.
12. With the [Monitoring Stack](#monitoring-stack) running, open the MMORPG
    Playtest dashboard. Expected result: the `Combat` panel shows the attacks,
    hits, deaths, engaged and moving Mobs, and Mob routes, the `Tick phases`
    panel shows the Mob and combat phases, and the `Arrows and deaths` panel
    shows the committed arrow batches.

No manual Unity Editor steps are required. The combat client adds no scene,
prefab, Inspector reference, input action, or asset; `ClientBootstrap` creates
the combat controllers, the targeting, the arrows, and the temporary combat HUD
at runtime. The EditMode and PlayMode suites cover the click rules, the
approach, the HUD values, the live arrow count, and the bootstrap:

```powershell
powershell -ExecutionPolicy Bypass -File tools/Run-UnityTests.ps1
```

With the Editor open, use the live Editor test commands in
[Unity Editor Automation With Unity CLI](#unity-editor-automation-with-unity-cli)
instead.

## Item Operations Hardening Verification

The operations hardening keeps the item, corpse, actor, capability,
interaction, and authority graph. Checked-in GameService defaults live under `Items:Operations`:

- `TransactionTimeoutMilliseconds`: `10000`.
- `LockTimeoutMilliseconds`: `2000`.
- `MaximumCommandPayloadBytes`: `65536`.
- `MaximumHttpRequestBodyBytes`: `262144`.
- `MetricsIntervalSeconds`: `30`.
- `MaintenanceIntervalSeconds`: `60`.
- `CleanupBatchSize`: `64`.
- `AuditRetentionDays`: `30`.
- `ClosedCorpseRetentionDays`: `30`.

Configuration fails at startup when a value is outside its bound, lock timeout
exceeds transaction timeout, or closed-corpse retention is shorter than audit
retention. Use standard ASP.NET Core configuration paths such as
`Items__Operations__LockTimeoutMilliseconds` for a temporary local override.

With the isolated PostgreSQL variable configured in the next section, run the
operational tests:

```powershell
dotnet test tests/Server/Mmorpg.GameService.Items.Tests `
  --configuration Release `
  --no-build `
  --filter "FullyQualifiedName~ItemOperationsHardeningTests|FullyQualifiedName~ItemOperationsHardeningIntegrationTests"
```

Expected result: all focused tests pass. A blocked character mutation returns
`item_transaction_timeout`; an oversized canonical command returns
`item_command_payload_too_large` before database access; expired Recovery items
are destroyed exactly once with `recovery_expired`; and policy-eligible empty
closed corpses plus unreferenced old operation rows are removed without deleting
durable destruction evidence. The worker corpse metrics are covered by
`CorpseRuntimeMetricsTests` in `Mmorpg.SimulationWorker.Tests`.

Run the actor and transaction hotspot scenarios:

```powershell
dotnet test Mmorpg.slnx `
  --configuration Release `
  --no-build `
  --filter "FullyQualifiedName~WorldActorRuntimeTests|FullyQualifiedName~CorpseLootConcurrencyIntegrationTests"
```

Expected result: `2,000` event-driven NPCs and `2,000` centrally scheduled Mobs
transition without actor-owned loops, `128` players hold independent NPC leases,
eight concurrent corpse claims commit once each, and forty repeated Bag swaps
preserve every aggregate and child custody relation.

Run the `100` bot Release worker-only hotspot workflow in
[Stack Stress Testing](STACK_STRESS_TESTING.md). Expected result:
`100/100` bots join and leave cleanly, movement inputs receive acknowledgement,
snapshot gaps remain zero under the accepted baseline, process measurements
refer to the registered worker runtime, and the worker log contains no movement
catch-up-budget warning.

During local service operation, GameService logs a periodic item summary with
transaction, contention, lifecycle, corpse, Recovery, and cleanup counts.
Significant item transactions carry a bounded correlation id and operation id.
SimulationWorker status distinguishes actor tiers and live versus durable corpse
counts. Credentials, session tokens, and command bodies must not appear.

No manual Unity Editor scene, prefab, inspector, package, input, or build-setting
steps are required for this verification. Run `tools/Run-UnityTests.ps1` as regression
coverage and expect all EditMode and PlayMode tests to pass unchanged.

## Isolated PostgreSQL Integration Tests

Every PostgreSQL test gets its own fresh database next to the database named in
`MMORPG_TEST_POSTGRES`, copied from a template that each test run builds once
with the migrations and startup seeds. Always use the isolated test Compose file
and never point the test variable at a development, staging, or production
database. Keep the host `127.0.0.1`, as in `.env.example`: with `localhost`, every
new connection on Windows first waits about two seconds for IPv6 loopback to
fail, which makes the PostgreSQL tests several times slower. If your `.env`
still says `Host=localhost` for `MMORPG_TEST_POSTGRES` or
`ConnectionStrings__Postgres`, change it to `Host=127.0.0.1`.

Start the test database:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
```

Set the dedicated connection and run all backend tests:

```powershell
$testConnectionLine = Get-Content .env |
  Where-Object { $_ -like "MMORPG_TEST_POSTGRES=*" } |
  Select-Object -First 1
$env:MMORPG_TEST_POSTGRES = $testConnectionLine.Split("=", 2)[1]
dotnet test Mmorpg.slnx --configuration Release
```

Expected result:

- All unit tests pass.
- PostgreSQL migration concurrency, ticket concurrency, wrong-worker protection,
  reconnect, heartbeat, cross-shard and cross-character exclusion, assignment
  failover, and idempotent release tests pass
  instead of being skipped.
- The server holds a group of test databases per project with PostgreSQL
  tests, named after `<base>_gameservice`, `<base>_gameservice_accounts`,
  `<base>_gameservice_items`, and `<base>_integration`, where `<base>` is the
  database named in the variable: the run lock database itself, its `_tpl_`
  templates, and numbered test databases such as `<base>_gameservice_items_1`.
  They are created on demand and kept. The test container keeps its data in
  memory, so `down` removes them.
- A second run of the same test project against the same server, started while
  the first still runs, fails at once with a message that names the databases.

Add `--filter "Category=Integration"` to run only the PostgreSQL and Redis
tests, or `--filter "Category!=Integration"` to run everything else without
containers.

Clean up the isolated environment:

```powershell
docker compose -f docker-compose.test.yml down
Remove-Item Env:MMORPG_TEST_POSTGRES
```

## Local Infrastructure

Start PostgreSQL and Redis locally:

```powershell
docker compose up -d
```

The local services use these ports:

- PostgreSQL: `localhost:5432`
- Redis: `127.0.0.1:6379`
- GameService: `http://localhost:5000`
- SimulationWorker realtime transport: `0.0.0.0:27015/udp`
- SimulationWorker advertised client endpoint: `127.0.0.1:27015/udp`

Local PostgreSQL credentials and SimulationWorker service credentials live only in
the ignored `.env` file. `.env.example` documents every required key without
placing active credentials in application settings or Compose YAML.
`SIMULATION_WORKER_ADVERTISED_HOST` and
`SIMULATION_WORKER_ADVERTISED_UDP_PORT` override the client-facing endpoint
without changing the local bind port.

### Reset The Local Development Database

GameService refuses to start when a catalog change would invalidate live item
state, and the error names the affected definition, equipment slot, tier, or
character setting. On a local development database the alternative to an
explicit data migration is a reset. It deletes every local account, character,
item, corpse, and Recovery delivery.

1. Stop GameService and SimulationWorker.
2. From the main repository checkout, not a worktree, remove the PostgreSQL
   volume and start the containers again. Compose names the volume after the
   checkout directory, so a worktree would not reach it. Redis keeps no data.

   ```powershell
   docker compose down -v
   docker compose up -d --wait
   ```

3. Start GameService. Expected result: it applies every migration, seeds the
   current item catalog, and reports ready. Create a new account and character
   in the client.

## Backend Services

Run GameService in the first terminal:

```powershell
dotnet run --project src/Server/GameService
```

Check GameService liveness and readiness:

```powershell
Invoke-RestMethod http://localhost:5000/health/live
Invoke-RestMethod http://localhost:5000/health/ready
```

Expected result: liveness reports `live`. Readiness reports `ready` only after a
real PostgreSQL `select 1` query and Redis `PING` both succeed.

GameService also uses Redis for bounded account-session validation caching and
distributed login and registration rate limits. Checked-in defaults are:

```json
"RedisAcceleration": {
  "Enabled": true,
  "KeyPrefix": "shooter-mmo",
  "SessionCacheEnabled": true,
  "SessionCacheTtlSeconds": 60,
  "SessionRevocationTombstoneSeconds": 120,
  "AuthenticationRateLimitingEnabled": true,
  "MetricsIntervalSeconds": 30
}
```

Set `RedisAcceleration__SessionCacheEnabled=false` before starting GameService
to measure the PostgreSQL validation path without disabling the distributed
authentication rate limit. A Redis read failure falls back to PostgreSQL.
Disconnected Redis operations fail fast and do not build an in-process command
backlog.
Logout, manual revocation, and session replacement instead return HTTP 503 if
the required revocation tombstone cannot be stored, because committing while a
stale cache entry can survive would be unsafe.

Register a test account and keep the returned account session active:

```powershell
$body = @{
  email = "player@example.com"
  username = "player_one"
  password = "TestPass123!"
} | ConvertTo-Json

$auth = Invoke-RestMethod http://localhost:5000/api/accounts/register `
  -Method Post `
  -Body $body `
  -ContentType "application/json"
```

Use the returned session token:

```powershell
$headers = @{ Authorization = "Bearer $($auth.sessionToken)" }
```

The response also contains `sessionId`. Token-bearing responses include
`Cache-Control: no-store` and `Pragma: no-cache`.

Create a character:

```powershell
$characterBody = @{ name = "Hero One" } | ConvertTo-Json

$character = Invoke-RestMethod http://localhost:5000/api/characters `
  -Method Post `
  -Headers $headers `
  -Body $characterBody `
  -ContentType "application/json"
```

Run SimulationWorker in the second terminal:

```powershell
dotnet run --project src/Server/SimulationWorker
```

SimulationWorker is a headless .NET Generic Host. It does not expose HTTP routes.
A successful start logs worker `local-simulation-worker-1`, fleet `local-fleet`,
node `local-node-1`, shard `local-shard-1`, World `development-world-2`, UDP port
`27015`, runtime id, realtime protocol version 17, simulation revision,
collision revision, world-actor revision and population, and loaded collision
chunks. Every 30 seconds it also logs aggregate
realtime packet, byte, entity, peer, quota, and snapshot counters. The same
interval logs a worker status line with connected real players, synthetic bots,
unauthenticated peers, CPU, working set, packet and payload rates, snapshot
drops, and quota rejections. Detailed tick and phase timing remains in the
server-side performance line.

Routine `HttpClient` request-start, request-send, and successful-response logs
for `GameServiceClient` are filtered below `Warning`. This keeps bot heartbeat
and release traffic from flooding the SimulationWorker terminal while HTTP
warnings, failures, simulation lifecycle events, worker status, and performance
logs remain visible.

The default resilience settings allow eight concurrent active-session
heartbeats, 120 inbound packets per second with a 240-packet burst, 128 KiB per
second inbound with a 256 KiB burst, and 256 KiB per second of unreliable
snapshots per peer with a 512 KiB burst. Aggregate unreliable snapshot output is
limited to 38 MiB per second with a 4 MiB burst and fair recipient rotation.
Interest enters at 128 meters and exits at 144 meters. Collision loads within
two chunks of active anchors and unloads outside three chunks. Invalid values
fail startup.

Run a one-time SimulationWorker startup health check while the normal worker is
stopped:

```powershell
dotnet run --project src/Server/SimulationWorker -- --health-check-only
```

Expected result: PostgreSQL is verified through GameService readiness, Redis is
verified directly with `PING`, collision data is loaded and checksum-validated,
UDP port availability is checked, and the process exits with code `0`. Stop
Redis, stop GameService, corrupt a collision chunk, or occupy UDP port 27015 and
repeat to verify a nonzero exit:

```powershell
$LASTEXITCODE
```

Verify that SimulationWorker owns its UDP socket:

```powershell
Get-NetUDPEndpoint -LocalPort 27015
```

Expected result: the command lists IPv4 and optionally IPv6 listeners for port
27015. SimulationWorker health is an executable startup check rather than an HTTP
surface.

Simulation topology and registration test:

1. Start GameService without SimulationWorker and call `GET /api/shards`.
2. Start SimulationWorker and call the endpoint again.
3. Stop SimulationWorker normally with Ctrl+C and call the endpoint again.
4. Start SimulationWorker, then terminate it without graceful shutdown. Wait longer
   than the configured 30-second timeout and call the endpoint again.

Expected result: `local-shard-1` is offline before the first heartbeat, online
while fresh heartbeats arrive, immediately offline after graceful shutdown, and
offline after the heartbeat timeout following an ungraceful stop. The public
response includes World, fleet, region, player count, and capacity, but does not
expose worker host, port, or runtime.

Create a join ticket after SimulationWorker has heartbeated the shard online:

```powershell
$joinBody = @{ characterId = $character.id } | ConvertTo-Json

$join = Invoke-RestMethod http://localhost:5000/api/shards/local-shard-1/join `
  -Method Post `
  -Headers $headers `
  -Body $joinBody `
  -ContentType "application/json"

```

Expected result: `join.shard.id` is `local-shard-1`, `join.shard.worldId` is
`development-world-2`, and `join.endpoint` identifies
`local-simulation-worker-1`, its current runtime, and `127.0.0.1:27015`. The
public shard list did not contain that endpoint.

The ticket is intentionally short-lived and is consumed by the Unity UDP
handshake. Do not attempt to send it to an HTTP SimulationWorker endpoint. The backend
socket tests verify join, leave, the reliable entity lifecycle, protocol
rejections, reconnects, item, corpse, and world interaction flows, replication
order, and shutdown with:

```powershell
dotnet test tests/Server/Mmorpg.SimulationWorker.Tests `
  --filter "FullyQualifiedName~RealtimeSimulationServiceTests|FullyQualifiedName~RealtimeEntityLifecycleTests|FullyQualifiedName~RealtimeProtocolRejectionTests|FullyQualifiedName~RealtimeSessionLifecycleTests|FullyQualifiedName~RealtimeItemOperationTests|FullyQualifiedName~RealtimeCorpseInteractionTests|FullyQualifiedName~RealtimeWorldActorInteractionTests|FullyQualifiedName~RealtimeReplicationContractTests"
```

Expected result: every test passes in about half a minute. The authenticated
client joins, moves, and leaves with exact session cleanup, an existing peer
receives reliable ordered spawn and despawn for another player, and every
protocol rejection arrives with its documented code before the delayed
disconnect. The tests start real UDP servers on free loopback ports and need no
database.

The realtime components also have in-memory tests that need no sockets:

```powershell
dotnet test tests/Server/Mmorpg.SimulationWorker.Tests `
  --filter "FullyQualifiedName~RealtimeMessageDispatcherTests|FullyQualifiedName~RealtimeCompletionProcessorTests|FullyQualifiedName~RealtimeCorpsePeerIdReuseTests|FullyQualifiedName~RealtimeAuthorityQueueTests|FullyQualifiedName~RealtimeItemOperationHandlerTests|FullyQualifiedName~RealtimeWorldInteractionOperationsTests|FullyQualifiedName~RealtimeServiceDisposalTests|FullyQualifiedName~RealtimeMetricNamesTests"
```

Expected result: every test passes within seconds. The dispatcher test compares
every peer state, message type, channel, and delivery method with the original
routing rules. Run `dotnet test Mmorpg.slnx --filter "FullyQualifiedName~Realtime"`
to run every realtime test at once. That filter spans the protocol tests in
`Mmorpg.Shared.Tests` and the worker tests in `Mmorpg.SimulationWorker.Tests`,
so run it against the solution.

Optional logout test after the join flow is complete:

```powershell
Invoke-RestMethod http://localhost:5000/api/accounts/logout `
  -Method Post `
  -Headers $headers
```

Expected result: the endpoint returns `204 No Content`. The bearer token then
returns `401 Unauthorized`, pending tickets are consumed, and active simulation
sessions owned by that account session are released.

An authenticated session can similarly revoke an owned session with
`DELETE /api/accounts/sessions/{sessionId}`.

## Monitoring Stack

The monitoring stack shows live metrics, logs, traces, dashboards, and alerts of
GameService and SimulationWorker in Grafana. What it measures and how to read it
is in [Monitoring](MONITORING.md).

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

Then start GameService and SimulationWorker as described in
[Backend Services](#backend-services).

Expected result: both processes log `Exporting metrics, traces, and logs over
OTLP to http://127.0.0.1:4317/.` at startup. Without the setting they log
`Telemetry export is disabled.` and nothing leaves the process.

Manual check:

1. Open `http://127.0.0.1:3000`. The home page is the **MMORPG Playtest**
   dashboard. Within about 20 seconds `Shards online` shows `1` and the tick
   panels show values far below the dashed 33.3 ms budget line.
2. Start population, for example with
   [Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md) or the Unity client. The
   `Players` or `Bots` stat, bandwidth, and packets rise within one refresh.
3. Register an account as in [Backend Services](#backend-services) and copy the
   `X-Correlation-ID` response header. In Grafana Explore, choose Loki and run
   `{service_name="game-service"} | CorrelationId="<header value>"`. The account
   log entry appears; open it and follow **View trace** to the request with its
   PostgreSQL commands in Tempo.
4. Stop SimulationWorker. Within about three minutes `Shards online` drops to
   `0`, and `ShardOffline` and then `SimulationWorkerDown` appear in the
   **Firing alerts** table and under Alerting in Grafana. Start the worker again
   and both alerts resolve within about a minute.

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

Dashboards are provisioned read-only from `observability/grafana/dashboards`.
Edit the JSON there and run
`docker compose -f docker-compose.observability.yml restart grafana` to load a
change at once.

## Unity Client Flow

The Unity project includes temporary runtime UI for the current backend flow.
Only this UI presentation is intentionally temporary. The runtime bootstrap,
session handling, API layer, scene lifecycle, input, camera, and gameplay systems
are maintained as long-term foundations. The UI is created automatically when
each scene starts. The local player and test map must be authored as Unity assets
and are not generated at runtime.

## Unity Client Assemblies And Addressables

The client code is split into nine area assemblies, and all runtime content
loads through Addressables. The structure, the dependency rules, and the
Addressables groups are described in
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#assemblies). No manual
Unity Editor step is required after pulling this structure: the Addressables
settings in `src/Client/Assets/AddressableAssetsData` are committed, and Play
Mode uses the "Use Asset Database" play mode script, which needs no content
build.

Working rules:

- New client content goes into a folder below `src/Client/Assets/Content`, or a
  World actor prefab below `src/Client/Assets/Prefabs/WorldActors`. Those folders
  are Addressables folder entries, so a new file is addressable at its path below
  `Assets/Content` without any settings change.
- A new World scene is mapped in `Assets/Content/Worlds/world-scene-catalog.json`
  and registered with `MMORPG > Tools > Addressables > Apply Client Layout`.
  Expected result: the Console reports `[ADDRESSABLES] The client layout is
  applied and valid.`, the scene is an entry in the `World Scenes` group with
  its scene name as address, and Build Profiles still list only LoginMenu and
  CharacterSelect.
- A normal player build builds the Addressables content first. To build the
  content alone, open `Window > Asset Management > Addressables > Groups` and
  select `Build > New Build > Default Build Script`. The first build in a new
  Library asks whether to turn on `Debug Build Layout`; either answer is fine.
  The build output and its content state stay in the ignored `Library` folder.
- To exercise built bundles in the Editor, select `Play Mode Script > Use
  Existing Build` in the Groups window after a content build, and switch back to
  `Use Asset Database` afterwards. The choice is stored per user in `Library`.

Manual test of the assemblies and Addressables:

1. Open `src/Client` in Unity 6000.5.4f1 and wait for the import. Expected
   result: the Console shows no compiler error, and the Project window shows
   one asmdef in each folder below `Assets/Scripts`.
2. Open `Window > Asset Management > Addressables > Groups`. Expected result:
   the groups `Client Content`, `Item Icons`, `World Actor Prefabs`, `World
   Collision`, and `World Scenes` exist, and `World Scenes` holds
   `DevelopmentWorld1` and `DevelopmentWorld2`.
3. Open `File > Build Profiles`. Expected result: the scene list holds only
   `LoginMenu` and `CharacterSelect`.
4. Run all EditMode and PlayMode tests in the Test Runner. Expected result: all
   pass, including `ClientAssemblyArchitectureTests` and
   `ClientAddressablesTests`.
5. Start PostgreSQL, Redis, GameService, and SimulationWorker as described in
   [Backend Services](#backend-services), open LoginMenu, enter Play Mode,
   register, create a character, and join `Local Shard 1`. Expected result:
   CharacterSelect shows `Loading DevelopmentWorld2.` briefly, the World scene
   opens with the local player and the authored NPC prefabs, the Console has no
   warning or error, and `Leave Shard` in the F2 panel returns to
   CharacterSelect.

## Unity Editor Automation With Unity CLI

The Unity project includes the Editor automation package `com.unity.pipeline`,
pinned to the experimental version `0.8.0-exp.1`. Together with the Unity CLI it
lets local tools and coding agents such as Claude Code drive a running Editor:
read and change scenes, GameObjects, components, and assets, read the console
and compiler state, evaluate C# in the Editor, enter Play Mode, and run tests.
It replaces the deprecated MCP server in the Unity AI Assistant package and does
not require a Unity AI subscription.

Security rules:

- The Editor server binds only to `127.0.0.1`. Every request needs the bearer
  token from a per-user descriptor file in the ignored `Library/` folder, and
  requests with an `Origin` header are refused, so browser pages cannot reach it.
- `eval` runs arbitrary C# with full Editor access, and some Editor changes
  cannot be undone. Commit or stash work before an agent makes larger Editor
  changes.
- The package runtime assembly compiles only in the Editor and in builds that
  define `ENABLE_PROFILER` (development builds) or `ENABLE_RUNTIME_PIPELINE`.
  Its Player server stays off because the Pipeline Runtime setting
  `enableInBuilds` is `false`. Do not enable `enableInBuilds`,
  `Allow Browser Clients`, or `ENABLE_RUNTIME_PIPELINE` without a reviewed
  reason.

One-time setup on a new machine:

1. Install the Unity CLI with `winget install Unity.CLI` and open a new
   terminal. Unity Hub may already have installed it in
   `%LOCALAPPDATA%\Unity\bin`.
2. Sign in with `unity auth login`.
3. Open the project with `unity open src/Client`, or through
   Unity Hub, and wait for package import and script compilation to finish. If
   the Editor was already open when the manifest changed, focus the Editor window
   so Package Manager resolves the change. The Unity project moved from
   `shooter-mmorpg-unity-client` to `src/Client` in October 2026. If Unity Hub
   still lists the old folder, remove that entry with `Remove from list` and add
   the new folder with `Add > Add project from disk`, or run
   `unity projects add src/Client` from the repository root.

No other manual Editor work is required. The package is already in the
manifest, so `unity pipeline install` is only needed for a project that does not
have it yet.

Verify the connection from `src/Client`:

```powershell
unity status
unity pipeline list
unity command editor_status
```

Expected result:

- `unity status` lists the project as `ready` on a loopback port such as `7800`.
- `unity pipeline list` reports Pipeline `true`, version `0.8.0-exp.1`, and
  Server Reachable `true`.
- `editor_status` returns `"status":"ready"`.

Useful commands, run from `src/Client` or with
`--project-path`:

- `unity list` lists every command the connected Editor exposes.
- `unity command console_status` reports compiler failure state and console
  counts. `unity command console --level warning` returns the entries.
- `unity command run_tests --mode editor --async_tests` starts the EditMode
  suite. Poll `unity command test_status` until `status` is `completed`; the
  result lists every test. A synchronous run is limited to the Pipeline's 30
  second request time and fails once the suite takes longer.
- `unity command run_tests --mode playmode --async_tests` starts the PlayMode
  suite. Entering Play Mode reloads the Editor domain and drops a synchronous
  request, so poll `unity command test_status` until `status` is `completed`.

Coding agents:

- Unity's official Claude Code plugin, `unity@unity-agent-plugin`, adds Unity
  skills that call the CLI. In the Claude desktop app, open `Plugins > Browse`
  and search for Unity. In a Claude Code terminal, run
  `/plugin marketplace add Unity-Technologies/unity-agent-plugin` and then
  `/plugin install unity@unity-agent-plugin`.
- Agents that can run shell commands should call `unity` directly. It is faster
  and uses fewer tokens than MCP.
- MCP clients that cannot run shell commands can use the CLI MCP server through
  `unity mcp configure <client>`. `unity mcp configure --list` shows supported
  clients. This repository does not configure an MCP client.

Package updates follow the normal Unity package rule in
[Repository Quality Checks](#repository-quality-checks). Because the package is
experimental, review its changelog, update it in a focused branch with
`unity pipeline install --package-version <version>`, let Unity rewrite
`packages-lock.json`, and run both Unity suites and the backend quality gate.

## Unity CI Runner

The `Unity quality gate` job in `.github/workflows/ci.yml` runs both Unity
suites through `tools/Run-UnityTests.ps1` on a self-hosted Windows runner.
GitHub-hosted runners cannot activate a Unity Personal license, so the runner
runs on the development machine under the developer's own Windows account. It
reuses the Unity installation, the Unity CLI sign-in, and the Personal license
that a local headless run uses, so the Unity gate runs only while that account
is signed in. A locked screen is fine. A queued job waits until the runner is
online, and GitHub cancels it after 24 hours.

The scheduled task `GitHub Actions Unity runner`, registered by
`tools/Register-UnityRunnerTask.ps1`, keeps the runner running:

- It starts `C:\actions-runner\run.cmd` at sign-in, after the machine wakes from
  sleep, and every five minutes when the runner is not running. A runner that
  exits for any reason, such as a crash, a killed process, or a network failure
  that ends the listener, comes back on its own within a few minutes.
- Only one runner runs at a time. It has no window that could be closed by
  mistake or paused by a click, and it runs at normal priority instead of the
  Task Scheduler default below normal.
- The Task Scheduler service starts it, so it does not belong to any terminal
  or app. A runner started from a terminal of an app, for example a Claude Code
  session, stops when that app closes or updates. On 2026-10-16 an update of
  the Claude desktop app stopped the runner that way, and the Unity gate waited
  overnight. Never start `run.cmd` by hand; use the task.

The job runs for pull requests, pushes to `development` and `main`, and manual
runs from `Actions > CI > Run workflow`. Pull requests from forks skip it. While
the repository variable `UNITY_CI_ENABLED` is not `true`, the job is shown as
skipped.

The runner checks the repository out into its own work folder,
`C:\actions-runner\_work\shooter-mmo\shooter-mmo`. That is a separate Unity
project with its own `Library`, so a job can run while an Editor has the main
checkout or a worktree open. Every job starts from a clean checkout and imports
the project again; both suites together take about four minutes.

Security rules, because the repository is public:

- The runner's job-started hook, `tools/Assert-TrustedRunnerJob.ps1`, fails
  every job before its first step unless it is a push, a manual run, or a pull
  request from a branch of `DrottPoker/shooter-mmo`. A condition in the workflow
  cannot protect the runner on its own, because a pull request from a fork runs
  its own copy of the workflow file. Never widen the hook to other events such
  as `pull_request_target`.
- `Settings > Actions > General` requires approval for all external
  contributors, so no workflow of a fork pull request runs before a maintainer
  approves it. Read the workflow changes of such a pull request before
  approving it.
- Jobs run with the rights of the Windows account that runs the runner. Only
  code from branches of this repository reaches it.
- Keep automatic runner updates on. GitHub stops sending jobs to a runner that
  falls more than 30 days behind the latest runner release.

The ruleset `development CI gate`, stored in
`.github/rulesets/development-ci-gate.json`, requires the jobs `Backend checks
and unit tests`, `Integration tests (items)`, `Integration tests (other)`,
`Observability configuration`, and `Unity quality gate` to pass before a commit
reaches `development`. A skipped job counts as passed. When a merge cannot wait
for the runner, a repository admin can set the ruleset to `Disabled` under
`Settings > Rules > Rulesets` and back to `Active` afterwards. When a job name
changes, update the JSON file and the live ruleset together:

```powershell
$rulesetId = ((gh api repos/DrottPoker/shooter-mmo/rulesets | Out-String | ConvertFrom-Json) | Where-Object { $_.name -eq "development CI gate" }).id
gh api -X PUT "repos/DrottPoker/shooter-mmo/rulesets/$rulesetId" --input .github/rulesets/development-ci-gate.json
```

### Set Up The Runner

Requirements on the machine:

- Unity `6000.5.4f1` in the default Unity Hub path, and the Unity CLI signed in:
  `unity license status` reports `License: active` and `Signed in: yes`.
- Git for Windows and the GitHub CLI, signed in with admin access to the
  repository.
- An execution policy that runs local scripts: `Get-ExecutionPolicy` reports
  `RemoteSigned` or `Unrestricted`. Windows PowerShell 5.1 is enough; the job
  does not need PowerShell 7.

Run these commands once from the repository root in a normal PowerShell window,
not as administrator. Replace the version with the latest release from
`https://github.com/actions/runner/releases`.

```powershell
# Download the runner and check it against the checksum in its release notes.
$version = "2.338.0"
$zip = "C:\actions-runner\actions-runner-win-x64-$version.zip"
New-Item -ItemType Directory -Force C:\actions-runner, C:\actions-runner-hooks | Out-Null
gh release download "v$version" --repo actions/runner --pattern "actions-runner-win-x64-$version.zip" --dir C:\actions-runner --clobber
$notes = gh release view "v$version" --repo actions/runner --json body --jq .body
$expected = [regex]::Match($notes, '<!-- BEGIN SHA win-x64 -->([0-9a-f]{64})<!-- END SHA win-x64 -->').Groups[1].Value
if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $expected) { throw "The runner download does not match its checksum." }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($zip, "C:\actions-runner")
Remove-Item $zip

# Register the runner. The registration token is valid for one hour and is never printed.
$token = gh api -X POST repos/DrottPoker/shooter-mmo/actions/runners/registration-token --jq .token
& C:\actions-runner\config.cmd --unattended --replace --url https://github.com/DrottPoker/shooter-mmo --token $token --name "$env:COMPUTERNAME-unity" --labels unity-6000.5.4f1 --work _work
Remove-Variable token

# Install the job-started hook outside the runner folder.
Copy-Item tools\Assert-TrustedRunnerJob.ps1 C:\actions-runner-hooks\ -Force
Set-Content C:\actions-runner\.env "ACTIONS_RUNNER_HOOK_JOB_STARTED=C:\actions-runner-hooks\Assert-TrustedRunnerJob.ps1" -Encoding Ascii

# Register the scheduled task that keeps the runner running, and start it.
& .\tools\Register-UnityRunnerTask.ps1

# Enable the job, require approval for fork workflows, and require green CI for development.
gh variable set UNITY_CI_ENABLED --body true --repo DrottPoker/shooter-mmo
gh api -X PUT repos/DrottPoker/shooter-mmo/actions/permissions/fork-pr-contributor-approval -f approval_policy=all_external_contributors
gh api -X POST repos/DrottPoker/shooter-mmo/rulesets --input .github/rulesets/development-ci-gate.json
```

Verify the runner:

```powershell
Get-ScheduledTask -TaskName "GitHub Actions Unity runner"
gh api repos/DrottPoker/shooter-mmo/actions/runners --jq '.runners[] | {id, name, status, labels: [.labels[].name]}'
gh workflow run ci.yml --repo DrottPoker/shooter-mmo --ref development
```

Expected result:

- The task state is `Running`.
- The runner is listed with name `<COMPUTERNAME>-unity`, status `online`, and
  labels `self-hosted`, `Windows`, `X64`, and `unity-6000.5.4f1`.
- The manual CI run passes the `Unity quality gate` job. Its `Set up runner`
  step reports `This self-hosted runner accepted the workflow_dispatch job`,
  the job summary lists both suites as passed, and the run uploads the XML
  results and Unity logs as `unity-test-results`.

To check that the runner recovers on its own, stop its listener while no job
runs, as a crash would:

```powershell
Stop-Process -Name Runner.Listener -Force
```

Expected result: the task state changes to `Ready`. At the next five-minute
check it is `Running` again. Within a few more minutes the newest
`Runner_*.log` in `C:\actions-runner\_diag` contains `Listening for Jobs`, and
GitHub lists the runner as `online`. In a test on 2026-10-17 the runner was back
four and a half minutes after the stop; the whole recovery takes at most about
ten minutes.

### Operate The Runner

- Status: `Settings > Actions > Runners` on GitHub, or the `gh api` command
  above. On the machine,
  `Get-ScheduledTask -TaskName "GitHub Actions Unity runner"` reports
  `Running` while the runner runs, and `Get-ScheduledTaskInfo` on the same task
  shows the last start. Runner logs are in `C:\actions-runner\_diag`, one
  `Runner_*.log` per start.
- Start it now instead of waiting for the next check:
  `Start-ScheduledTask -TaskName "GitHub Actions Unity runner"`.
- Restart it, for example after a change to `C:\actions-runner\.env`, with
  `Stop-ScheduledTask` and then `Start-ScheduledTask` on the same task.
  Stopping cancels a running job. After a hard stop GitHub keeps the old
  session for a few minutes, so the new listener logs
  `A session for this runner already exists` and retries until it connects.
- Keep it stopped with `Disable-ScheduledTask` and then `Stop-ScheduledTask`
  on the task. `Enable-ScheduledTask` and `Start-ScheduledTask` undo that.
- After a change to `tools/Register-UnityRunnerTask.ps1`, run it again. It
  updates the task and leaves a running runner alone; the change applies at the
  next start.
- A job that fails with Unity exit code `198`, `Access token is unavailable`, or
  `No valid Unity Editor license found` lost the Unity sign-in. Run
  `unity auth login`, check `unity license status`, and rerun the job.
- After a change to `tools/Assert-TrustedRunnerJob.ps1`, copy it to
  `C:\actions-runner-hooks` again. The runner runs that copy, not the
  repository file.
- After a Unity Editor upgrade, change the label in the workflow and on the
  runner in the same pull request:
  `gh api -X PUT "repos/DrottPoker/shooter-mmo/actions/runners/<id>/labels" -f "labels[]=unity-<version>"`.
  The runner id is in the runner listing above.
- To remove the runner, set `UNITY_CI_ENABLED` to `false`, run
  `Stop-ScheduledTask` and then
  `Unregister-ScheduledTask -Confirm:$false` on the task
  `GitHub Actions Unity runner`, run
  `& C:\actions-runner\config.cmd remove --token (gh api -X POST repos/DrottPoker/shooter-mmo/actions/runners/remove-token --jq .token)`
  and delete `C:\actions-runner` and `C:\actions-runner-hooks`.

## One-Time Unity World Scene Authoring

The scenes, prefabs, and `PlayerControls` asset described here are already
committed. Do not repeat these steps on an existing checkout, because recreating
an asset replaces the committed version and its later additions. The steps
record how the assets were first created; use them only to rebuild a missing
asset from scratch. The steps describe the current click-to-move actions and
orbit camera.

### Create The Input Actions Asset

1. Create the folder `Assets/Input`.
2. In that folder, select `Create > Input Actions` and name the asset
   `PlayerControls`.
3. Open it and create an action map named `Player`.
4. Add these actions and bindings:
   - `Select`: Button. Bind `<Mouse>/leftButton`.
   - `PointerPosition`: Value, Vector2. Bind `<Pointer>/position`.
   - `CameraOrbit`: Button. Bind `<Mouse>/middleButton`.
   - `CameraOrbitDelta`: Value, Vector2. Bind `<Mouse>/delta`.
   - `CameraRotate`: Value, Vector2. Add a 2D Vector composite with the four
     arrow keys.
   - `CameraZoom`: Value, Axis. Bind `<Mouse>/scroll/y`.
   - `Cancel`: Button. Bind `<Keyboard>/escape`.
   - `ToggleWorldDebug`: Button. Bind `<Keyboard>/f2`.
   - `ToggleInventory`, `ToggleCharacterInventory`, and
     `ToggleEquipmentInventory`: Button. Bind `<Keyboard>/i`, `<Keyboard>/b`,
     and `<Keyboard>/c`.
5. Click `Save Asset`.

Expected result: `PlayerControls.inputactions` contains one `Player` map and the
eleven actions with no missing bindings.

### Create The LocalPlayer Prefab

1. Create the folders `Assets/Prefabs` and `Assets/Prefabs/Player`.
2. In an empty scene or the current development World scene, create an empty
   GameObject named `LocalPlayer` at position `0, 0, 0`.
3. Add `CharacterController` and use:
   - Radius: `0.35`
   - Height: `2`
   - Slope Limit: `45`
   - Step Offset: `0.35`
   - Skin Width: `0.035`
   - Min Move Distance: `0`
4. Add `CharacterBody` and set Controller Skin Width Ratio to `0.1`. It manages
   the CharacterController center automatically. With these dimensions the
   resulting Center is `0, 1.035, 0`, which places the controller contact
   envelope at local Y zero.
5. Add `PlayerInput`. Assign `PlayerControls` to Actions, set Default Map to
   `Player`, and set Behavior to `Invoke C Sharp Events`.
6. Add `LocalPlayerInput` and `LocalPlayerController`.
7. Add a child named `CameraTarget` at local position `0, 1.45, 0` and assign it
   to the LocalPlayerController Camera Target field.
8. Add a child named `LocalPlayerCamera` directly under LocalPlayer. Tag it
   `MainCamera` and add Camera, AudioListener, and OrbitCameraController.
   Configure:
   - Camera Near Clip Plane: `0.05`
   - Camera Far Clip Plane: `500`
   - Target: `CameraTarget`
   - Minimum, Maximum, and Initial Distance: `3`, `30`, and `12`
   - Minimum, Maximum, and Initial Pitch: `10`, `80`, and `40`
   - Drag Degrees Per Pixel: `0.25`
   - Key Yaw and Key Pitch Degrees Per Second: `120` and `60`
   - Zoom Step Fraction: `0.15`
   - Field Of View: `55`
   - Collision Radius: `0.25`
9. Assign LocalPlayerCamera to both the PlayerInput Camera field and the
   LocalPlayerController Player Camera field.
10. Add an empty child named `PlayerVisual` at local position `0, 0, 0`.
   Add a Capsule child named `Body` at local position `0, 1, 0` and remove its
   CapsuleCollider. All future character models must be authored or positioned
   so their foot plane is local Y zero below PlayerVisual.
11. Optional visual children such as Eyes must also have their primitive colliders
   removed. CharacterController must remain the only player collider.
12. Adjust the visual or material as desired without changing the root scale.
13. Drag the `LocalPlayer` root into `Assets/Prefabs/Player` to create
   `LocalPlayer.prefab`, then delete the temporary scene instance if it was not
   created directly in the development World scene.

Expected result: the prefab root has CharacterController, PlayerInput,
CharacterBody, LocalPlayerInput, and LocalPlayerController. There is exactly one
collision controller on the player. The root is the logical ground point,
PlayerVisual is at local Y zero, CameraTarget and the player-owned
LocalPlayerCamera are assigned on LocalPlayerController, and there is exactly one
Camera and one AudioListener in the prefab.

### Create The RemotePlayer Prefab

1. In an empty scene or the current development World scene, create an empty
   GameObject named `RemotePlayer` at position `0, 0, 0`.
2. Add `RemotePlayerView` to the root.
3. Add an empty child named `PlayerVisual` at local position `0, 0, 0`.
4. Add a Capsule child named `Body` below PlayerVisual at local position
   `0, 1, 0`.
5. Remove the CapsuleCollider that Unity adds to Body.
6. Give Body a visually distinct material if desired. Keep its foot plane at
   local Y zero.
7. Verify the complete RemotePlayer hierarchy contains no Collider, Rigidbody,
   CharacterController, PlayerInput, Camera, or AudioListener component.
8. Drag the RemotePlayer root into `Assets/Prefabs/Player` to create
   `RemotePlayer.prefab`, then delete the temporary scene instance.

Expected result: `Assets/Prefabs/Player/RemotePlayer.prefab` contains one
`RemotePlayerView` and at least one Renderer. It is a presentation-only network
view with no local input, physics authority, camera, or audio listener.

### Build The Test Map

1. Open `Assets/Scenes/DevelopmentWorld1.unity`.
2. Create an empty root named `Environment` with empty `Boundaries` and
   `Obstacles` children. Reset all three transforms.
3. Create these Cube objects. Parent the walls under `Boundaries`, the test
   objects under `Obstacles`, and Ground directly under `Environment`:
   - `Ground`: position `0, -0.25, 0`, scale `30, 0.5, 30`.
   - `NorthWall`: position `0, 1.5, 14.5`, scale `30, 3, 1`.
   - `SouthWall`: position `0, 1.5, -14.5`, scale `30, 3, 1`.
   - `WestWall`: position `-14.5, 1.5, 0`, scale `1, 3, 30`.
   - `EastWall`: position `14.5, 1.5, 0`, scale `1, 3, 30`.
   - `CameraTestWall`: position `0, 1.5, 4`, scale `8, 3, 0.5`.
   - `LowCover`: position `-4, 0.75, -2`, scale `4, 1.5, 1`.
   - `HighCover`: position `5, 1.5, 1`, scale `2, 3, 2`.
   - `Ramp`: position `0, 0.4, -7`, rotation `12, 0, 0`, scale `5, 0.5, 8`.
   - `Step01`: position `-8, 0.15, -7`, scale `2, 0.3, 2`.
   - `Step02`: position `-8, 0.3, -5.5`, scale `2, 0.6, 2`.
   - `Step03`: position `-8, 0.45, -4`, scale `2, 0.9, 2`.
4. Keep the BoxCollider on all 12 map primitives and do not add Rigidbodies.
5. Mark Environment and every child as static.
6. Create Ground, Wall, and Obstacle materials under
   `Assets/Art/Materials/TestMap` and assign them to make collision surfaces
   visually distinct.

Expected result: DevelopmentWorld1 contains a bounded movement area with flat ground,
a slope, three step heights, low and high cover, and a dedicated wall for camera
collision testing.

### Configure And Bake World Collision

This is required once for DevelopmentWorld1 and again whenever an
authoritative map collider changes.

1. Exit Play Mode and open `Assets/Scenes/DevelopmentWorld1.unity`.
2. Select the `Environment` root.
3. Click `Add Component` and add `World Collision Authoring`.
4. Configure the component:
   - World Id: `development-world-1`
   - Chunk Size: `32`
   - Collision Root: drag the same `Environment` object into this field
   - Layer Mask: `3`
5. Save DevelopmentWorld1.
6. Select `MMORPG > Tools > World Collision > Bake Open Scene` from Unity's top
   menu.
7. Wait for asset import and script compilation to complete.

After both development scenes are Addressables World scenes, use `MMORPG >
Tools > World Collision > Bake World Scenes` to validate each catalog
mapping and bake every World scene in one deterministic pass. When
several Worlds share one reusable scene, the scene is baked once for each of
those Worlds, and its `WorldCollisionAuthoring` World id must be one of them.

The bake writes every file to a temporary `.tmp` sibling first and then swaps
them in, chunks before the manifest, with a backup of each previous file. If any
write or swap fails, the previous files are restored, so a failed bake never
leaves a manifest whose checksums do not match its chunks. Unity ignores the
`.tmp` files.

Expected result: Unity Console reports `[WORLD COLLISION] Baked world
'development-world-1'` with 13 boxes, 0 meshes, and 0 heightfields in 4 chunks,
and a SHA-256 revision. The command updates:

- `content/worlds/development-world-1/collision.json`
- `src/Shared/WorldData/Generated/WorldCollision/development-world-1/manifest.json`
- Four `chunk_*.bytes` files in the same generated directory

The bake reads enabled, non-trigger BoxColliders, non-convex MeshColliders, and
TerrainColliders below the collision root. Meshes and terrains bake as one-sided
triangles that collide only from their front. The bake stops with a red error
for any other Collider type, for a convex MeshCollider, and for a rotated or
scaled Terrain. The authoring rules for terrain, caves, and tunnels are in
`docs/DEVELOPMENT_WORLDS.md` under Terrain And Mesh Collision Authoring.

Verify the checked-in bake from the repository root:

```powershell
dotnet run --project tools/WorldCollisionCompiler -- --verify
```

Expected result: the command reports the same World revisions, four verified
chunks for Development World 1, and 256 verified chunks for Development World
2. It exits with code 1 if any World's collision source and generated data
differ.

### Compose DevelopmentWorld1

1. Create an empty root named `Gameplay` and reset its transform.
2. Add a child named `PlayerSpawn` at position `0, 0, -1` with rotation
   `0, 0, 0`.
3. Do not place a LocalPlayer instance in DevelopmentWorld1. The player exists
   only after an accepted character join.
4. Remove any separate Main Camera from DevelopmentWorld1. The runtime LocalPlayer owns
   the only gameplay camera and AudioListener.
5. Create an empty child of Gameplay named `EntityPresentationRoot` and reset
   its transform. This object owns runtime views for replicated entities and
   must not be a child of LocalPlayer.
6. Create an empty GameObject named `WorldSceneContext` and add the
   `WorldSceneContext` component.
7. Assign the `LocalPlayer.prefab` asset to Local Player Prefab and assign
   Player Spawn Point to PlayerSpawn. Drag the prefab asset from the Project
   window, not a scene instance.
8. Assign `RemotePlayer.prefab` to the Remote Player Prefab field. Drag the
   prefab asset from the Project window, not a temporary scene instance.
9. Assign Entity Presentation Root to the `EntityPresentationRoot` transform.
10. Keep one Directional Light in the scene and save the scene.

Expected result: DevelopmentWorld1 contains no LocalPlayer or gameplay camera
before a join. After an accepted join, WorldSceneContext creates one LocalPlayer
from the assigned prefab at PlayerSpawn, connects it to the authoritative
movement state, and connects its camera. Runtime remote views remain below
`EntityPresentationRoot`. Opening DevelopmentWorld1 directly without a joined
session creates no player and reports no missing-reference error.

The Unity project also contains separate EditMode and PlayMode test assemblies.
Open `Window > General > Test Runner` and run both suites before delivering Unity
changes. You can run the same suites outside the Editor with:

```powershell
$env:UNITY_EDITOR_PATH = "C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe"
& .\tools\Run-UnityTests.ps1
```

An explicit `-UnityEditorPath` or `UNITY_EDITOR_PATH` always runs that Editor.
The script uses the Unity CLI only when `-UnityCliPath` or `UNITY_CLI_PATH` is
set, or when neither path is set and the CLI exists at
`%LOCALAPPDATA%\Unity\bin\unity.exe`. The headless run cannot start while an
Editor has the project open; use the live Editor commands in
[Unity Editor Automation With Unity CLI](#unity-editor-automation-with-unity-cli)
instead.

Expected result:

- EditMode validates API parsing, client state, Input Actions, collision resource
  loading, prediction, reconciliation, remote interpolation, world-actor and
  interaction state, pointer targeting, actor content tooling, both player
  prefab contracts, and authored development-world scene composition.
- PlayMode validates that loading `LoginMenu` creates the persistent client
  bootstrap, realtime client, actor and interaction controllers, and runtime
  login panel, and that DevelopmentWorld1 without a joined session creates no
  LocalPlayer while creating the expected presentation and temporary UI
  controllers.

The headless workflow requires an active Unity Editor entitlement for the exact
installed Editor version. If the script exits with code `198`, reports `Access
token is unavailable`, or reports `No valid Unity Editor license found`, perform
these exact manual steps:

1. Exit every Unity Editor process and quit Unity Hub from its notification-area
   icon.
2. Start Unity Hub, sign in, and confirm that a valid Personal or organization
   license is active under `Preferences > Licenses`.
3. Open this project with Unity `6000.5.4f1` once and wait for package import and
   script compilation to finish without a licensing error.
4. Close the Editor, keep the signed-in Hub session available, and rerun
   `tools/Run-UnityTests.ps1`.

Expected result: the script creates fresh EditMode and PlayMode XML files under
`TestResults/Unity` and reports both suites as passed. License exit code `198`
occurs before project tests start and is not a test failure. As in the backend
gate, a skipped or inconclusive test, or a suite without tests, fails the run.

CI runs the same script for every pull request on a self-hosted runner,
described in [Unity CI Runner](#unity-ci-runner). If Unity is not installed in
the default Unity Hub path there, the repository variable `UNITY_EDITOR_PATH`
can name the full path to `Unity.exe`.

Before using the Unity client, start these services:

```powershell
docker compose up -d
dotnet run --project src/Server/GameService
dotnet run --project src/Server/SimulationWorker
```

Then open `src/Client` in Unity and start from:

```text
Assets/Scenes/LoginMenu.unity
```

The networking packages are declared in `Packages/manifest.json`. If Unity was
open while they were first added, exit Play Mode and wait for package resolution
and script compilation to finish. If Unity still displays the old duplicate
`Mmorpg.GameProtocol` assembly error after compilation, close and reopen the
project once so its package cache is rebuilt. The persistent bootstrap adds
`RealtimeSimulationClient` to its own runtime object. The only new authored movement
reference is the Remote Player Prefab field on WorldSceneContext, described
above.

Simulation snapshots use LiteNetLib's unchanneled `Unreliable` delivery. LiteNetLib
reports these receive events as channel 0 even if a channel number was supplied
to the send overload. Snapshot validation therefore checks the protocol message
type and delivery method, while reliable control messages still validate channel
0 explicitly. After changing realtime transport code, exit Unity Play Mode and
restart SimulationWorker so both processes use the current protocol implementation.
The protocol also validates shard and World identity, the exact placement,
the compiled movement-simulation revision, the initial carry tuple,
server-assigned network entity ids, reliable carry-state updates, reliable item
operation intents and results, and reliable entity lifecycle messages.
Rebuild every standalone client after a protocol or simulation revision change.

An intended change to shared movement or collision behavior fails the movement
determinism tests in both the backend suite and Unity EditMode. Bump
`GameSimulationCompatibility.Revision`, then set `ExpectedSimulationRevision`
and `ExpectedFingerprint` in
`src/Client/Assets/Tests/EditMode/Shared/MovementDeterminismScenario.cs`
to the new values. `Mmorpg.Shared.Tests` compiles the same file, so both
runtimes keep checking one pinned pair. If only Unity fails, a new float
expression in `GameSimulation` is missing an explicit `(float)` cast around an
intermediate result; `MovementDeterminismScenario.ComputeTrace()` returns the
per-tick states for comparing the runtimes.
Standalone build output belongs under ignored `ClientBuilds` or `Builds`
directories and must never be committed.

### Rebind An Offline Shard To Another World

Shard-to-World binding is intentionally changeable between worker process
generations. It is not a runtime map switch. Prepare all target World content
before changing a shard:

- Add `content/worlds/<WorldId>/world.json` with the stable identity, client
  scene, bounds, and spawn. Item services such as Bank and Recovery Storage come
  from NPC capabilities in the actor spawns.
- Provide `content/worlds/<WorldId>/collision.json` and its compiled collision
  data below `src/Shared/WorldData/Generated/WorldCollision/<WorldId>`.
- Provide `content/worlds/<WorldId>/actor-spawns.json` with at least one spawn
  and its compiled
  `src/Shared/WorldData/Generated/Worlds/<WorldId>/world-actors.json`. The
  SimulationWorker loads runtime actor data for every World, and both World
  compilers reject a World directory that lacks `world.json`,
  `actor-spawns.json`, or `collision.json`. Shared actor definitions remain under
  `content/actors`.
- Map the authored Unity scene's `WorldId` in
  `Assets/Content/Worlds/world-scene-catalog.json`, then run `MMORPG > Tools >
  Addressables > Apply Client Layout` so the scene becomes an Addressables
  World scene. Do not add it to Build Profiles.

Perform the rebind in this order:

1. Stop the shard's SimulationWorker cleanly with `Ctrl+C` and wait for its
   graceful offline registration to complete.
2. Confirm players have left and allow all open durable corpses for that shard
   to expire or close through their normal lifecycle. Do not rewrite World-local
   coordinates in PostgreSQL by hand.
3. Change only `SimulationWorker:WorldId` in
   `src/Server/SimulationWorker/Config/appsettings.json` and verify that the selected World
   directory contains its manifest, collision, and actor runtime data.
4. Start SimulationWorker. Its first authenticated heartbeat proposes the
   configured World. GameService locks the shard, verifies that the World exists
   in the database, checks the offline rebind blockers, and atomically persists
   the new binding with the worker assignment.
5. If the worker stops after GameService returns
   `shard_world_rebind_blocked`, leave it stopped. The error lists active
   assignments, pending join tickets, active simulation sessions, and open
   corpses. Drain the reported state and start the worker again.
6. If GameService returns `simulation_world_not_found`, correct the World
   manifest or configured `WorldId`, restart GameService so it reconciles the
   canonical manifest catalog, and then start the worker again.
7. Join through CharacterSelect. Unity resolves the accepted `WorldId` through
   its client catalog before it requests a ticket, revalidates the exact World
   returned by placement, and loads the mapped scene only after the realtime
   join succeeds.

Expected result: no player can enter during the rebind, stale worker content
cannot claim the shard, and the next worker runtime plus Unity client use the
same World identity. The repository contains complete manifest, scene,
collision, actor, and client-catalog baselines for both development Worlds.
The local worker configuration currently requests `development-world-2` for
`local-shard-1`. Follow
[Development Worlds](DEVELOPMENT_WORLDS.md) and repeat its verification before
performing another rebind.

The current catalog maps both development World IDs to their scenes, and both
scenes are part of the build. Their exact Unity scene, collision-bake, and Build
Profiles workflow is documented in [Development Worlds](DEVELOPMENT_WORLDS.md).

Scene flow:

- `LoginMenu`
- `CharacterSelect`
- the scene mapped from the selected shard's `WorldId`, currently `DevelopmentWorld2`

Expected result in `LoginMenu`:

- A `Login Menu` panel appears in the Game view.
- `Game Service` displays `http://localhost:5000`.
- Request timeout displays 10 seconds.
- Realtime timeout displays 10 seconds.

These values are stored in
`Assets/Content/Config/ClientConfig.asset`, which the client loads through
Addressables at startup. Change the asset before building a client for another
environment.
Endpoint fields are no longer editable from the runtime login panel.

Manual Unity test flow:

1. Change `Email` and `Username` to unique values.
2. Click `Register`.
3. Unity loads `CharacterSelect`.
4. Create a character if none exists.
5. Select a character.
6. Select `Local Shard 1`.
7. Click `Join Selected Shard`.
8. Unity loads `DevelopmentWorld2`.

Expected result:

- `DevelopmentWorld2` shows the selected character on shard `local-shard-1`
  using World `development-world-2`.
- A local test player is instantiated only after the accepted join and starts
  from the authoritative state associated with the authored PlayerSpawn.
- A left click on the ground walks there, the middle mouse button or the arrow
  keys orbit the camera, and the scroll wheel zooms. The cursor stays visible.
- F2 hides or restores the scrollable bottom-left World Client Debug panel.
- World Debug displays `Joined` and `127.0.0.1:27015/udp`.
- World Debug displays shard `local-shard-1`, World `development-world-2`, worker
  `local-simulation-worker-1`, and the current runtime id.
- World Debug displays an increasing observed server tick, the configured 30 Hz
  simulation and 15 Hz snapshot rates, client FPS and frame timing, prediction
  backlog, reconciliation statistics, ping, snapshot age, observed packet and
  payload rates, estimated snapshot loss, and client-known entities.
- World Debug does not display SimulationWorker CPU, memory, capacity, or total
  player and bot populations. Those values appear only in the SimulationWorker
  terminal and metrics surface.
- Local movement responds immediately through prediction and remains corrected
  to SimulationWorker snapshots without repeated visible snapping on flat ground.
- `Leave Shard` receives server acknowledgement, closes UDP, and returns to
  `CharacterSelect`.
- SimulationWorker logs the joined and released simulation-session ids.
- `Back To Login` revokes the current GameService session before clearing local
  client state.

Expected Unity Console sequence for a successful login and simulation join:

```text
[AUTH] Validating login credentials for username 'player_one'.
[AUTH] Account 'player_one' (<account-id>) logged in.
[CLIENT] Character 'Hero One' (<character-id>) is requesting access to shard 'local-shard-1'.
[AUTH] GameService issued a short-lived join ticket for character 'Hero One' on shard 'local-shard-1'.
[CLIENT] Opening UDP connection to SimulationWorker 'local-simulation-worker-1' runtime '<worker-runtime-id>' at 127.0.0.1:27015/udp for shard 'local-shard-1'.
[CLIENT] UDP transport connected to 127.0.0.1:27015/udp. Sending the short-lived join ticket to SimulationWorker.
[SIMULATION] Account '<account-id>' with character 'Hero One' (<character-id>) connected to shard 'local-shard-1' for world 'development-world-2' through worker 'local-simulation-worker-1' runtime '<worker-runtime-id>'. Simulation session '<simulation-session-id>' controls network entity '<entity-id>'.
[CLIENT] Server-authoritative movement is active at 30 ticks per second with 15 snapshots per second.
```

An authentication, API, transport, timeout, protocol, or SimulationWorker rejection
appears as a red Unity Console error with the responsible category and stable
error code. Passwords, bearer tokens, join-ticket values, service secrets, and
secret simulation-session tokens are never written to the console.

### LiteNetLib Package Signature Warning

Unity Package Manager shows a yellow missing-signature warning for LiteNetLib
2.1.4 because the package is delivered by the scoped OpenUPM third-party
registry and is not signed by Unity. It is not a LiteNetLib compilation error.
The project pins the exact version in `Packages/manifest.json` and
`Packages/packages-lock.json`.

No Unity Editor action is required for this warning. Do not select an available
package update without reviewing the upstream changelog and source changes.
Before a production release, review the resolved package source and license, and
consider vendoring the reviewed source or pinning an immutable reviewed upstream
revision if the release threat model requires stronger supply-chain control.

Unity client stability test:

1. Double-click `Register`, `Create Character`, refresh, join, and leave buttons.
2. Verify only one operation starts and controls remain disabled until it ends.
3. Stop SimulationWorker, click `Join Selected Shard`, and wait for the realtime
   timeout.
4. Verify CharacterSelect shows a structured realtime network or timeout error
   and does not load a development World scene.
5. Restart SimulationWorker, join again, and use `Leave Shard`.
6. Verify only one leave runs, CharacterSelect loads after acknowledgement, and
   SimulationWorker logs the released session.
7. Verify that an in-flight simulation snapshot during leave does not produce
   `invalid_snapshot_delivery`.
8. Join again and stop SimulationWorker while the selected World scene is active.
9. Verify the client clears shard state and returns to CharacterSelect after the
   unexpected disconnect.
10. Revoke the current account session, then refresh characters.
11. Verify the client clears all local state and returns to LoginMenu after HTTP
    401.

Single active account session test with a standalone build:

1. Restart GameService so it applies the latest database migration, then start
   SimulationWorker.
2. Rebuild the standalone development client so it contains the current session
   monitor and realtime disconnect handling.
3. Start the standalone client and enter `local-shard-1` with character one.
4. In Unity Play Mode, log into the same account. Selecting character two is
   allowed only after this new login has replaced the first session.
5. Wait up to five seconds and inspect the first client's Unity log.
6. Join `local-shard-1` with character two from the newly authenticated client.

Expected result:

- The first client is disconnected, clears its account and shard state, and
  returns to LoginMenu.
- Its Console contains an `[AUTH]` message explaining that the account logged in
  from another client and includes `account_session_replaced`.
- The first character disappears from other clients after its exact server
  session is invalidated.
- Only the second account session remains authorized and only its selected
  character can remain connected.

Click-to-move and camera test:

No Inspector, scene, prefab, package, input-action, or build-setting change is
required. The committed `PlayerControls` asset and `LocalPlayer` prefab already
contain click-to-move and the orbit camera.

1. Join `DevelopmentWorld2` through LoginMenu and CharacterSelect. Press F2 to
   hide World Debug if it covers much of a small Game view, and keep the Game
   view focused.
2. Left-click the ground a few meters in front of the player.
3. From the plaza, click the ground east of the East Service Hall, so the
   straight line crosses the hall. Press F2 to watch World Debug while the player
   walks.
4. Click the roof or a wall face of a hall.
5. Click a new spot while the player still walks.
6. Drag with the middle mouse button, then hold each arrow key.
7. Scroll the mouse wheel both ways.
8. Walk so a hall stands between the player and the camera.
9. With World Debug visible, click and scroll on the panel.
10. Hover Mira the Quartermaster on the plaza, then left-click her from more than
    three meters away.
11. Press Escape.

Expected result:

- Step 2: a small yellow disc marks the destination. The player starts walking
  on the next simulation tick without a visible server round trip, turns toward
  its direction of travel, stops on the disc, and the disc disappears.
- Step 3: the player walks around the hall, never into it. World Debug shows
  the path id, the current waypoint of the route, and the latest plan with its
  search nodes and milliseconds. Frames do not stall while it plans.
- Step 4: the player walks to the closest reachable point beside the hall and
  stops there.
- Step 5: the new click replaces the route at once.
- Step 6: dragging turns and tilts the camera around the player. Left and right
  arrows turn it, up and down tilt it, and the tilt stops at 10 and 80 degrees.
- Step 7: each notch zooms by about 15 percent between 3 and 30 meters, and the
  zoom eases instead of jumping.
- Step 8: the camera moves in front of the hall at once and eases back out
  afterwards.
- Step 9: clicks and scrolling on the panel neither move the player nor zoom
  the camera.
- Step 10: the prompt reads `Click to interact with Mira the Quartermaster`. The
  player walks to about 2.75 meters from Mira and stops, and her capability
  panel opens.
- Step 11: Escape closes the interaction.
- The cursor stays visible and free throughout. WASD, Shift, Space, the right
  mouse button, F1, and E do nothing.
- World Debug reports no hard snaps, and the latest position correction stays
  near zero while the player walks.
- The SimulationWorker terminal shows no `unknown_movement_path` or
  `invalid_movement_input` protocol errors.

Server-authoritative movement test:

1. Start PostgreSQL, Redis, GameService, and SimulationWorker with the commands above.
2. Enter DevelopmentWorld1 through LoginMenu and CharacterSelect. Do not start
   directly from DevelopmentWorld1 for this test.
3. Confirm World Debug shows server-authoritative client prediction and an
   increasing observed server tick value.
4. Click spots far apart, and click a new spot to change direction sharply while
   the player walks.
5. Click the far side of CameraTestWall, LowCover, and HighCover, and click
   beyond a boundary wall.
6. Click a spot halfway up Ramp, wait at least three seconds there, then click
   the tops of Step01, Step02, and Step03 in turn, and walk off the high edge of
   the second ramp.
7. Watch the Unity Console and SimulationWorker terminal while moving for at least
   30 seconds.
8. Stop SimulationWorker while the character is moving.

Expected result:

- Input remains responsive because the client predicts the same fixed-step rules
  used by SimulationWorker.
- Normal snapshots do not cause repeated large position snaps on flat ground,
  the ramp, or steps.
- The server tick increases continuously and snapshots acknowledge input without
  protocol errors.
- Routes lead around the walls and cover. A route against a boundary ends at
  the closest reachable point, and a route that collision blocks ends after half
  a second instead of pushing against the wall.
- The capsule follows the walkable ramp and climbs the configured step heights
  in both the predicted and authoritative state.
- A route continues when input packets pause, because every route ends.
- Standing on Ramp leaves the character stationary, and the rendered feet
  remain aligned with the ramp surface while moving and idle.
- Step01, Step02, Step03, and short grounded drops transition smoothly instead
  of snapping the rendered player or camera directly to each discrete height.
- Walking off an edge above the ground snap range falls and lands without delay
  from step presentation smoothing.
- The client refuses to activate movement and reports a collision revision error
  if its baked world data differs from SimulationWorker.
- The client refuses to activate movement and reports a simulation revision
  error if its compiled movement rules differ from SimulationWorker.
- Stopping SimulationWorker clears the active simulation session and returns the client to
  CharacterSelect through the existing disconnect recovery flow.

Remote interpolation test with a standalone build:

1. Create a Windows development build containing LoginMenu, CharacterSelect,
   and DevelopmentWorld1 in that order.
2. Run the build and keep Unity Editor available as the second client.
3. Log in with two different accounts and select two different characters on
   `local-shard-1`.
4. Join the shard from both clients.
5. Click to walk with each character while watching it from the other client.

Expected result: each client owns one predicted local player and creates one
presentation-only RemotePlayer instance for the other character under
`Gameplay/EntityPresentationRoot`. The local player is not duplicated under
that root. Remote motion is interpolated instead of jumping directly between 15
Hz snapshots. After a temporary network or frame stall, the remote render clock
restores its intended buffer instead of retaining permanent extra delay.
Leaving or disconnecting sends reliable despawn and removes the corresponding
remote view immediately without waiting for a snapshot timeout.

DevelopmentWorld1 lifecycle test without a join:

1. Open `Assets/Scenes/DevelopmentWorld1.unity`.
2. Press Play.

Expected result:

- The scene-authored map, spawn point, prefab references, and presentation root
  remain available.
- No LocalPlayer, gameplay camera, or local input object is created.
- World Debug reports that no active simulation session exists.

Use the full login and simulation join flow above to test movement across the
ramp, steps, cover, and camera wall. Authenticated World-scene movement is
intentionally not an offline preview because a local player represents an
accepted world presence.

## Related Documentation

- [Project Overview](PROJECT_OVERVIEW.md)
- [Project Architecture](PROJECT_ARCHITECTURE.md)
- [Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md)
- [Service Features](SERVICE_FEATURES.md)
- [Monitoring](MONITORING.md)
- [Game Features](GAME_FEATURES.md)
