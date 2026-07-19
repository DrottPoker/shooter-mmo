# Local Development

Last updated: 2026-07-19

## Requirements

- .NET SDK 10.0 or newer.
- Docker Desktop.
- Unity Editor for the client project.

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
`SimulationWorker/Config/appsettings.json`. AuthService owns only the available
fleet, node, and shard topology. Both services search their content root and
parent directories for `.env`. Explicit process environment variables and
command-line values can still override service configuration for deployments.

SimulationWorker loads `WorldData/Worlds/<WorldId>/world.json` and derives actor
and collision paths below the same World directory. The checked-in content for
both development Worlds is copied into build and publish output. Startup
validates identity, bounds, spawn, service points, actor revisions, references,
ordering, collision checksums, and content semantics before UDP admission
begins. `SimulationWorker__WorldDataPath` can point a deployment at a different
canonical Worlds root.

AuthService reads `Items:CatalogPath` from its configuration. The checked-in
default points to `WorldData/Items/core.item-catalog.json`, which the AuthService
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
dotnet restore ShooterMmo.slnx --locked-mode
& .\Tools\Verify-DependencyPolicy.ps1
dotnet format ShooterMmo.slnx --verify-no-changes --no-restore
dotnet build ShooterMmo.slnx --configuration Release --no-restore
dotnet run --project Tools/ItemCatalogCompiler `
  --configuration Release `
  --no-build -- `
  WorldData/Shared/Authoring/Items/core.item-catalog.json `
  WorldData/Shared/Runtime/Items/core.item-catalog.json `
  --verify
dotnet test ShooterMmo.slnx --configuration Release --no-build
```

Expected result:

- Restore accepts every committed `packages.lock.json` file.
- Dependency policy verification confirms exact Unity and NuGet versions, local
  package paths, Unity lock coverage, and the absence of tracked client builds.
- Format reports no files that need changes.
- Build completes with zero warnings and zero errors.
- Item catalog verification reports nine definitions, one base Secure Container
  tier, and the deterministic checked-in revision.
- Unit tests pass.
- PostgreSQL integration tests are skipped unless their dedicated connection is
  configured.

NuGet and GitHub Actions updates are proposed monthly by Dependabot against
`development`. Unity Package Manager updates remain explicit because Dependabot
does not own `Packages/manifest.json`. Update Unity packages in a focused branch,
let Unity rewrite `packages-lock.json`, run both Unity suites, and run the backend
quality gate before merging. `LiteNetLib` must stay aligned between
`SimulationWorker/SimulationWorker.csproj` and the Unity manifest.

## Item Catalog Authoring And Verification

The neutral source catalog is
`WorldData/Shared/Authoring/Items/core.item-catalog.json`. It defines stable category,
tag, equipment-slot, item, Bag-layout, location-eligibility, policy-default, and
Secure Container tier content. The generated runtime catalog is
`WorldData/Shared/Runtime/Items/core.item-catalog.json`.

Catalog format version 2 uses unitless non-negative integer `unitWeight` and
`carryCapacityBonus` fields. Do not add physical-unit or decimal weight fields.
The baseline scale is ammunition `1`, pistol `10`, and base character capacity
`200`.

The canonical content workflow is available in Unity at
`Shooter MMO > Tools > Item Catalog`:

1. Use the searchable definition list and category filter to select an item.
2. Edit gameplay fields and the separate client presentation fields in the same
   window. Baked definition ids are read-only and cannot be deleted or reused.
3. Optionally choose an icon Sprite below an `Assets/Resources` folder, with one
   Sprite per asset file. The tool stores its extension-free Resources path, not
   the image data, in the presentation JSON. An item may remain iconless until
   approved client art is available.
4. Select `Validate` to run the strict shared compiler without writing files.
5. Select `Save` to write editable authoring and presentation content without
   replacing the runtime gameplay catalog.
6. Select `Save And Bake` to validate and atomically replace authoring, runtime,
   and presentation JSON. Structural changes require explicit confirmation.

The gameplay source remains
`WorldData/Shared/Authoring/Items/core.item-catalog.json`. The Editor-only assembly does
not duplicate its rules. It invokes `Tools/ItemCatalogCompiler`, which remains
the validation and structural-fingerprint authority used by backend development
and CI.

Client-only presentation content is bundled at
`shooter-mmorpg-unity-client/Assets/Resources/Items/Presentation/item-presentation-catalog.json`.
It maps every stable gameplay definition id to an optional icon Resources path,
localization key, fallback display name, and optional prefab presentation key.
The file records the exact gameplay source revision and a separate deterministic
presentation revision. Runtime lookup validates the pair once and caches it.
Inventory responses therefore need definition ids and instance state, not icon
files or complete definition data.

After an intentional authoring change, compile the runtime catalog:

```powershell
dotnet run --project Tools/ItemCatalogCompiler -- `
  WorldData/Shared/Authoring/Items/core.item-catalog.json `
  WorldData/Shared/Runtime/Items/core.item-catalog.json
```

Then verify the checked-in result:

```powershell
dotnet run --project Tools/ItemCatalogCompiler -- `
  WorldData/Shared/Authoring/Items/core.item-catalog.json `
  WorldData/Shared/Runtime/Items/core.item-catalog.json `
  --verify
```

Expected result: both commands report catalog `core`, nine definitions, one
base Secure Container tier, and the same SHA-256 catalog revision. `--verify`
returns exit code 1 for malformed authoring, invalid rules, or stale runtime
JSON.

Inspect `structuralFingerprint` for every changed definition and Secure
Container tier. A display-only edit changes the catalog revision without
changing an item structural fingerprint. Weight, stack, tag, equipment,
location, policy-default, Bag-layout, or tier-capacity changes alter the
relevant structural fingerprint. AuthService now permits a structural revision
only when no live item instance or affected Secure Container entitlement depends
on it. Otherwise startup fails with the affected stable id and requires an
explicit data migration before the new catalog can become current. Display-only
changes remain safe because they preserve the structural fingerprint.

The complete authoring contract is documented in
`WorldData/Shared/Authoring/Items/README.md`. No Unity Editor action is required for
command-line item catalog compilation or verification.

If `Validate`, `Save`, or `Save And Bake` fails, read the field paths in the
window error. Validation uses temporary candidate files. A failed validation
writes nothing, and a failed multi-file bake restores every previous catalog
file. Correct or discard the draft, select `Reload` to return to checked-in
content when appropriate, and run the command-line `--verify` command above.

## Phase 3 PostgreSQL Item Foundation Verification

Start the isolated temporary PostgreSQL database and run only the Phase 3
integration suite:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$values = @{}
Get-Content .env | ForEach-Object {
  if ($_ -match '^([^#=]+)=(.*)$') {
    $values[$matches[1]] = $matches[2]
  }
}
$env:SHOOTER_MMO_TEST_POSTGRES = `
  "Host=127.0.0.1;Port=55432;" + `
  "Database=$($values['TEST_POSTGRES_DB']);" + `
  "Username=$($values['TEST_POSTGRES_USER']);" + `
  "Password=$($values['TEST_POSTGRES_PASSWORD'])"
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~ItemPersistenceIntegrationTests"
```

Expected result: ten tests pass. They reset only the dedicated database whose
name contains `test`, then verify the complete catalog mirror, legacy backfill,
atomic new-character bootstrap, uniqueness and location constraints, planned
custody shapes, explicit delete behavior, and structural catalog startup fence.

To exercise concurrent migration initialization as well, run:

```powershell
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~ConcurrentMigrationInitializationAppliesEachMigrationOnce"
```

Expected result: the test passes with ten immutable migration ids, one current
catalog revision, and no duplicate topology or item seed rows.

For an existing local development database, start AuthService normally. Expect
one log entry if the new migration or a new catalog revision is applied. Inspect
the resulting foundation without exposing database credentials:

