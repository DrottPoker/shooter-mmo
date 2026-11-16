# The parts of tools/Measure-ClientCrowd.ps1 that need neither Unity nor a stack: the settings
# of the bots, the Mob variant of each size, the requests of the crowd test, the load samples
# that prove a recording undisturbed, and the summary tables. tools/ci/Test-ClientCrowdCommon.ps1
# checks them. Dot-source it.

# The kinds of measurement, in the order a run measures them.
$CrowdKinds = @("Standing", "Arrival", "Churn", "Mobs")

# The churn: of its bot slots this share is the population's minimum, and the target is drawn
# again every PopulationChangeIntervalSeconds between it and every slot. A bot stays 30 to 60 s
# and, after a clean logout, offline at least while its body stays, 20 s and 2 s for the release.
# Measured on the coordinator's rules, 200 slots give about 3 spawns and 3 despawns a second and
# 160 to 200 players in view, the bodies of bots that left included.
$ChurnMinimumShare = 0.6
$ChurnSettings = [ordered]@{
    PopulationChangeIntervalSeconds = 10
    MinimumOnlineSeconds = 30
    MaximumOnlineSeconds = 60
    MinimumOfflineSeconds = 5
    MaximumOfflineSeconds = 25
    Seed = 1337
}

# The whole numbers of a parameter, given as a list or, as powershell -File passes a list, as
# one text with commas; each between Minimum and Maximum.
function ConvertTo-CrowdNumbers([string[]]$Values, [string]$Name, [int]$Minimum, [int]$Maximum) {
    $numbers = @()
    foreach ($text in @($Values | ForEach-Object { "$_".Split(",") } | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        $number = 0
        if (-not [int]::TryParse($text, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or
            $number -lt $Minimum -or $number -gt $Maximum) {
            throw "Every value of $Name is a whole number from $Minimum to $Maximum, not '$text'."
        }

        $numbers += $number
    }

    if ($numbers.Count -eq 0) {
        throw "$Name needs at least one value."
    }

    $numbers
}

# The kinds of -Only, given as a list or as one text with commas, in the order a run measures
# them; every kind when none is given.
function ConvertTo-CrowdKinds([string[]]$Values) {
    $named = @($Values | ForEach-Object { "$_".Split(",") } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    foreach ($kind in $named) {
        if ($CrowdKinds -notcontains $kind) {
            throw "-Only takes $($CrowdKinds -join ', '), not '$kind'."
        }
    }

    if ($named.Count -eq 0) {
        return $CrowdKinds
    }

    @($CrowdKinds | Where-Object { $named -contains $_ })
}

function Format-Number($Value, [string]$Format) {
    if ($null -eq $Value -or "$Value" -eq "") { return "-" }
    return ([double]$Value).ToString($Format, [Globalization.CultureInfo]::InvariantCulture)
}

# The settings of the bots of one kind, as ActiveSimulationBots takes them on its command line:
# a crowd that stays, or the churn of that many slots.
function Get-CrowdBotSettings([string]$Kind, [int]$Count) {
    $settings = [ordered]@{}
    if ($Kind -eq "Churn") {
        $settings.MinimumActiveBots = [int][Math]::Round($Count * $ChurnMinimumShare)
        $settings.MaximumActiveBots = $Count
        foreach ($key in $ChurnSettings.Keys) {
            $settings[$key] = $ChurnSettings[$key]
        }
    }
    else {
        $settings.MinimumActiveBots = $Count
        $settings.MaximumActiveBots = $Count
        $settings.MinimumOnlineSeconds = 86400
        $settings.MaximumOnlineSeconds = 86400
    }

    $settings.StatusIntervalSeconds = 2
    $settings
}

function ConvertTo-BotArguments($Settings) {
    $arguments = @()
    foreach ($key in $Settings.Keys) {
        $arguments += @("--ActiveSimulationBots:$key", ([string]$Settings[$key]))
    }

    $arguments
}

# Shares a number of Mobs between areas, the first areas one more when it does not divide.
function Split-MobCount([int]$Total, [int]$Areas) {
    $counts = @()
    for ($index = 0; $index -lt $Areas; $index++) {
        $counts += [int]([Math]::Floor($Total / $Areas)) + $(if ($index -lt $Total % $Areas) { 1 } else { 0 })
    }

    $counts
}

# The variant scenario of one Mob size from the checked-in one, tools/load/client-crowd-mobs.json:
# the Mobs shared between its areas, and an area that gets none left out.
function New-MobVariantScenario([string]$TemplateJson, [int]$Mobs) {
    $scenario = $TemplateJson | ConvertFrom-Json
    $areas = @($scenario.worldVariant.areas)
    $counts = @(Split-MobCount $Mobs $areas.Count)
    $kept = @()
    for ($index = 0; $index -lt $areas.Count; $index++) {
        if ($counts[$index] -gt 0) {
            $areas[$index].count = $counts[$index]
            $kept += $areas[$index]
        }
    }

    $scenario.worldVariant.areas = $kept
    $scenario | ConvertTo-Json -Depth 10
}

# The actor of the variant's Mobs; every area has the same.
function Get-MobVariantActor([string]$TemplateJson) {
    $actors = @(($TemplateJson | ConvertFrom-Json).worldVariant.areas | ForEach-Object { $_.actorId } | Select-Object -Unique)
    if ($actors.Count -ne 1) {
        throw "Every area of the Mob variant must have the same actor, found: $($actors -join ', ')."
    }

    $actors[0]
}

# The requests of the crowd test in its results folder that the script has not answered yet:
# crowd-<label>-<request>.request without a .done of the same name.
function Get-PendingCrowdRequests([string]$Folder) {
    if (-not (Test-Path -LiteralPath $Folder)) {
        return @()
    }

    @(Get-ChildItem -LiteralPath $Folder -Filter "crowd-*.request" -File | Sort-Object Name | ForEach-Object {
            $done = [IO.Path]::ChangeExtension($_.FullName, ".done")
            if (-not (Test-Path -LiteralPath $done)) {
                [pscustomobject]@{
                    Request = ([IO.Path]::GetFileNameWithoutExtension($_.Name) -split '-')[-1]
                    Path = $_.FullName
                    DonePath = $done
                }
            }
        })
}

function Complete-CrowdRequest($Request) {
    [IO.File]::WriteAllText($Request.DonePath, [DateTime]::UtcNow.ToString("o", [Globalization.CultureInfo]::InvariantCulture))
}

# The rows of the load monitor's file, without a row that the monitor could not complete, also
# one without the graphics card's use.
function Read-LoadSamples([string]$Path) {
    $invariant = [Globalization.CultureInfo]::InvariantCulture
    $rows = @()
    if (-not (Test-Path -LiteralPath $Path)) {
        return $rows
    }

    foreach ($row in @(Import-Csv -LiteralPath $Path)) {
        $time = [DateTime]::MinValue
        $processors = 0.0
        $gpu = 0.0
        if (-not [DateTime]::TryParse($row.utc, $invariant, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$time) -or
            -not [double]::TryParse($row.foreignProcessors, [Globalization.NumberStyles]::Float, $invariant, [ref]$processors) -or
            -not [double]::TryParse($row.foreignGpuPercent, [Globalization.NumberStyles]::Float, $invariant, [ref]$gpu)) {
            continue
        }

        $rows += [pscustomobject]@{
            Time = $time.ToUniversalTime()
            foreignProcessors = $processors
            foreignGpuPercent = $gpu
            processorPercent = $row.processorPercent
            playerPrivateMegabytes = $row.playerPrivateMegabytes
            playerWorkingSetMegabytes = $row.playerWorkingSetMegabytes
            foreign = $row.foreign
        }
    }

    $rows
}

# What other processes used on average within a recording, the average processor use of the
# machine, and the test player's largest memory, from the samples between its start and
# AfterSeconds after its end. A sample measures the seconds since the one before it, so a
# recording shorter than the sampling gap needs the first sample after its end, which a longer
# AfterSeconds takes in. LargestGapSeconds is the longest time within the recording without a
# sample, the whole recording when it has none; without a sample the averages are missing, not
# zero.
function Measure-Load([string]$Path, [DateTime]$Start, [DateTime]$End, [double]$AfterSeconds = 2.0) {
    $start = $Start.ToUniversalTime()
    $end = $End.ToUniversalTime()
    $rows = @(Read-LoadSamples $Path | Where-Object { $_.Time -ge $start -and $_.Time -le $end.AddSeconds($AfterSeconds) })
    $largestGap = 0.0
    $last = $start
    foreach ($row in $rows) {
        $largestGap = [Math]::Max($largestGap, ($row.Time - $last).TotalSeconds)
        $last = $row.Time
    }

    $largestGap = [Math]::Max($largestGap, ($end - $last).TotalSeconds)
    $processors = ($rows | Measure-Object -Property foreignProcessors -Average).Average
    $gpu = ($rows | Measure-Object -Property foreignGpuPercent -Average).Average
    $processor = ($rows | Where-Object { $_.processorPercent } | Measure-Object -Property processorPercent -Average).Average
    $private = ($rows | Where-Object { $_.playerPrivateMegabytes } | Measure-Object -Property playerPrivateMegabytes -Maximum).Maximum
    $working = ($rows | Where-Object { $_.playerWorkingSetMegabytes } | Measure-Object -Property playerWorkingSetMegabytes -Maximum).Maximum
    $names = @($rows | ForEach-Object { $_.foreign -split ' ' } | Where-Object { $_ } | Group-Object | Sort-Object Count -Descending | Select-Object -First 5 | ForEach-Object { $_.Name })
    [pscustomobject]@{
        Samples = $rows.Count
        LargestGapSeconds = $largestGap
        ForeignProcessors = $processors
        ForeignGpuPercent = $gpu
        ForeignNames = $names
        ProcessorPercent = if ($null -eq $processor) { 0.0 } else { $processor }
        PlayerPrivateMegabytes = $private
        PlayerWorkingSetMegabytes = $working
    }
}

# The test player's private memory at the sample nearest to a moment, within WithinSeconds, or
# null.
function Get-PrivateMemoryNear([string]$Path, [DateTime]$Time, [double]$WithinSeconds = 5.0) {
    $time = $Time.ToUniversalTime()
    $nearest = Read-LoadSamples $Path | Where-Object { $_.playerPrivateMegabytes } |
        Sort-Object { [Math]::Abs(($_.Time - $time).TotalSeconds) } | Select-Object -First 1
    if ($null -eq $nearest -or [Math]::Abs(($nearest.Time - $time).TotalSeconds) -gt $WithinSeconds) {
        return $null
    }

    [double]$nearest.playerPrivateMegabytes
}

# Why the loads of an attempt's recordings prove nothing, or null when every recording has
# samples that cover it and other programs stayed below both limits in each.
function Get-LoadProblem($Windows, [double]$GapSeconds, [double]$Processors, [double]$GpuPercent) {
    foreach ($window in $Windows) {
        $load = $window.Load
        if ($load.Samples -eq 0 -or $load.LargestGapSeconds -gt $GapSeconds) {
            return ("the samples of what other programs used do not cover the {0} ({1} samples, {2:N0} s without one), so nothing shows that it was undisturbed" -f
                $window.Name, $load.Samples, $load.LargestGapSeconds)
        }

        if ($load.ForeignProcessors -gt $Processors -or $load.ForeignGpuPercent -gt $GpuPercent) {
            return ("another process disturbed the {0}: {1:N2} processors and {2:N1} % of the graphics card" -f
                $window.Name, $load.ForeignProcessors, $load.ForeignGpuPercent)
        }
    }

    $null
}

# A moment of the result file; PowerShell 7 reads it as a date already.
function ConvertFrom-UtcText($Value) {
    if ($Value -is [DateTime]) {
        return $Value.ToUniversalTime()
    }

    [DateTime]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
}

function Get-MachineLine([string]$Title, $Result) {
    "$Title, $($Result.runtime) $($Result.platform), $($Result.screenWidth) by $($Result.screenHeight), $($Result.graphicsDevice), $($Result.processor)"
}

function Format-Starvation($Summary) {
    "{0} %, {1} times, longest {2} s" -f (Format-Number $Summary.remoteStarvedPercent "0.00"), $Summary.remoteStarvations,
        (Format-Number $Summary.longestRemoteStarvationSeconds "0.00")
}

function Format-Memory($Memory) {
    "{0} / {1} / {2} MB" -f (Format-Number $Memory.allocatedMegabytes "0"), (Format-Number $Memory.reservedMegabytes "0"),
        (Format-Number $Memory.managedHeapMegabytes "0")
}

# The tables of crowd-summary.md, one per selected kind of measurement that the results hold:
# Standing, Arrival with a second table of the memory at the departure and the return, Churn,
# and Mobs. Each entry has the Kind (crowd, churn, or mobs), its Count, the test's Result, the
# Loads of its recordings by name (Arrival, Standing, Return), and the PrivateMemory of each
# memory row. The standing table is the baseline's table, unchanged.
function New-CrowdSummary($Entries, [string[]]$Kinds = $CrowdKinds) {
    $lines = @()
    $crowds = @($Entries | Where-Object { $_.Kind -eq "crowd" })
    $standing = @($crowds | Where-Object { $Kinds -contains "Standing" -and $_.Result.standing })
    if ($standing.Count -gt 0) {
        $lines += Get-MachineLine "Crowd baseline" $standing[0].Result
        $lines += "| Bots | Shown after | Frame p50 / p95 / p99 / max | Owner snapshots per s | Remote starved | Allocated / reserved / managed | Player private memory | Machine busy |"
        $lines += "| ---: | ---: | --- | ---: | --- | --- | ---: | ---: |"
        foreach ($entry in $standing) {
            $r = $entry.Result
            $s = $r.summary
            $lines += ("| {0} | {1} s | {2} / {3} / {4} / {5} ms | {6} | {7} | {8} / {9} / {10} MB | {11} MB | {12} % |" -f
                $entry.Count,
                (Format-Number $r.crowdShownSeconds "0.0"),
                (Format-Number $s.frameMilliseconds.p50 "0.0"), (Format-Number $s.frameMilliseconds.p95 "0.0"),
                (Format-Number $s.frameMilliseconds.p99 "0.0"), (Format-Number $s.frameMilliseconds.maximum "0.0"),
                (Format-Number $r.ownerSnapshotsPerSecond "0.0"),
                (Format-Starvation $s),
                (Format-Number $r.allocatedMegabytes "0"), (Format-Number $r.reservedMegabytes "0"), (Format-Number $r.managedHeapMegabytes "0"),
                (Format-Number $entry.Loads['Standing'].PlayerPrivateMegabytes "0"),
                (Format-Number $entry.Loads['Standing'].ProcessorPercent "0"))
        }
    }

    $arrivals = @($crowds | Where-Object { $Kinds -contains "Arrival" -and $_.Result.arrival.measured })
    if ($arrivals.Count -gt 0) {
        if ($lines.Count -gt 0) { $lines += "" }
        $lines += Get-MachineLine "Crowd arrival, from standing in the arena until the whole crowd is shown, and again when it comes back" $arrivals[0].Result
        $lines += "| Bots | Arrival | Shown after | Longest frame | Frames over 33.3 ms | Frames over 16.7 ms | Frame p50 / p95 / p99 | Machine busy |"
        $lines += "| ---: | --- | ---: | ---: | ---: | ---: | --- | ---: |"
        foreach ($entry in $arrivals) {
            foreach ($stretch in @(@("first", $entry.Result.arrival, $entry.Loads['Arrival']), @("return", $entry.Result.returnArrival, $entry.Loads['Return']))) {
                if ($null -eq $stretch[1] -or -not $stretch[1].measured) {
                    continue
                }

                $s = $stretch[1].summary
                $lines += ("| {0} | {1} | {2} s | {3} ms | {4} | {5} | {6} / {7} / {8} ms | {9} % |" -f
                    $entry.Count, $stretch[0],
                    (Format-Number $stretch[1].shownSeconds "0.0"),
                    (Format-Number $s.frameMilliseconds.maximum "0.0"),
                    $s.hitchFrames, $s.slowFrames,
                    (Format-Number $s.frameMilliseconds.p50 "0.0"), (Format-Number $s.frameMilliseconds.p95 "0.0"), (Format-Number $s.frameMilliseconds.p99 "0.0"),
                    (Format-Number $(if ($stretch[2]) { $stretch[2].ProcessorPercent } else { $null }) "0"))
            }
        }
    }

    $returns = @($crowds | Where-Object { $Kinds -contains "Arrival" -and @($_.Result.memory).Count -gt 1 })
    if ($returns.Count -gt 0) {
        if ($lines.Count -gt 0) { $lines += "" }
        $lines += Get-MachineLine "Crowd departure and return, the memory with the crowd, once it left, and with it back" $returns[0].Result
        $lines += "| Bots | Moment | Bots known | Allocated / reserved / managed | Player private memory | Body meshes / materials |"
        $lines += "| ---: | --- | ---: | --- | ---: | --- |"
        foreach ($entry in $returns) {
            $rows = @($entry.Result.memory)
            for ($index = 0; $index -lt $rows.Count; $index++) {
                $memory = $rows[$index]
                $private = if ($index -lt @($entry.PrivateMemory).Count) { @($entry.PrivateMemory)[$index] } else { $null }
                $lines += ("| {0} | {1} | {2} | {3} | {4} MB | {5} / {6} |" -f
                    $entry.Count, $memory.moment, $memory.knownBots, (Format-Memory $memory), (Format-Number $private "0"),
                    $memory.bodyMeshes, $memory.bodyMaterials)
            }
        }
    }

    $churns = @($Entries | Where-Object { $Kinds -contains "Churn" -and $_.Kind -eq "churn" })
    if ($churns.Count -gt 0) {
        if ($lines.Count -gt 0) { $lines += "" }
        $lines += Get-MachineLine "Crowd churn, bots that come and go while the client stands" $churns[0].Result
        $lines += "| Bot slots | Bots known | Spawns per s | Despawns per s | Frame p50 / p95 / p99 | Longest frame | Frames over 33.3 ms | Remote starved | Machine busy |"
        $lines += "| ---: | --- | ---: | ---: | --- | ---: | ---: | --- | ---: |"
        foreach ($entry in $churns) {
            $r = $entry.Result
            $s = $r.summary
            $lines += ("| {0} | {1} to {2} | {3} | {4} | {5} / {6} / {7} ms | {8} ms | {9} | {10} | {11} % |" -f
                $entry.Count, $r.fewestKnownBots, $r.mostKnownBots,
                (Format-Number $r.spawnsPerSecond "0.0"), (Format-Number $r.despawnsPerSecond "0.0"),
                (Format-Number $s.frameMilliseconds.p50 "0.0"), (Format-Number $s.frameMilliseconds.p95 "0.0"), (Format-Number $s.frameMilliseconds.p99 "0.0"),
                (Format-Number $s.frameMilliseconds.maximum "0.0"), $s.hitchFrames,
                (Format-Starvation $s),
                (Format-Number $entry.Loads['Standing'].ProcessorPercent "0"))
        }
    }

    $mobs = @($Entries | Where-Object { $Kinds -contains "Mobs" -and $_.Kind -eq "mobs" })
    if ($mobs.Count -gt 0) {
        if ($lines.Count -gt 0) { $lines += "" }
        $lines += Get-MachineLine "Walking Mobs in view, without bots" $mobs[0].Result
        $lines += "| Mobs | Shown after | Arrival longest frame / over 33.3 ms | Frame p50 / p95 / p99 / max | Frames over 33.3 ms | Remote starved | Allocated / reserved / managed | Player private memory | Machine busy |"
        $lines += "| ---: | ---: | --- | --- | ---: | --- | --- | ---: | ---: |"
        foreach ($entry in $mobs) {
            $r = $entry.Result
            $s = $r.summary
            $a = $r.arrival.summary
            $lines += ("| {0} | {1} s | {2} ms / {3} | {4} / {5} / {6} / {7} ms | {8} | {9} | {10} | {11} MB | {12} % |" -f
                $entry.Count,
                (Format-Number $r.crowdShownSeconds "0.0"),
                (Format-Number $a.frameMilliseconds.maximum "0.0"), $a.hitchFrames,
                (Format-Number $s.frameMilliseconds.p50 "0.0"), (Format-Number $s.frameMilliseconds.p95 "0.0"),
                (Format-Number $s.frameMilliseconds.p99 "0.0"), (Format-Number $s.frameMilliseconds.maximum "0.0"),
                $s.hitchFrames,
                (Format-Starvation $s),
                (Format-Memory $r),
                (Format-Number $entry.Loads['Standing'].PlayerPrivateMegabytes "0"),
                (Format-Number $entry.Loads['Standing'].ProcessorPercent "0"))
        }
    }

    $lines
}
