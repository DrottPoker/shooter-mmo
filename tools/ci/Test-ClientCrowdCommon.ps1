# Checks the helpers of tools/ClientCrowdCommon.ps1 that decide what tools/Measure-ClientCrowd.ps1
# measures and reports, without Unity or a stack: the settings of the bots, the Mob variant of a
# size, the requests of the crowd test, the load samples that prove a recording undisturbed, and
# the summary tables, whose standing table must stay the baseline's table.
#
# Exit codes: 0 every case holds, 1 a case failed.
$ErrorActionPreference = "Stop"
. (Join-Path (Split-Path -Parent $PSScriptRoot) "ClientCrowdCommon.ps1")

$root = Join-Path ([IO.Path]::GetTempPath()) ("client-crowd-common-test-" + [Guid]::NewGuid().ToString("N"))
$failures = @()

function Assert-Equal([string]$Case, $Expected, $Actual) {
    if ("$Expected" -ne "$Actual") {
        $script:failures += "${Case}: expected '$Expected', got '$Actual'."
    }
}

function Assert-True([string]$Case, [bool]$Condition) {
    if (-not $Condition) {
        $script:failures += "${Case}: does not hold."
    }
}

try {
    $null = New-Item -ItemType Directory -Force $root

    # Lists come as lists or, through powershell -File, as one text with commas.
    Assert-Equal "Sizes as a list" "100 200 400" ((ConvertTo-CrowdNumbers @("100", "200", "400") "-Bots" 1 900) -join " ")
    Assert-Equal "Sizes as one text" "100 200" ((ConvertTo-CrowdNumbers @("100,200") "-Bots" 1 900) -join " ")
    $refused = $null
    try { $null = ConvertTo-CrowdNumbers @("100,1000") "-Bots" 1 900 } catch { $refused = $_.Exception.Message }
    Assert-True "A size out of range" ("$refused" -match "'1000'")
    Assert-Equal "Every kind by default" "Standing Arrival Churn Mobs" ((ConvertTo-CrowdKinds @()) -join " ")
    Assert-Equal "Kinds in the order of a run" "Arrival Mobs" ((ConvertTo-CrowdKinds @("Mobs,Arrival")) -join " ")
    $refused = $null
    try { $null = ConvertTo-CrowdKinds @("Standing", "Crowd") } catch { $refused = $_.Exception.Message }
    Assert-True "An unknown kind" ("$refused" -match "'Crowd'")

    # A crowd that stays, and the churn of 200 slots between 120 and 200.
    $crowd = Get-CrowdBotSettings "Standing" 100
    Assert-Equal "Crowd bot minimum" 100 $crowd.MinimumActiveBots
    Assert-Equal "Crowd bot maximum" 100 $crowd.MaximumActiveBots
    Assert-Equal "Crowd bots stay" 86400 $crowd.MinimumOnlineSeconds
    $churn = Get-CrowdBotSettings "Churn" 200
    Assert-Equal "Churn minimum" 120 $churn.MinimumActiveBots
    Assert-Equal "Churn slots" 200 $churn.MaximumActiveBots
    Assert-Equal "Churn target interval" 10 $churn.PopulationChangeIntervalSeconds
    Assert-Equal "Churn online time" "30-60" "$($churn.MinimumOnlineSeconds)-$($churn.MaximumOnlineSeconds)"
    Assert-Equal "Churn seed" 1337 $churn.Seed
    $arguments = @(ConvertTo-BotArguments $crowd)
    Assert-Equal "Bot arguments" "--ActiveSimulationBots:MinimumActiveBots 100 --ActiveSimulationBots:MaximumActiveBots 100" (($arguments | Select-Object -First 4) -join " ")

    # The Mobs of a size are shared between the variant's areas.
    Assert-Equal "Mobs that divide" "25 25 25 25" ((Split-MobCount 100 4) -join " ")
    Assert-Equal "Mobs that do not divide" "2 2 2 1" ((Split-MobCount 7 4) -join " ")
    Assert-Equal "Fewer Mobs than areas" "1 1 0 0" ((Split-MobCount 2 4) -join " ")
    $template = [IO.File]::ReadAllText((Join-Path (Split-Path -Parent $PSScriptRoot) "load/client-crowd-mobs.json"))
    Assert-Equal "Mob actor" "mob.grey_wolf" (Get-MobVariantActor $template)
    $variant = New-MobVariantScenario $template 100 | ConvertFrom-Json
    Assert-Equal "A variant of 100 Mobs" "25 25 25 25" ((@($variant.worldVariant.areas) | ForEach-Object { $_.count }) -join " ")
    Assert-True "A variant keeps the patrols" (@($variant.worldVariant.areas | Where-Object { @($_.patrol).Count -lt 2 }).Count -eq 0)
    $small = New-MobVariantScenario $template 2 | ConvertFrom-Json
    Assert-Equal "A variant leaves out an area without Mobs" "1 1" ((@($small.worldVariant.areas) | ForEach-Object { $_.count }) -join " ")

    # A request counts until the script answers it.
    $requests = Join-Path $root "requests"
    $null = New-Item -ItemType Directory -Force $requests
    [IO.File]::WriteAllText((Join-Path $requests "crowd-player-100-1-depart.request"), "x")
    [IO.File]::WriteAllText((Join-Path $requests "crowd-player-100-1-return.request"), "x")
    [IO.File]::WriteAllText((Join-Path $requests "crowd-player-100-1-return.done"), "x")
    $pending = @(Get-PendingCrowdRequests $requests)
    Assert-Equal "Pending requests" "depart" (($pending | ForEach-Object { $_.Request }) -join " ")
    Complete-CrowdRequest $pending[0]
    Assert-Equal "An answered request" 0 @(Get-PendingCrowdRequests $requests).Count
    Assert-Equal "No results folder yet" 0 @(Get-PendingCrowdRequests (Join-Path $root "missing")).Count

    # Load samples every 2 s from 10:00:02 on, one without the graphics card's use.
    $invariant = [Globalization.CultureInfo]::InvariantCulture
    $start = [DateTime]::new(2026, 11, 16, 10, 0, 0, [DateTimeKind]::Utc)
    $csv = @("utc,foreignProcessors,foreignGpuPercent,processorPercent,playerPrivateMegabytes,playerWorkingSetMegabytes,foreign")
    for ($second = 2; $second -le 20; $second += 2) {
        $busy = if ($second -eq 12) { "3.50" } else { "0.50" }
        $gpu = if ($second -eq 14) { "" } else { "1.0" }
        $csv += [string]::Format($invariant, "{0:o},{1},{2},60,{3},{4},chrome", $start.AddSeconds($second), $busy, $gpu, 1000 + $second, 1200 + $second)
    }

    $loadPath = Join-Path $root "load.csv"
    [IO.File]::WriteAllLines($loadPath, [string[]]$csv)
    $quiet = Measure-Load $loadPath $start.AddSeconds(1) $start.AddSeconds(9)
    Assert-Equal "Samples of a recording" 5 $quiet.Samples
    Assert-Equal "Largest gap of a recording" 2 $quiet.LargestGapSeconds
    Assert-Equal "Foreign processors of a recording" "0.50" (Format-Number $quiet.ForeignProcessors "0.00")
    Assert-Equal "Largest private memory of a recording" 1010 $quiet.PlayerPrivateMegabytes
    $short = Measure-Load $loadPath $start.AddSeconds(16.5) $start.AddSeconds(17.0) 0
    Assert-Equal "A short recording between samples" 0 $short.Samples
    $covered = Measure-Load $loadPath $start.AddSeconds(16.5) $start.AddSeconds(17.0) 10
    Assert-Equal "A short recording with the sample after it" 2 $covered.Samples
    $withoutGpu = Measure-Load $loadPath $start.AddSeconds(13) $start.AddSeconds(15) 0
    Assert-Equal "A sample without the graphics card's use is none" 0 $withoutGpu.Samples
    $busyLoad = Measure-Load $loadPath $start.AddSeconds(11) $start.AddSeconds(13) 0
    Assert-Equal "No problem in a quiet recording" "" "$(Get-LoadProblem @([pscustomobject]@{ Name = 'arrival'; Load = $quiet }) 10 2.5 10)"
    Assert-True "A recording without samples proves nothing" ("$(Get-LoadProblem @([pscustomobject]@{ Name = 'arrival'; Load = $short }) 10 2.5 10)" -match "do not cover the arrival")
    Assert-True "A busy recording is disturbed" ("$(Get-LoadProblem @([pscustomobject]@{ Name = 'arrival'; Load = $quiet }, [pscustomobject]@{ Name = 'return'; Load = $busyLoad }) 10 2.5 10)" -match "disturbed the return")
    Assert-Equal "Private memory near a moment" 1006 (Get-PrivateMemoryNear $loadPath $start.AddSeconds(6.4))
    Assert-Equal "No private memory far from every sample" "" "$(Get-PrivateMemoryNear $loadPath $start.AddSeconds(40))"

    # The summary of a crowd of 100 with its return, a churn, and Mobs.
    $statistics = '{ "count": 600, "average": 5.0, "p50": 4.5, "p95": 7.2, "p99": 9.6, "maximum": 14.6 }'
    $summary = '{ "durationSeconds": 60.0, "frameMilliseconds": ' + $statistics + ', "slowFrames": 12, "hitchFrames": 0, "remoteStarvedPercent": 0.0, "remoteStarvations": 0, "longestRemoteStarvationSeconds": 0.0 }'
    $machine = '"runtime": "IL2CPP", "platform": "player", "screenWidth": 1920, "screenHeight": 1080, "graphicsDevice": "GPU", "processor": "CPU"'
    $stretch = '{ "measured": true, "shownSeconds": 1.7, "summary": ' + $summary + ' }'
    $memory = '[ { "moment": "with the crowd", "knownBots": 100, "allocatedMegabytes": 312.2, "reservedMegabytes": 809.0, "managedHeapMegabytes": 14.0, "bodyMeshes": 37, "bodyMaterials": 210 },' +
        ' { "moment": "crowd gone", "knownBots": 0, "allocatedMegabytes": 300.0, "reservedMegabytes": 809.0, "managedHeapMegabytes": 12.0, "bodyMeshes": 37, "bodyMaterials": 210 },' +
        ' { "moment": "crowd back", "knownBots": 100, "allocatedMegabytes": 313.0, "reservedMegabytes": 809.0, "managedHeapMegabytes": 14.0, "bodyMeshes": 37, "bodyMaterials": 210 } ]'
    $crowdResult = ('{ ' + $machine + ', "mode": "crowd", "standing": true, "crowdShownSeconds": 1.7, "ownerSnapshotsPerSecond": 15.0, "allocatedMegabytes": 312.2, "reservedMegabytes": 809.0, "managedHeapMegabytes": 14.0, "summary": ' + $summary +
        ', "arrival": ' + $stretch + ', "returnArrival": ' + $stretch + ', "memory": ' + $memory + ' }') | ConvertFrom-Json
    $churnResult = ('{ ' + $machine + ', "mode": "churn", "standing": true, "summary": ' + $summary + ', "arrival": { "measured": false }, "returnArrival": { "measured": false }, "memory": [], "fewestKnownBots": 160, "mostKnownBots": 200, "spawnsPerSecond": 3.04, "despawnsPerSecond": 2.96 }') | ConvertFrom-Json
    $mobResult = ('{ ' + $machine + ', "mode": "mobs", "standing": true, "crowdShownSeconds": 2.1, "allocatedMegabytes": 290.0, "reservedMegabytes": 700.0, "managedHeapMegabytes": 11.0, "summary": ' + $summary + ', "arrival": ' + $stretch + ', "returnArrival": { "measured": false }, "memory": [] }') | ConvertFrom-Json
    $load = [pscustomobject]@{ PlayerPrivateMegabytes = 1842.0; ProcessorPercent = 64.2 }
    $entries = @(
        [pscustomobject]@{ Kind = "crowd"; Count = 100; Result = $crowdResult; Loads = @{ Arrival = $load; Standing = $load; Return = $load }; PrivateMemory = @(1842.0, 1700.0, $null) },
        [pscustomobject]@{ Kind = "churn"; Count = 200; Result = $churnResult; Loads = @{ Standing = $load }; PrivateMemory = @() },
        [pscustomobject]@{ Kind = "mobs"; Count = 100; Result = $mobResult; Loads = @{ Arrival = $load; Standing = $load }; PrivateMemory = @() })
    $lines = @(New-CrowdSummary $entries)
    $expected = @(
        "Crowd baseline, IL2CPP player, 1920 by 1080, GPU, CPU",
        "| Bots | Shown after | Frame p50 / p95 / p99 / max | Owner snapshots per s | Remote starved | Allocated / reserved / managed | Player private memory | Machine busy |",
        "| ---: | ---: | --- | ---: | --- | --- | ---: | ---: |",
        "| 100 | 1.7 s | 4.5 / 7.2 / 9.6 / 14.6 ms | 15.0 | 0.00 %, 0 times, longest 0.00 s | 312 / 809 / 14 MB | 1842 MB | 64 % |")
    for ($index = 0; $index -lt $expected.Count; $index++) {
        Assert-Equal "Standing table line $($index + 1)" $expected[$index] $lines[$index]
    }

    Assert-True "Arrival of the first crowd" ($lines -contains "| 100 | first | 1.7 s | 14.6 ms | 0 | 12 | 4.5 / 7.2 / 9.6 ms | 64 % |")
    Assert-True "Arrival of the crowd that came back" ($lines -contains "| 100 | return | 1.7 s | 14.6 ms | 0 | 12 | 4.5 / 7.2 / 9.6 ms | 64 % |")
    Assert-True "Memory once the crowd left" ($lines -contains "| 100 | crowd gone | 0 | 300 / 809 / 12 MB | 1700 MB | 37 / 210 |")
    Assert-True "Memory without a sample" ($lines -contains "| 100 | crowd back | 100 | 313 / 809 / 14 MB | - MB | 37 / 210 |")
    Assert-True "Churn" ($lines -contains "| 200 | 160 to 200 | 3.0 | 3.0 | 4.5 / 7.2 / 9.6 ms | 14.6 ms | 0 | 0.00 %, 0 times, longest 0.00 s | 64 % |")
    Assert-True "Mobs" ($lines -contains "| 100 | 2.1 s | 14.6 ms / 0 | 4.5 / 7.2 / 9.6 / 14.6 ms | 0 | 0.00 %, 0 times, longest 0.00 s | 290 / 700 / 11 MB | 1842 MB | 64 % |")
    Assert-Equal "Tables" 5 @($lines | Where-Object { $_ -like "| ---*" }).Count

    # A run of one kind prints only its tables.
    $churnOnly = @(New-CrowdSummary @($entries[1]))
    Assert-True "A churn alone" ($churnOnly[0] -like "Crowd churn, *" -and @($churnOnly | Where-Object { $_ -like "| ---*" }).Count -eq 1)
    $standingOnly = @(New-CrowdSummary $entries @("Standing"))
    Assert-Equal "Only the standing table" 1 @($standingOnly | Where-Object { $_ -like "| ---*" }).Count
    $arrivalOnly = @(New-CrowdSummary $entries @("Arrival"))
    Assert-True "Only the arrival and memory tables" ($arrivalOnly[0] -like "Crowd arrival, *" -and @($arrivalOnly | Where-Object { $_ -like "| ---*" }).Count -eq 2)
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Host "  $failure"
    }

    Write-Host "Result: the client crowd helpers failed $($failures.Count) case(s)"
    exit 1
}

Write-Host "Result: the client crowd helpers hold in every case"
exit 0