```powershell
docker compose exec postgres sh -lc 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "select catalog_id, revision, is_current from item_catalog_revisions order by applied_at;"'
docker compose exec postgres sh -lc 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "select count(*) as active_characters_missing_item_state from characters c left join character_item_states s on s.character_id = c.id where c.deleted_at is null and s.character_id is null;"'
```

Expected result: catalog `core` has exactly one row with `is_current = true`,
and `active_characters_missing_item_state` is `0`. No Unity Editor action is
required for Phase 3 verification.

Stop the isolated test database when finished:

```powershell
docker compose -f docker-compose.test.yml down
```

## Phase 4 Item Read Model Verification

Start the isolated PostgreSQL database, configure the dedicated test connection
as shown in the Phase 3 section, then run the read-model suite:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$values = @{}
Get-Content .env | ForEach-Object {
  if ($_ -match '^([^#=]+)=(.*)$') {
    $values[$matches[1]] = $matches[2]
  }
}
$env:SHOOTER_MMO_TEST_POSTGRES = `
  "Host=127.0.0.1;Port=55432;" + `
  "Database=$($values['TEST_POSTGRES_DB']);" + `
  "Username=$($values['TEST_POSTGRES_USER']);" + `
  "Password=$($values['TEST_POSTGRES_PASSWORD'])"
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~ItemReadModelIntegrationTests"
docker compose -f docker-compose.test.yml down
```

Expected result: seven tests pass. They verify the current catalog graph,
cross-account denial, complete empty state, stable slot ordering, all owned
snapshot sections, fixed-point encumbrance, definition-id deduplication,
secret exclusion, and read-only behavior. Fixture items are written only by
test-project helpers to the dedicated test database. AuthService exposes no
grant route.

To exercise the two real HTTP reads, start local PostgreSQL and Redis with
`docker compose up -d --wait`, then run `dotnet run --project AuthService`. In a
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
  -Body (@{ name = "Phase Four $($suffix.Substring(0, 8))" } | ConvertTo-Json)
$catalog = Invoke-RestMethod `
  -Uri http://localhost:5000/api/items/catalog `
  -Headers $headers
$snapshot = Invoke-RestMethod `
  -Uri "http://localhost:5000/api/characters/$($character.id)/inventory" `
  -Headers $headers
$catalog.definitions.Count
$snapshot.permanentInventory.slots.Count
$snapshot.bank.slots.Count
$snapshot.secureContainer.contents.slots.Count
```

Expected result: the values are `9`, `20`, `40`, and `4`. The snapshot is empty
but complete, reports capacity `200`, and contains no operation payload,
credentials, client icon, or repeated definition metadata. No Unity Editor
action is required for Phase 4 verification.

## Phase 5 Item Transaction Kernel Verification

Phase 5 has no HTTP or Unity mutation flow. Verify the internal kernel against
the isolated PostgreSQL database:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$values = @{}
Get-Content .env | ForEach-Object {
  if ($_ -match '^([^#=]+)=(.*)$') {
    $values[$matches[1]] = $matches[2]
  }
}
$env:SHOOTER_MMO_TEST_POSTGRES = `
  "Host=127.0.0.1;Port=55432;" + `
  "Database=$($values['TEST_POSTGRES_DB']);" + `
  "Username=$($values['TEST_POSTGRES_USER']);" + `
  "Password=$($values['TEST_POSTGRES_PASSWORD'])"
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~ItemConcurrencyIntegrationTests"
Remove-Item Env:SHOOTER_MMO_TEST_POSTGRES
docker compose -f docker-compose.test.yml down
```

Expected result: eleven tests pass. The suite executes every internal command
and verifies account authorization, effective policy lineage, specific and
deterministic slots, equipment, empty and non-empty Bag rules, Recovery add and
claim, account-wide Secure Container tier changes, unitless carried-state
recomputation, exact hard-cap rollback, canonical operation replay, competing
item, slot, equipment, and quantity races, the shared Bag aggregate lock, and
failed-swap rollback. The dedicated database is reset between tests.

No AuthService item write endpoint should be manually invoked because Phase 5
does not expose one. No Unity Editor action is required for Phase 5 verification.

## Phase 6 Policy And Offline Item API Verification

Run the Phase 6 policy and HTTP integration suites against the isolated test
database:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$values = @{}
Get-Content .env | ForEach-Object {
  if ($_ -match '^([^#=]+)=(.*)$') {
    $values[$matches[1]] = $matches[2]
  }
}
$env:SHOOTER_MMO_TEST_POSTGRES = `
  "Host=127.0.0.1;Port=55432;" + `
  "Database=$($values['TEST_POSTGRES_DB']);" + `
  "Username=$($values['TEST_POSTGRES_USER']);" + `
  "Password=$($values['TEST_POSTGRES_PASSWORD'])"
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~ItemPolicyLifecycleIntegrationTests|FullyQualifiedName~ItemApiIntegrationTests"
Remove-Item Env:SHOOTER_MMO_TEST_POSTGRES
docker compose -f docker-compose.test.yml down
```

Expected result: eight tests pass. They verify insurance application and
removal, exact quest-grant cleanup and reaccept, catalog ETag and `304`, no-store
headers, owner scoping, Secure Container tier access, stable Problem Details,
offline session fencing, stable split, merge, and destroy validation, Recovery
deposit rejection, system delivery, successful bank claim, and hard-cap claim
rollback. The test host uses the real account
session authentication handler and item endpoints over loopback HTTP.

To inspect the account Phase 6 HTTP contract manually, start local PostgreSQL and
Redis with `docker compose up -d --wait`, run
`dotnet run --project AuthService`, and use the Phase 4 registration commands to
create `$headers` and `$character`. Then run:

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

No Unity Editor action is required for Phase 6 verification.

## Phase 7 Carry State And Shared Encumbrance Verification

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
$env:SHOOTER_MMO_TEST_POSTGRES = `
  "Host=127.0.0.1;Port=55432;" + `
  "Database=$($values['TEST_POSTGRES_DB']);" + `
  "Username=$($values['TEST_POSTGRES_USER']);" + `
  "Password=$($values['TEST_POSTGRES_PASSWORD'])"
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~PlayerMovementSimulationTests|FullyQualifiedName~CarryStateStoreTests|FullyQualifiedName~SimulationSessionReleaseServiceTests|FullyQualifiedName~AuthoritativePlayerMovementTests|FullyQualifiedName~RealtimeProtocolTests|FullyQualifiedName~RealtimeSimulationServiceTests|FullyQualifiedName~CarryStateIsFencedIntoJoinHeartbeatAndReconnect|FullyQualifiedName~CarryStateCountsEveryCarriedCustodyExactlyOnceAndExcludesExternalCustody|FullyQualifiedName~SwappingToLowerCapacityBagRejectsTheCompleteAggregateAboveHardCap"
Remove-Item Env:SHOOTER_MMO_TEST_POSTGRES
docker compose -f docker-compose.test.yml down
```

Expected result: every selected test passes. The suite verifies base capacity
`200`, equipped Bag capacity, exact custody contribution, exclusion of bank,
Recovery Storage, and corpse custody, lower-capacity Bag rollback, sprint at and
above 100 percent, the linear multiplier through `0.20` at 140 percent,
monotonic active-session propagation, protocol validation, and reconnect
restoration.

Run all Unity tests through the repository script:

```powershell
powershell -ExecutionPolicy Bypass -File Tools/Run-UnityTests.ps1
```

Expected result: all EditMode and PlayMode tests pass. The EditMode suite runs
the same shared GameSimulation reference points and verifies monotonic client
carry-state updates.

For a manual default-state and reconnect check:

1. Start PostgreSQL and Redis with `docker compose up -d --wait`.
2. Run `dotnet run --project AuthService` in one terminal.
3. Run `dotnet run --project SimulationWorker` in a second terminal. Confirm
   startup reports realtime protocol version `11` and simulation revision
   `movement-simulation-v3`.
4. Open `shooter-mmorpg-unity-client` in Unity `6000.5.2f1`, open LoginMenu,
   enter Play Mode, register or log in, select a character and shard, and join.
5. Press F2 in the active World scene. A new empty character shows weight
   `0 / 200`, movement `100%`, sprint allowed, and its committed item-state
   revision.
6. Leave to CharacterSelect and join the same character again. The carry tuple
   and revision are restored and movement remains available.
7. Exit Play Mode and stop both backend processes and Compose services.

No Inspector, scene, prefab, package, input-action, or build-setting change is
required for Phase 7.

## Phase 8 In-World Mutation Boundary Verification

Run the Phase 8 protocol, worker, HTTP client, and isolated PostgreSQL coverage:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$values = @{}
Get-Content .env | ForEach-Object {
  if ($_ -match '^([^#=]+)=(.*)$') {
    $values[$matches[1]] = $matches[2]
  }
}
$env:SHOOTER_MMO_TEST_POSTGRES = `
  "Host=127.0.0.1;Port=55432;" + `
  "Database=$($values['TEST_POSTGRES_DB']);" + `
  "Username=$($values['TEST_POSTGRES_USER']);" + `
  "Password=$($values['TEST_POSTGRES_PASSWORD'])"
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~SimulationItemMutationIntegrationTests|FullyQualifiedName~DuplicateReliableItemIntentsReplayOneCommittedCarryRevision|FullyQualifiedName~ItemOperationIntentsRoundTripEveryPhaseEightMutationKind|FullyQualifiedName~ItemOperationResultRoundTripsCommittedRevisionsAndRefreshSignal|FullyQualifiedName~ItemOperationIntentRejectsUnboundedRecoveryClaims|FullyQualifiedName~SimulationItemMutationSendsExactLiveAuthorityAndValidatesCommittedCarry|FullyQualifiedName~SimulationWorkerRejectsInvalidItemServicePoints"
Remove-Item Env:SHOOTER_MMO_TEST_POSTGRES
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
powershell -ExecutionPolicy Bypass -File Tools/Run-UnityTests.ps1
```

Expected result: all EditMode and PlayMode tests pass. Phase 8 EditMode tests
round-trip typed item intents and committed results through the same protocol
source compiled by the backend and Unity.

For a manual runtime smoke check:

1. Start PostgreSQL and Redis with `docker compose up -d --wait`.
2. Run `dotnet run --project AuthService` in one terminal.
3. Run `dotnet run --project SimulationWorker` in a second terminal. Confirm it
   reports realtime protocol version `11` and loads the configured local bank,
   Recovery Storage, and insurance NPC service points without a configuration
   error.
4. Open `shooter-mmorpg-unity-client` in Unity `6000.5.2f1`, open LoginMenu,
   enter Play Mode, log in, select a character and shard, and join the mapped
   development World scene.
5. Press F2 and confirm movement and the admitted carry tuple remain available.
   This step verifies the Phase 8 boundary. The current Phase 9 build also has
   the inventory panel documented below.
6. Leave and reconnect. Confirm the same committed carry revision is restored.
7. Exit Play Mode and stop both backend processes and Compose services.

No Inspector, scene, prefab, package, input-action, or build-setting changes are
required for Phase 8. No manual Unity Editor setup is required.

## Phase 9 Unity Inventory Foundation Verification

Run the dedicated fixture and client-foundation coverage against the isolated
test database:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$env:SHOOTER_MMO_TEST_POSTGRES = `
  (Get-Content .env | Where-Object {
    $_ -like "SHOOTER_MMO_TEST_POSTGRES=*"
  }).Split("=", 2)[1]
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~PhaseNineDevelopmentFixture|FullyQualifiedName~DevelopmentItemTool|FullyQualifiedName~ItemReadModelIntegrationTests"
Remove-Item Env:SHOOTER_MMO_TEST_POSTGRES
docker compose -f docker-compose.test.yml down
powershell -ExecutionPolicy Bypass -File Tools/Run-UnityTests.ps1
```

Expected result: every selected backend test passes, including one real
PostgreSQL transaction-service fixture flow, Editor-tool list and grant flows,
exact 140 percent package weight, hard-cap rejection, readback, Recovery
revision, and replay refusal. Both Unity suites pass. Phase 9 EditMode coverage
verifies the shared Editor menu root and machine-readable tool response, the
presentation catalog cache, catalog mismatch, complete and focused snapshots,
monotonic and divergent revisions, operation correlation, specialized targets,
Secure Container eligibility, non-empty Bag rejection, split weight, and the
140 percent hard cap, plus typed valid and invalid drag-and-drop behavior.
PlayMode verifies the persistent controller and runtime uGUI root.

### Give A Character Development Items

The Unity Editor window is the primary workflow. It never writes directly to
PostgreSQL. It starts a guarded AuthService command that uses
`ItemTransactionService`, accepts only Development, requires a loopback
PostgreSQL host with a non-production-like database name, and refuses an active
character. Individual grants are repeatable. Exact test packages require a
character with no items or Recovery deliveries.

The offline requirement protects live authority. The Editor command commits
directly through AuthService and does not have a SimulationWorker result path to
advance an already joined session's carry tuple and item-state revision. Simply
removing the guard could leave the worker and Unity on stale live state. A safe
online grant tool must be a separate service-authenticated development intent
routed through the worker that currently owns the character.

1. Build Release once, then start normal local infrastructure and AuthService:

   ```powershell
   dotnet build ShooterMmo.slnx --configuration Release
   docker compose up -d --wait
   dotnet run --project AuthService
   ```

2. Open LoginMenu in Unity, enter Play Mode, register a disposable local account,
   and create a new character. Do not join a shard. Exit Play Mode. AuthService
   may remain running.
3. Select `Shooter MMO > Tools > Inventory Item Grants` from Unity's top menu.
   The window refreshes automatically. Select the new offline character. The
   row shows account, online state, item count, weight, and capacity.
4. For a custom grant, choose an item definition, quantity, and Permanent
   Inventory, Bank, or Secure Container destination. Click `Give Item`.
   Expected result: the status reports the committed grant and the character row
   immediately shows the updated item count, weight, capacity, and revision.
5. For a complete scenario, choose a package and click `Give Package`. Available
   packages are:

   - Phase 9 Full Test Pack
   - Equipment Test Pack
   - Stack Split And Merge Pack
   - Encumbrance 100% Pack
   - Encumbrance 140% Pack
   - Secure Container Pack
   - Recovery Delivery Pack

   Expected result: an empty offline character receives the selected
   deterministic state. The full Phase 9 pack reports weight `122 / 250`. The
   encumbrance packs report `200 / 200` and `280 / 200`. Package controls become
   unavailable once that character owns items, while individual grants remain
   available. Invalid stack, Secure Container, slot, or hard-cap requests are
   rejected by AuthService without a partial item transaction.
6. To create a durable corpse, keep a source character offline and use the
   `Create Phase 11 Corpse` section. Leave Shard Id as `local-shard-1`, enter a
   nearby safe World position such as `(0, 0, -1)`, and click
   `Create Durable Corpse`. An empty source is seeded automatically with the
   Phase 9 package. A non-empty source uses its current item state. A source with
   only pending Recovery deliveries cannot be auto-seeded.

   Expected result: the status reports the corpse id, exact Shard, absolute
   five-minute expiry, and committed item-state revision. Restart
   SimulationWorker after creation so its fresh runtime restores the corpse.
7. Start SimulationWorker with its Development launch profile, enter Play Mode,
   log into the target account, select its character, and join the local shard:

   ```powershell
   dotnet run --project SimulationWorker
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
uses the same guardrails and transaction service:

```powershell
dotnet run --project AuthService --configuration Release --no-build -- `
  --dev-items-list

dotnet run --project AuthService --configuration Release --no-build -- `
  --dev-items-grant <characterId> material.iron_ore 20 bank

dotnet run --project AuthService --configuration Release --no-build -- `
  --dev-items-package <characterId> phase9_full

dotnet run --project AuthService --configuration Release --no-build -- `
  --dev-items-corpse <characterId> local-shard-1 0 0 -1
```

The original full-fixture command remains compatible:

```powershell
dotnet run --project AuthService -- --seed-phase9-items <characterId>
```

Expected result: the process exits after logging 17 granted item instances, one
Recovery delivery, the committed item-state revision, and carry `122/250`.
Running it again fails with the empty-character guard instead of duplicating
items.

### Manual Player Loop

1. Press `B` and verify only Permanent inventory, the equipped Field Pack
   contents, and Secure Container are shown. Press `C` and verify equipment plus
   character storage are shown. Press `I` and verify the complete Development
   view adds the upper-right Bank and Recovery context. Pressing the active key
   closes that view, pressing another inventory key switches views, and
   `Escape` closes any view and restores shooter pointer capture. Verify that
   hidden modules leave no background or header behind and that Character
   Inventory keeps the same position and size in all three views.
2. Drag the rifle, vest, pickaxe, or ring from Permanent inventory onto a
   compatible empty equipment slot. Drag the equipped item onto an empty
   Permanent slot to unequip it. Valid targets highlight green, invalid targets
   highlight red, and the UI waits for the committed refresh before showing the
   new location. The header drops by the equipped item's own weight and restores
   it when the item is unequipped into carried storage. Clicking a destination
   without dragging does not move an item.
3. Drag the empty Field Pack from Permanent inventory into one of the equipped
   Bag's general slots and back. Open Bank and drag the second empty Field Pack
   between Bank and Permanent inventory. Click the equipped non-empty Field Pack
   only to inspect its actions, then drag it and confirm ordinary Permanent, Bag,
   and Bank destinations reject it.
4. Drag the field dressing, iron ore, and ammunition out of and back into the
   medical, material, and ammunition specialized slots. Drag the rifle across a
   specialized slot and Secure Container target and confirm both reject it.
5. In Bank, click an iron-ore stack, enter the split quantity, enable
   `Split to Target`, then drag that selected stack onto an empty Bank slot. Drag
   the resulting stack onto its compatible peer to merge it back. Confirm source
   quantities and item identities change only after the server result and
   refresh. Drag two unlike items between compatible occupied ordinary slots and
   confirm they exchange slots in one committed refresh. Repeat against an
   incompatible specialized or Secure Container slot and confirm neither moves.
6. In Recovery, drag either item in the prepared delivery onto a Permanent or
   Bank slot. The complete delivery disappears and both original item instances
   appear in the chosen container only after the committed full refresh.
7. Starting from fixture weight `122 / 250`, move Bank iron ore quantity `20` to
   Permanent inventory. Weight becomes `242 / 250`. Move Bank iron ore quantity
   `16` to reach `338 / 250`, where sprint is blocked and the movement multiplier
   begins its linear decline. Move the single field dressing to reach
   `340 / 250`, followed by the empty Bank Field Pack to reach exactly
   `350 / 250`, load `140%`, and movement `20%`.
8. Attempt to move the single Bank ammunition item into carried storage. The
   local target is disabled. Any equivalent authoritative request is rejected by
   the server, and the committed weight remains exactly `350 / 250`.
9. Leave to CharacterSelect and rejoin. Press `I` and confirm every item location,
   quantity, carry value, and revision is restored without loss or duplication.
10. Reopen and refresh Bank and Recovery repeatedly. Definition labels and icon
    references remain stable. Automated EditMode coverage also asserts that both
    catalog loads return the same presentation index and icon cache.

The checked-in `SimulationWorker/Config/appsettings.Development.json` sets
`DevelopmentItemInteractions:GlobalBankAndRecoveryAccess=true`. This is a local
testing capability, not an AuthService or protocol bypass. If the same setting is
enabled outside the Development environment, SimulationWorker refuses startup.
Production continues to require authored Bank and Recovery service-point
proximity, and insurance access always remains proximity based.

The catalog-update path is covered by
`MapperRejectsCatalogMismatchAndInvalidDeliveryRevision`: a mismatched server
revision is refused and the controller presents `item_catalog_update_required`
instead of rendering item slots.

World-loot keeps its reserved context identity. Corpse inspection, concurrent
loot, partial stacks, and atomic equipped-Bag swaps are implemented by Phase 11
and use the separate verification flow below.

## Phase 10 Death And Durable Corpse Verification

Phase 10 has no fabricated combat or Unity death flow. Verify its complete
durable boundary against the isolated PostgreSQL database:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$env:SHOOTER_MMO_TEST_POSTGRES = `
  (Get-Content .env | Where-Object {
    $_ -like "SHOOTER_MMO_TEST_POSTGRES=*"
  }).Split("=", 2)[1]
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~DeathLootIntegrationTests|FullyQualifiedName~DurableCorpseStoreTests|FullyQualifiedName~AuthServiceClientTests"
Remove-Item Env:SHOOTER_MMO_TEST_POSTGRES
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

No manual Unity Editor steps are required for Phase 10. Do not change a scene,
prefab, Inspector property, input action, package, or build setting. Phase 11
uses the prepared exact-session death boundary for its Development corpse
fixture without fabricating the future combat producer.

## Phase 11 Concurrent Corpse Container Verification

Run the complete Phase 11 backend coverage against the isolated PostgreSQL
database, then run both Unity suites:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$env:SHOOTER_MMO_TEST_POSTGRES = `
  (Get-Content .env | Where-Object {
    $_ -like "SHOOTER_MMO_TEST_POSTGRES=*"
  }).Split("=", 2)[1]
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~CorpseLootConcurrencyIntegrationTests|FullyQualifiedName~CorpseInteractionEndpointsEnforceAuthorityCommitAndCloseTransactions|FullyQualifiedName~CorpseFixtureSeedsEmptyCharacterAndUsesDurableDeathPipeline|FullyQualifiedName~RealtimeCorpseProtocolTests|FullyQualifiedName~CorpseRealtimeStateTests|FullyQualifiedName~SimulationCorpseInteractionServiceTests"
Remove-Item Env:SHOOTER_MMO_TEST_POSTGRES
docker compose -f docker-compose.test.yml down
powershell -ExecutionPolicy Bypass -File Tools/Run-UnityTests.ps1
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
   `Development Build`, and build the existing scene list. Do not add or reorder
   scenes. Expected result: the standalone player reaches LoginMenu.
2. Start PostgreSQL, Redis, and AuthService. Using the Editor client, create
   three disposable accounts with one character each: one offline corpse source
   and two looters. Apply `Phase 9 Full Test Pack` to both looters while they are
   offline so each has an occupied equipped Bag.
3. In `Shooter MMO > Tools > Inventory Item Grants`, select the offline source.
   Under `Create Phase 11 Corpse`, keep Shard Id `local-shard-1`, use World
   Position `(0, 0, -1)`, and click `Create Durable Corpse`. Expected result:
   the tool reports a corpse id and an expiry five minutes in the future.
4. Start or restart SimulationWorker after the fixture commits. Run the built
   player and enter Play Mode in the Editor. Log the two looters into their
   separate accounts and join `local-shard-1`. Both spawn beside the generic
   corpse capsule.
5. Press `E` in both clients. Expected result: each opens the same complete
   three-section corpse view. The character inventory module stays fixed while
   the Corpse context module opens.
6. Drag two different corpse items into valid empty carried slots at nearly the
   same time. Expected result: both commits appear in both views with no lost or
   duplicated item. Then drag the same remaining item simultaneously. Exactly
   one client commits, and the loser receives a stable error followed by current
   authoritative state.
7. For a stack, click `Partial Stack`, enter a quantity, and drag it to a valid
   destination from one client while the other client loots from the same stack.
   Expected result: committed quantities never become negative and their total
   is conserved.
8. Drag a carried item into an empty corpse slot. Then drag two complete items
   that cannot merge onto each other in both directions. Expected result: the
   deposit appears for both viewers, each valid occupied drop swaps atomically,
   and Character Inventory refreshes without closing the corpse. Repeat with a
   partial compatible stack deposit and verify total quantity is conserved.
9. Drag a corpse item to another empty corpse slot, split one corpse stack into
   another corpse slot, merge compatible corpse stacks, and drop two complete
   non-mergeable corpse items onto each other. Expected result: every operation
   commits as corpse-only movement, both clients receive the result, and the
   character weight and item-state revision do not change. Verify that each
   Corpse Equipment button shows a type such as `Head [head]` or
   `Body Armour [body_armor]`. A compatible item is accepted and an incompatible
   item is rejected without moving either item.
10. In one client, drag a corpse Bag child. At the same time in the other client,
   drag the corpse Bag root onto the occupied player Bag equipment slot.
   Expected result: only a compatible complete outcome commits. Neither Bag
   aggregate is split, and both clients converge after the broadcast delta or
   refresh.
11. Create a fresh fixture and restart SimulationWorker before its five-minute
   deadline. Rejoin the Shard. Expected result: the corpse returns at
   `(0, 0, -1)` with the original absolute expiry. It disappears and closes any
   open view at expiry rather than receiving a new five-minute lifetime.

The future combat system is not required for this test. The fixture enters the
same durable death, restore, interaction, transaction, and expiry boundaries
that combat must call later.

## Phase 12 World Actor And Interaction Verification

Compile or verify the canonical actor content from any repository directory:

```powershell
dotnet run --project Tools/WorldActorCompiler --configuration Release
dotnet run --project Tools/WorldActorCompiler `
  --configuration Release `
  --no-build -- `
  --verify
dotnet run --project Tools/WorldActorCompiler `
  --configuration Release `
  --no-build -- `
  WorldData/Shared/Authoring/Actors/core.world-actors.json `
  WorldData/Worlds/development-world-2/Authoring/actor-spawns.json `
  WorldData/Worlds/development-world-2/Runtime/world-actors.json `
  --verify
```

Expected result: the first two commands report World `development-world-1`
with four definitions and five runtime instances. The third reports
`development-world-2` with four definitions and seven runtime instances. Verify
mode performs no write and fails if authoring, runtime JSON, references, bounds,
transforms, fingerprints, ordering, or the complete revision diverge.

Run the focused backend actor and protocol coverage, then the complete Unity
workflow:

```powershell
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~WorldActor|FullyQualifiedName~RealtimeWorldActor"
powershell -ExecutionPolicy Bypass -File Tools/Run-UnityTests.ps1
```

Expected result: every selected backend test and every Unity EditMode and
PlayMode test passes. The coverage includes deterministic compilation, strict
runtime validation, identity scopes, assignment reconstruction, reliable
interest presence, central Mob scheduling, composable capabilities, typed
deferred dispatch, range, line of sight, rate, revision, session and shared
corpse-lease authority, protocol bounds, monotonic Unity state, targeting, and
Editor compiler integration.

### Manual Actor Authoring And Interaction Loop

Existing canonical content and fallback presentation require no scene, prefab,
Inspector, package, or build-setting change. Use these exact Unity Editor steps
to exercise the authoring tools and optional presentation mapping:

1. Open the Unity project and select `Shooter MMO > Tools > Content > Actor
   Studio`. Select `Mira the Quartermaster`. Expected result: the window shows
   an NPC with dialogue, vendor, quest offer, quest turn-in, crafting,
   insurance, bank, and Recovery Storage capabilities, an invulnerable damage
   policy, and authoritative interaction bounds.
2. Click `Duplicate Selected`, give the copy a globally unique test actor id,
   and use `Add Capability`, `Move Up`, `Move Down`, and `Remove Capability`.
   Click `Preview Changes`. Expected result: inline validation and the exact
   canonical JSON change appear without writing files. Click `Reload` to discard
   the disposable copy unless it is intended as real content.
3. Select `Feral Wolf`. Expected result: the same tool edits a Mob definition
   with damageable policy plus activity and respawn profile references, without
   adding a Mob runtime subclass.
4. To use authored prefabs, click `Create Presentation Registry`, then assign a
   prefab in `Replaceable prefab` for each presentation archetype. Expected
   result: Unity creates
   `Assets/Resources/ShooterMmo/WorldActors/WorldActorPresentationRegistry.asset`.
   The prefab changes presentation only. This optional asset creation and prefab
   assignment are the only manual Unity asset steps.
5. Click `Save + Compile`, then `Verify Canonical`. Expected result: canonical
   authoring and runtime JSON save deterministically and verification reports
   the same revision. If no intentional content change was made, the files stay
   byte-for-byte equivalent.
6. Open `Shooter MMO > Tools > Content > Spawn Authoring` and click `Import`.
   Inspect the Points, Groups, Areas, and Patrols tabs with `Scene Preview`
   enabled. Expected result: scene handles show the service NPC point, two-guard
   group, two-wolf seeded area, and wolf patrol path imported from neutral
   WorldData.
7. Add or duplicate a disposable entry, move it with the scene handle, click
   `Ground Snap`, and inspect `Preview Changes`. Delete the disposable entry,
   then click `Export + Compile` and `Verify Canonical`. Expected result: the
   preview shows exact neutral JSON, ground snapping uses authored scene
   collision, and clean export verifies deterministically.
8. Start AuthService and SimulationWorker, join `local-shard-1`, and approach
   Mira. Expected result: actor instances appear and disappear through normal
   interest presence. The worker startup log reports the actor content revision,
   four definitions, and five instances.
9. Point the crosshair at Mira from at most `3.0` metres and press `E`. Expected
   result: temporary uGUI opens only after the server accepts and lists the
   player-specific authoritative capability summary. Clicking a capability
   reports `world_interaction_capability_deferred`, never local success.
10. Repeat from beyond `3.0` metres and with static collision blocking line of
    sight. Expected result: the server rejects the request and no interaction
    remains open. Open within range, move through `3.5` metres, and verify the
    server closes only that session. Pressing `E` also closes an active session.
11. Join with two clients and open Mira independently. Expected result: both
    sessions coexist. One client cannot open a corpse while its NPC interaction
    is active. After closing it with `E`, target a corpse and verify the existing
    Phase 11 inspection, drag-and-drop, and authoritative mutation flow is
    unchanged.
12. Restart SimulationWorker and reconnect. Expected result: actors reconstruct
    from compiled WorldData with fresh runtime identities, normal interest
    presence returns, and no stale actor or interaction session survives.

Ordinary NPCs remain event-driven. The Feral Wolf changes only between central
dormant and active scheduling buckets based on player distance. No actor creates
a durable instance row, timer, task, database session, or HTTP poller.

Run the deterministic realtime scalability workload separately when changing
interest selection, snapshot encoding, or quota code:

```powershell
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
  --configuration Release `
  --filter "Category=Load"
```

Expected result: 25,000 entities and 1,000 observers complete spatial selection
and snapshot encode/decode inside the five-second regression budget.

Run the external headless stack stress flow before accepting a new capacity
baseline or after changing the realtime loop, collision, interest, snapshot,
session-heartbeat, account, placement, or database behavior. Worker-only mode
uses in-memory identities to isolate SimulationWorker. Full-stack mode drives
normal AuthService APIs against a guarded disposable PostgreSQL database.
Complete commands, expected results, measurements, safety checks, and cleanup
are documented in [Stack Stress Testing](STACK_STRESS_TESTING.md).

Use `Tools/ActiveSimulationBots` when a long-running synthetic population must
share the normal AuthService, SimulationWorker, and shard with a Unity player.
Its account-login bypass is Development-only and in memory, while every bot
still uses an exact-runtime one-time ticket and the complete UDP join, input,
snapshot, and leave flow. Configuration, startup commands, expected behavior,
capacity reservation, Ctrl+C cleanup, and the visual Unity test are documented
in [Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md).

## Phase 13 Insurance And Quest Item Lifecycle

Phase 13 uses Mira the Quartermaster and the existing Phase 12 world interaction
session. No scene object, inspector assignment, or new Unity asset is required.
The default AuthService lifecycle profile charges `100` currency for one-death
insurance and grants one `quest_item.signal_transponder` with lineage
`quest.local.signal_transponder`.

Prepare a local character while it is offline:

1. Open `Shooter MMO > Tools > Inventory Item Grants` and grant
   `weapon.training_rifle` into a free Permanent Inventory slot.
2. Give the character local development currency. With the default Compose
   database values, replace the character name and run:

```powershell
docker exec shooter_mmo_postgres psql -U shooter_mmo -d shooter_mmo -c `
  "update characters set currency = 500 where name = 'YOUR CHARACTER';"
```

Expected result: PostgreSQL reports `UPDATE 1`. If local database names or users
differ, use the matching values from `.env`.

Start AuthService and SimulationWorker, join `local-shard-1`, approach Mira
within `3.0` metres, point the crosshair at her, and press `E`.

Expected result:

- The existing authoritative interaction opens and lists Insurance plus Quest
  item lifecycle actions.
- Applying insurance refreshes the same item with `Insured | source: Insurance
  NPC`. The server charges the configured price atomically and rejects a second
  active policy.
- Explicit removal keeps the item instance and removes the insured label.
- Accepting the quest creates one protected Signal Transponder with source
  `Quest grant`. Repeated acceptance does not create a duplicate.
- Generic destroy rejects the protected quest item. NPC abandonment removes only
  that grant lineage, and reacceptance creates exactly one new required item.
- Moving beyond `3.5` metres or losing line of sight still closes the interaction
  through the Phase 12 authority path.

Recovery Storage headings label `death_recovered_insured` as consumed insurance
and `death_recovered_protected` as protected-on-death. Phase 13 intentionally has
no Mob combat or other new death producer. Use the existing backend death
fixture to verify first-death consumption and replay idempotency.

No manual Unity Editor setup is required for Phase 13. Editor actions are needed
only for the optional offline item grant used by this manual workflow.

## Phase 14 Mob Corpse Variants

Phase 14 reuses the existing corpse presence, view, drag, inventory refresh, and
generic loot-crate presentation. It adds no scene object, prefab, inspector
reference, or protocol version. Mob definitions own the persistence choice and
lifetime:

- `mob.feral_wolf`: `live`, `120` seconds.
- `mob.feral_alpha`: `durable`, `600` seconds.

Verify the content manually:

1. Open `Shooter MMO > Tools > Content > Actor Studio`.
2. Select `mob.feral_wolf`. Confirm `Corpse Persistence` is `live` and
   `Corpse Lifetime Seconds` is `120`.
3. Select `mob.feral_alpha`. Confirm persistence is `durable` and lifetime is
   `600`.
4. Select `npc.city_guard` or `npc.city_services`. Confirm NPC content does not
   carry Mob corpse settings.
5. Select `Validate`, `Compile`, and `Verify`.

Expected result: every action succeeds, there are four actor definitions and
five runtime instances, and the checked-in actor runtime revision is
`69ee1d2fdf104ea5600d5011c6a0ce0cb801faddc116715898429ea5f4c70916`.

Run the worker-memory lifecycle workflow:

```powershell
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
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
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
  --configuration Release `
  --no-build `
  --filter "FullyQualifiedName~MobCorpseLifecycleIntegrationTests"
```

Expected result: both integration tests pass. Retrying one live Mob loot grant
creates one persistent player item. A durable alpha corpse restores into a new
worker store with the same database deadline, three-section custody, and loot.

No manual Unity gameplay setup is required. There is intentionally no killable
Mob fixture in this phase. `MobCorpseLifecycleService` consumes an authoritative
death event and already resolved loot seeds, but combat, damage, death detection,
loot-table generation, and respawn remain future producers.

## Phase 15 Performance And Operations Hardening

Phase 15 keeps the existing item, corpse, actor, capability, interaction, and
authority graph. Checked-in AuthService defaults live under `Items:Operations`:

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
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
  --configuration Release `
  --no-build `
  --filter "FullyQualifiedName~ItemOperationsHardeningTests|FullyQualifiedName~ItemOperationsHardeningIntegrationTests"
```

Expected result: all focused tests pass. A blocked character mutation returns
`item_transaction_timeout`; an oversized canonical command returns
`item_command_payload_too_large` before database access; expired Recovery items
are destroyed exactly once with `recovery_expired`; and policy-eligible empty
closed corpses plus unreferenced old operation rows are removed without deleting
durable destruction evidence.

Run the actor and transaction hotspot scenarios:

```powershell
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
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

During local service operation, AuthService logs a periodic item summary with
transaction, contention, lifecycle, corpse, Recovery, and cleanup counts.
Significant item transactions carry a bounded correlation id and operation id.
SimulationWorker status distinguishes actor tiers and live versus durable corpse
counts. Credentials, session tokens, and command bodies must not appear.

No manual Unity Editor scene, prefab, inspector, package, input, or build-setting
steps are required for Phase 15. Run `Tools/Run-UnityTests.ps1` as regression
coverage and expect all EditMode and PlayMode tests to pass unchanged.

## Isolated PostgreSQL Integration Tests

The integration test resets the target database's `public` schema. Always use the
isolated test Compose file and never point the test variable at a development,
staging, or production database.

Start the test database:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
```

Set the dedicated connection and run all backend tests:

```powershell
$testConnectionLine = Get-Content .env |
  Where-Object { $_ -like "SHOOTER_MMO_TEST_POSTGRES=*" } |
  Select-Object -First 1
$env:SHOOTER_MMO_TEST_POSTGRES = $testConnectionLine.Split("=", 2)[1]
dotnet test ShooterMmo.slnx --configuration Release
```

Expected result:

- All unit tests pass.
- PostgreSQL migration concurrency, ticket concurrency, wrong-worker protection,
  reconnect, heartbeat, cross-shard and cross-character exclusion, assignment
  failover, and idempotent release tests pass
  instead of being skipped.

Clean up the isolated environment:

```powershell
docker compose -f docker-compose.test.yml down
Remove-Item Env:SHOOTER_MMO_TEST_POSTGRES
```

## Local Infrastructure

Start PostgreSQL and Redis locally:

```powershell
docker compose up -d
```

The local services use these ports:

- PostgreSQL: `localhost:5432`
- Redis: `127.0.0.1:6379`
- AuthService: `http://localhost:5000`
- SimulationWorker realtime transport: `0.0.0.0:27015/udp`
- SimulationWorker advertised client endpoint: `127.0.0.1:27015/udp`

Local PostgreSQL credentials and SimulationWorker service credentials live only in
the ignored `.env` file. `.env.example` documents every required key without
placing active credentials in application settings or Compose YAML.
`SIMULATION_WORKER_ADVERTISED_HOST` and
`SIMULATION_WORKER_ADVERTISED_UDP_PORT` override the client-facing endpoint
without changing the local bind port.

## Backend Services

Run AuthService in the first terminal:

```powershell
dotnet run --project AuthService
```

Check AuthService liveness and readiness:

```powershell
Invoke-RestMethod http://localhost:5000/health/live
Invoke-RestMethod http://localhost:5000/health/ready
```

Expected result: liveness reports `live`. Readiness reports `ready` only after a
real PostgreSQL `select 1` query and Redis `PING` both succeed.

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
dotnet run --project SimulationWorker
```

SimulationWorker is a headless .NET Generic Host. It does not expose HTTP routes.
A successful start logs worker `local-simulation-worker-1`, fleet `local-fleet`,
node `local-node-1`, shard `local-shard-1`, World `development-world-2`, UDP port
`27015`, runtime id, realtime protocol version 14, simulation revision,
collision revision, world-actor revision and population, and loaded collision
chunks. Every 30 seconds it also logs aggregate
realtime packet, byte, entity, peer, quota, and snapshot counters. The same
interval logs a worker status line with connected real players, synthetic bots,
unauthenticated peers, CPU, working set, packet and payload rates, snapshot
drops, and quota rejections. Detailed tick and phase timing remains in the
server-side performance line.

Routine `HttpClient` request-start, request-send, and successful-response logs
for `AuthServiceClient` are filtered below `Warning`. This keeps bot heartbeat
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
dotnet run --project SimulationWorker -- --health-check-only
```

Expected result: PostgreSQL is verified through AuthService readiness, Redis is
verified directly with `PING`, collision data is loaded and checksum-validated,
UDP port availability is checked, and the process exits with code `0`. Stop
Redis, stop AuthService, corrupt a collision chunk, or occupy UDP port 27015 and
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

1. Start AuthService without SimulationWorker and call `GET /api/shards`.
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
socket tests verify join, leave, and the reliable entity lifecycle with:

```powershell
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
  --filter "FullyQualifiedName~RealtimeSimulationServiceTests|FullyQualifiedName~RealtimeEntityLifecycleTests"
```

Expected result: the authenticated client joins, moves, and leaves with exact
session cleanup. The two-client lifecycle test also proves that an existing peer
receives reliable ordered spawn and despawn for the other player.

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

## Unity Client Flow

The Unity project includes temporary runtime UI for the current backend flow.
Only this UI presentation is intentionally temporary. The runtime bootstrap,
session handling, API layer, scene lifecycle, input, camera, and gameplay systems
are maintained as long-term foundations. The UI is created automatically when
each scene starts. The local player and test map must be authored as Unity assets
and are not generated at runtime.

## One-Time Unity World Scene Authoring

The code foundation expects real scene and prefab assets. Complete these manual
steps in Unity Editor after the scripts compile.

### Create The Input Actions Asset

1. Create the folder `Assets/Input`.
2. In that folder, select `Create > Input Actions` and name the asset
   `PlayerControls`.
3. Open it and create an action map named `Player`.
4. Add these actions and bindings:
   - `Move`: Value, Vector2. Add a 2D Vector composite with WASD.
   - `Sprint`: Button. Bind left Shift.
   - `Jump`: Button. Bind Space.
   - `Aim`: Button. Bind `<Mouse>/rightButton`.
   - `Look`: Value, Vector2. Bind `<Mouse>/delta`.
   - `ToggleDebugCursor`: Button. Bind `<Keyboard>/f1`.
   - `ToggleWorldDebug`: Button. Bind `<Keyboard>/f2`.
5. Click `Save Asset`.

Expected result: `PlayerControls.inputactions` contains one `Player` map and the
seven actions with no missing bindings.

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
   - Grounded Vertical Velocity: `-2`
7. Add a child named `CameraTarget` at local position `0, 1.45, 0` and assign it
   to the LocalPlayerController Camera Target field.
8. Add a child named `LocalPlayerCamera` directly under LocalPlayer. Tag it
   `MainCamera` and add Camera, AudioListener, and ThirdPersonCameraController.
   Configure:
   - Camera Near Clip Plane: `0.05`
   - Camera Far Clip Plane: `500`
   - Shoulder Offset: `1.1`
   - Aim Shoulder Offset: `1.3`
   - Vertical Offset: `0.45`
   - Aim Vertical Offset: `0.35`
   - Distance: `5.25`
   - Initial Pitch: `12`
   - Min Pitch: `-50`
   - Max Pitch: `75`
   - Normal Field Of View: `60`
   - Aim Field Of View: `50`
   - Aim Transition Sharpness: `12`
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
6. Select `Shooter MMO > Tools > World Collision > Bake Open Scene` from Unity's top
   menu.
7. Wait for asset import and script compilation to complete.

After both development scenes exist in Build Profiles, use `Shooter MMO >
Tools > World Collision > Bake Build World Scenes` to validate each catalog
mapping and bake every enabled World scene in one deterministic pass.

Expected result: Unity Console reports `[WORLD COLLISION] Baked world
'development-world-1'` with 13 boxes, four chunks, and a SHA-256 revision. The command
updates:

- `WorldData/Worlds/development-world-1/Authoring/collision.json`
- `WorldData/Worlds/development-world-1/Runtime/Resources/ShooterMmo/WorldCollision/development-world-1/manifest.json`
- Four `chunk_*.bytes` files in the same runtime directory

The bake stops with a red error if the collision root contains enabled,
non-trigger Collider types other than BoxCollider. This is intentional until a
later collision format adds terrain and triangle-mesh chunks.

Verify the checked-in bake from the repository root:

```powershell
dotnet run --project Tools/WorldCollisionCompiler -- `
  WorldData/Worlds/development-world-1/Authoring/collision.json `
  WorldData/Worlds/development-world-1/Runtime/Resources/ShooterMmo/WorldCollision/development-world-1 `
  --verify
dotnet run --project Tools/WorldCollisionCompiler -- `
  WorldData/Worlds/development-world-2/Authoring/collision.json `
  WorldData/Worlds/development-world-2/Runtime/Resources/ShooterMmo/WorldCollision/development-world-2 `
  --verify
```

Expected result: the commands report the same World revisions, four verified
chunks for Development World 1, and 256 verified chunks for Development World
2. Either command exits with code 1 if its authoring JSON and runtime data
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
$env:UNITY_EDITOR_PATH = "C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe"
& .\Tools\Run-UnityTests.ps1
```

Expected result:

- EditMode validates API parsing, client state, Input Actions, collision resource
  loading, prediction, reconciliation, remote interpolation, world-actor and
  interaction state, crosshair targeting, actor content tooling, both player
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
3. Open this project with Unity `6000.5.2f1` once and wait for package import and
   script compilation to finish without a licensing error.
4. Close the Editor, keep the signed-in Hub session available, and rerun
   `Tools/Run-UnityTests.ps1`.

Expected result: the script creates fresh EditMode and PlayMode XML files under
`TestResults/Unity` and reports both suites as passed. License exit code `198`
occurs before project tests start and is not a test failure.

The CI Unity job uses a Windows self-hosted runner because Unity requires an
installed and activated Editor. To enable it:

1. In the GitHub repository, open `Settings > Actions > Runners` and register a
   Windows x64 self-hosted runner.
2. Add the custom label `unity-6000.5.2f1` to that runner.
3. Install and activate Unity `6000.5.2f1` for the Windows account that runs the
   runner service.
4. Add repository variable `UNITY_CI_ENABLED` with value `true`.
5. If Unity is not installed in the default Unity Hub path, add repository
   variable `UNITY_EDITOR_PATH` containing the full path to `Unity.exe`.

Expected result: pushes and pull requests run both Unity suites, upload XML and
Unity logs as `unity-test-results`, and fail the Unity quality gate on compiler
or test errors. Until the runner and variable exist, the Unity job is shown as
skipped instead of waiting forever for an unavailable runner.

Before using the Unity client, start these services:

```powershell
docker compose up -d
dotnet run --project AuthService
dotnet run --project SimulationWorker
```

Then open `shooter-mmorpg-unity-client` in Unity and start from:

```text
Assets/Scenes/LoginMenu.unity
```

The networking packages are declared in `Packages/manifest.json`. If Unity was
open while they were first added, exit Play Mode and wait for package resolution
and script compilation to finish. If Unity still displays the old duplicate
`ShooterMmo.GameProtocol` assembly error after compilation, close and reopen the
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
Protocol version 8 also validates shard and World identity, the exact placement,
the compiled movement-simulation revision, the initial carry tuple,
server-assigned network entity ids, reliable carry-state updates, reliable item
operation intents and results, and reliable entity lifecycle messages.
Rebuild every standalone client after a protocol or simulation revision change.
Standalone build output belongs under ignored `ClientBuilds` or `Builds`
directories and must never be committed.

### Rebind An Offline Shard To Another World

Shard-to-World binding is intentionally changeable between worker process
generations. It is not a runtime map switch. Prepare all target World content
before changing a shard:

- Add `WorldData/Worlds/<WorldId>/world.json` with the stable identity, client
  scene, bounds, spawn, and item service points.
- Provide `Authoring/collision.json` and the checksummed collision runtime below
  that World directory.
- Provide `Authoring/actor-spawns.json` and `Runtime/world-actors.json` when the
  World uses actor content. Shared actor definitions remain under
  `WorldData/Shared/Authoring/Actors`.
- Add the authored Unity scene to the client build and map its `WorldId` in
  `Assets/Resources/Worlds/world-scene-catalog.json`.

Perform the rebind in this order:

1. Stop the shard's SimulationWorker cleanly with `Ctrl+C` and wait for its
   graceful offline registration to complete.
2. Confirm players have left and allow all open durable corpses for that shard
   to expire or close through their normal lifecycle. Do not rewrite World-local
   coordinates in PostgreSQL by hand.
3. Change only `SimulationWorker:WorldId` in
   `SimulationWorker/Config/appsettings.json` and verify that the selected World
   directory contains its manifest, collision, and actor runtime data.
4. Start SimulationWorker. Its first authenticated heartbeat proposes the
   configured World. AuthService locks the shard, verifies that the World exists
   in the database, checks the offline rebind blockers, and atomically persists
   the new binding with the worker assignment.
5. If the worker stops after AuthService returns
   `shard_world_rebind_blocked`, leave it stopped. The error lists active
   assignments, pending join tickets, active simulation sessions, and open
   corpses. Drain the reported state and start the worker again.
6. If AuthService returns `simulation_world_not_found`, correct the World
   manifest or configured `WorldId`, restart AuthService so it reconciles the
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
- `Auth Service` displays `http://localhost:5000`.
- Request timeout displays 10 seconds.
- Realtime timeout displays 10 seconds.

These values are stored in
`Assets/Resources/Config/ShooterMmoClientConfig.asset`. Create build-specific
variants or change the asset before building a client for another environment.
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
- You can move with `WASD`, sprint with `Shift`, jump with `Space`, control the
  camera continuously with the mouse, and hold the right mouse button to aim.
- A small unarmed crosshair dot appears at screen center.
- F1 releases or recaptures the debug cursor, and F2 hides or restores the
  scrollable bottom-left World Client Debug panel.
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
- `Back To Login` revokes the current AuthService session before clearing local
  client state.

Expected Unity Console sequence for a successful login and simulation join:

```text
[AUTH] Validating login credentials for username 'player_one'.
[AUTH] Account 'player_one' (<account-id>) logged in.
[CLIENT] Character 'Hero One' (<character-id>) is requesting access to shard 'local-shard-1'.
[AUTH] AuthService issued a short-lived join ticket for character 'Hero One' on shard 'local-shard-1'.
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

1. Restart AuthService so it applies the latest database migration, then start
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

Camera and input test:

1. Move with keyboard WASD, sprint with left Shift, and jump with Space.
2. Jump without sprinting, press Shift while airborne, and keep moving forward.
3. Begin sprinting on the ground, jump while holding Shift, then release and
   press Shift again before landing.
4. Move the mouse without holding a mouse button and test the full upward and
   downward look range.
5. Hold right mouse button and move sideways with A and D.
6. While holding right mouse button, hold Shift and press Space while moving.
7. Begin sprinting, then press and hold right mouse button.
8. Press F1, use the visible cursor, then press F1 again.
9. Press F2 twice to hide and restore the World Debug panel.
10. Walk with a wall between the camera target and desired camera position.

Expected result:

- Movement is supplied by the PlayerInput-owned Input Actions asset rather than
  direct device polling or runtime-created bindings.
- Sprint cannot begin in the air. A ground-started sprint continues through a
  jump only while Shift remains held.
- Falling and landing remain continuous with no visible forced downward snap.
- The LocalPlayer root and PlayerVisual both use local Y zero as the foot plane.
  CharacterBody aligns the controller contact envelope without moving visuals.
- Mouse look remains active without holding Aim.
- The camera can look substantially farther upward and downward without turning
  fully upside down.
- Aim turns the player toward the camera heading and supports strafing.
- Aim blocks sprint and jump. Entering Aim while sprinting immediately returns
  movement to walk speed.
- F1 releases pointer input without causing Aim, then restores captured shooter
  input.
- Camera focus stays at the single `CameraTarget` height without the previous
  duplicated vertical offset.
- Normal camera framing stays over the right shoulder.
- Normal camera distance is 4.75 meters. Aim smoothly tightens the shoulder
  offset, moves to 4.25 meters, and changes FOV from 60 to 45.
- The mouse wheel does not change camera distance.
- The camera moves in front of walls using a spherecast and returns to the desired
  distance when the obstruction clears.
- The unarmed crosshair dot is centered while the cursor is captured and hidden
  while the debug cursor is released.
- The World Client Debug panel stays in the bottom-left corner, scrolls when the
  viewport is small, and F2 controls its visibility.

Server-authoritative movement test:

1. Start PostgreSQL, Redis, AuthService, and SimulationWorker with the commands above.
2. Enter DevelopmentWorld1 through LoginMenu and CharacterSelect. Do not start
   directly from DevelopmentWorld1 for this test.
3. Confirm World Debug shows server-authoritative client prediction and an
   increasing observed server tick value.
4. Move, rotate, sprint, jump, release movement, and change direction sharply.
5. Walk into the four boundaries, CameraTestWall, LowCover, and HighCover.
6. Walk up and down Ramp, release all movement input while standing halfway up,
   wait for at least three seconds, then traverse Step01, Step02, and Step03.
7. Watch the Unity Console and SimulationWorker terminal while moving for at least
   30 seconds.
8. Stop SimulationWorker while the character is moving.

Expected result:

- Input remains responsive because the client predicts the same fixed-step rules
  used by SimulationWorker.
- Normal snapshots do not cause repeated large position snaps on flat ground,
  the ramp, or steps.
- Sprint cannot begin in the air and jump remains responsive even if the input
  batch containing its edge is duplicated.
- The server tick increases continuously and snapshots acknowledge input without
  protocol errors.
- If input packets stop for more than the configured 500 ms timeout, SimulationWorker
  neutralizes movement and action state instead of continuing the last command.
- The capsule stops at walls and cover, follows the walkable ramp, and climbs
  the configured step heights in both the predicted and authoritative state.
- Releasing movement input on Ramp leaves the character stationary, and the
  rendered feet remain aligned with the ramp surface while moving and idle.
- Step01, Step02, Step03, and short grounded drops transition smoothly instead
  of snapping the rendered player or camera directly to each discrete height.
- Jump takeoff, airborne falling, and landing from heights above the ground snap
  range remain responsive and are not delayed by step presentation smoothing.
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
5. Move each character while watching it from the other client.

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
- [Game Features](GAME_FEATURES.md)
