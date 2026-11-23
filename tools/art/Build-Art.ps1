# Takes the art from changed .blend files to exported assets in Unity in one run, and stops where a
# person must look: it makes every content record current, with several Blender processes at once;
# plans what each asset needs, a sync, an approval, an export, or nothing, in the order the assets
# follow each other; runs the syncs and plans again; lists every review stage that waits for an
# approval with what changed, its review sheet, and the command that approves it; exports every asset
# whose approvals hold and that follows no asset waiting for one, with several Blender processes at
# once; runs the Unity follow-up after an export (tools/art/Update-UnityArt.ps1); verifies the records
# (tools/art/Verify-ArtAssets.ps1); and prints the seconds of each phase and asset, how many check units
# came from the cache, the assets it synced, and what is left for a person. It never records an
# approval. A body's export gives the fit and the paint layout that the cage, the garments, and the
# paint layers follow, so after an export of a body it plans again. A sync is not run again while the
# asset's file and everything it follows are as the build's last sync of it left them, which
# artifacts/art-build/synced.json keeps. The Unity follow-up runs while an export left its marker
# artifacts/art-build/unity/pending, also one of an earlier run whose follow-up failed. See
# docs/ART_PIPELINE.md, Build The Art.
#
# Exit codes: 0 everything is exported and verified, 2 review stages wait for an approval and
# everything else that could be exported was, 1 an error.
param(
    # Prints what every asset needs and why, and stops.
    [switch]$Plan,
    # How many Blender processes run at once: by default half the logical processors, at most 8.
    [int]$Jobs = 0,
    # Computes every check of a garment template or a masked texture, without the cache of the checks.
    [switch]$NoCache,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

if ($Jobs -lt 0) {
    throw "-Jobs is how many Blender processes run at once, 1 or more."
}

if ($Jobs -eq 0) {
    $Jobs = Get-ArtDefaultJobs
}

$culture = [System.Globalization.CultureInfo]::InvariantCulture
$phases = New-Object System.Collections.Generic.List[object]
$assetTimes = New-Object System.Collections.Generic.List[object]
$exportReport = New-Object System.Collections.Generic.List[object]
$synced = New-Object System.Collections.Generic.HashSet[string]
$syncedOrder = New-Object System.Collections.Generic.List[string]
$syncRecordsPath = Join-Path $script:ArtRepositoryRoot "artifacts/art-build/synced.json"
$unityPendingPath = Join-Path $script:ArtRepositoryRoot "artifacts/art-build/unity/pending"

function Add-ArtPhase([string]$Name, [System.Diagnostics.Stopwatch]$Watch) {
    $phases.Add([pscustomobject]@{ Phase = $Name; Seconds = $Watch.Elapsed.TotalSeconds })
}

function Read-ArtAllManifests {
    # Every asset below art/, by folder.
    $manifests = [ordered]@{}
    foreach ($directory in @(Get-ArtAssetDirectories)) {
        $manifest = Read-ArtAssetManifest $directory
        $manifests[$manifest.RelativeDirectory] = $manifest
    }

    return $manifests
}

function Update-ArtContentRecords([string]$Blender) {
    # Makes every content record current, one Blender process per file whose hash differs from its
    # record, several at once: the cage and every other asset first, then the garment templates, whose
    # content run takes the cage's content.
    foreach ($garments in @($false, $true)) {
        $stale = @((Read-ArtAllManifests).Values | Where-Object {
                ($_.Kind -eq "garment") -eq $garments -and $null -ne (Get-ArtContentProblem $_) })
        if ($stale.Count -eq 0) {
            continue
        }

        $folder = Get-ArtBuildFolder "content"
        $contentJobs = @($stale | ForEach-Object {
                New-ArtBlenderJob $_ (@("content") + (Get-ArtValidationArguments $_ $_.Stages[-1])) (Join-Path $folder "$($_.Name).log") @{
                    SourceSha256 = Get-ArtFileSha256 $_.SourcePath
                }
            })
        Invoke-ArtBlenderJobs $Blender $contentJobs $Jobs {
            param($Job)
            if ($null -eq $Job.Error) {
                try {
                    $null = Update-ArtContentRecord $Blender $Job.Manifest $Job.Results $Job.SourceSha256
                }
                catch {
                    $Job.Error = $_.Exception.Message
                }
            }

            $state = if ($null -eq $Job.Error) { "recorded its content" } else { "failed: $($Job.Error)" }
            Write-Host "$($Job.Manifest.RelativeDirectory): $state ($($Job.Seconds.ToString('0.0', $culture)) s)."
            $assetTimes.Add([pscustomobject]@{ Asset = $Job.Manifest.RelativeDirectory; Phase = "content"; Seconds = $Job.Seconds; Units = 0; Cached = 0 })
        }

        $failed = @($contentJobs | Where-Object { $null -ne $_.Error })
        if ($failed.Count -gt 0) {
            throw "The content of $(@($failed | ForEach-Object { $_.Name }) -join ', ') could not be recorded:`n - $(@($failed | ForEach-Object { $_.Error }) -join "`n - ")"
        }
    }
}

function Get-ArtGroup($Manifest, $Bodies) {
    # The place of an asset in the order the assets follow each other and the plan lists them.
    switch ($Manifest.Kind) {
        "cage" { return 1 }
        "animations" { return 2 }
        "garment" { return 3 }
        "texture" { return 4 }
        "paint" { return 5 }
        default { if ($Bodies -ccontains $Manifest.RelativeDirectory) { return 0 } else { return 6 } }
    }
}

function Get-ArtFollowed($Manifest) {
    # The assets whose content or export an asset follows: the bodies of the cage, of a garment, and of
    # a paint layer, whose exports give the fit and the paint layout; and the content of a garment's
    # cage, animation libraries, and templates of lower layers, a texture's template with its libraries
    # and its template's lower layers, a library's rig, and a paint layer's cage.
    $followed = New-Object System.Collections.Generic.List[object]
    function Add-Followed([string]$Asset, [bool]$ReadsExport) {
        if (-not [string]::IsNullOrEmpty($Asset) -and @($followed | Where-Object { $_.Asset -ceq $Asset }).Count -eq 0) {
            $followed.Add([pscustomobject]@{ Asset = $Asset; ReadsExport = $ReadsExport })
        }
    }

    switch ($Manifest.Kind) {
        "cage" {
            foreach ($body in @($Manifest.Bodies)) { Add-Followed $body.Asset $true }
        }
        "garment" {
            Add-Followed $Manifest.Cage.RelativeDirectory $false
            foreach ($body in @($Manifest.Bodies)) { Add-Followed $body.Asset $true }
            foreach ($library in @(Get-ArtClipLibraries $Manifest.Skeleton)) { Add-Followed $library.Asset $false }
            foreach ($pair in @(Get-ArtGarmentPairs $Manifest)) { Add-Followed $pair.RelativeDirectory $false }
        }
        "texture" {
            Add-Followed $Manifest.Garment.RelativeDirectory $false
            foreach ($library in @(Get-ArtClipLibraries $Manifest.Skeleton)) { Add-Followed $library.Asset $false }
            foreach ($pair in @(Get-ArtGarmentPairs $Manifest.Garment)) { Add-Followed $pair.RelativeDirectory $false }
        }
        "animations" {
            Add-Followed $Manifest.Rig.Asset $false
        }
        "paint" {
            foreach ($body in @($Manifest.Bodies)) { Add-Followed $body.Asset $true }
            foreach ($cage in @(Get-ArtPaintCage $Manifest)) { Add-Followed $cage.RelativeDirectory $false }
        }
    }

    return $followed.ToArray()
}

function Get-ArtSyncReasons($Manifest, $Manifests) {
    # Why the asset needs its sync: something it follows changed, or its own content differs from its
    # approval, which a sync fits again and shows on a new review sheet. None for a kind without a sync.
    $reasons = New-Object System.Collections.Generic.List[string]
    if (@("cage", "garment", "texture", "animations", "paint") -notcontains $Manifest.Kind) {
        return $reasons.ToArray()
    }

    switch ($Manifest.Kind) {
        "cage" {
            $problem = Get-ArtCageBodyProblem $Manifest
            if ($null -ne $problem) { $reasons.Add($problem) }
        }
        "garment" {
            if ($null -ne $Manifest.Export) {
                foreach ($problem in @(Get-ArtGarmentFitProblems $Manifest)) { $reasons.Add("it $problem") }
                # The sync keeps its cloth clear of the templates of lower layers it lies over, so
                # other content of one of them can move it; the export only checks that.
                $pairProblem = Get-ArtPairSourcesProblem $Manifest
                if ($null -ne $pairProblem) { $reasons.Add("it $pairProblem") }
            }
        }
        "texture" {
            $problem = Get-ArtTextureTemplateProblem $Manifest
            if ($null -ne $problem) { $reasons.Add($problem) }
        }
        "animations" {
            $rig = $Manifests[$Manifest.Rig.Asset]
            if ($null -ne $Manifest.Export -and $null -ne $rig -and (Get-ArtExportedContent $rig) -cne $Manifest.Export.rigContentSha256) {
                $reasons.Add("its rig $($Manifest.Rig.Asset) changed since its export")
            }
        }
        "paint" {
            if ($null -ne $Manifest.Export) {
                foreach ($problem in @(Get-ArtPaintDependencyProblems $Manifest)) { $reasons.Add("it $problem") }
            }
        }
    }

    $changed = @($Manifest.Stages | Where-Object { -not (Test-ArtStageHolds $Manifest $_) })
    if ($changed.Count -gt 0) {
        $reasons.Add("its '$($changed[0])' content differs from its approval")
    }

    return $reasons.ToArray()
}

function Get-ArtExportReasons($Manifest, $Manifests) {
    # Why an asset whose approvals hold needs an export: its own record is missing or older than its
    # content or its files, or a record of what it follows names other content than the current one.
    $reasons = New-Object System.Collections.Generic.List[string]
    if ($Manifest.Kind -eq "cage") {
        return $reasons.ToArray()
    }

    if ($null -eq $Manifest.Export) {
        $reasons.Add("it was never exported")
        return $reasons.ToArray()
    }

    if ($null -ne $Manifest.ExportProblem) { $reasons.Add($Manifest.ExportProblem) }
    if ($Manifest.Export.contentSha256 -cne (Get-ArtExportedContent $Manifest)) { $reasons.Add("its content changed since its export") }
    foreach ($problem in @(Get-ArtExportFileProblems $Manifest)) { $reasons.Add($problem) }
    switch ($Manifest.Kind) {
        "garment" {
            foreach ($problem in @(Get-ArtGarmentDependencyProblems $Manifest)) { $reasons.Add("it $problem") }
        }
        "texture" {
            if ((Get-ArtExportedContent $Manifest.Garment) -cne $Manifest.Export.garmentContentSha256) {
                $reasons.Add("its template $($Manifest.Garment.RelativeDirectory) changed since its export")
            }

            if (@($Manifest.Export.masks | Where-Object { $null -ne $_ }).Count -gt 0) {
                foreach ($problem in @((Get-ArtClipSourcesProblem $Manifest "tools/art/Sync-TextureAsset.ps1"),
                        (Get-ArtPairSourcesProblem $Manifest $Manifest.Garment "tools/art/Sync-TextureAsset.ps1") | Where-Object { $null -ne $_ })) {
                    $reasons.Add("it $problem")
                }
            }
        }
        "animations" {
            $rig = $Manifests[$Manifest.Rig.Asset]
            if ($null -eq $rig -or (Get-ArtExportedContent $rig) -cne $Manifest.Export.rigContentSha256) {
                $reasons.Add("its rig $($Manifest.Rig.Asset) changed since its export")
            }
        }
        "paint" {
            foreach ($problem in @(Get-ArtPaintDependencyProblems $Manifest)) { $reasons.Add("it $problem") }
        }
    }

    return $reasons.ToArray()
}

function Read-ArtSyncRecords {
    # What the build's syncs left, by asset: the SHA-256 of the asset's file and the fingerprints of what
    # it followed. A missing or broken file reads as no record, which costs one sync.
    $records = @{}
    if (-not (Test-Path -LiteralPath $syncRecordsPath -PathType Leaf)) {
        return $records
    }

    try {
        foreach ($property in (Get-Content -Raw -LiteralPath $syncRecordsPath | ConvertFrom-Json).PSObject.Properties) {
            $follows = @{}
            foreach ($followed in $property.Value.follows.PSObject.Properties) {
                $follows[$followed.Name] = [string]$followed.Value
            }

            $records[$property.Name] = [pscustomobject]@{ SourceSha256 = [string]$property.Value.sourceSha256; Follows = $follows }
        }
    }
    catch {
        return @{}
    }

    return $records
}

function Save-ArtSyncRecords($Records) {
    $lines = New-Object System.Collections.Generic.List[string]
    $assets = [string[]]@($Records.Keys)
    [Array]::Sort($assets, [StringComparer]::Ordinal)
    for ($i = 0; $i -lt $assets.Count; $i++) {
        $record = $Records[$assets[$i]]
        $names = [string[]]@($record.Follows.Keys)
        [Array]::Sort($names, [StringComparer]::Ordinal)
        $follows = @($names | ForEach-Object { "`"$_`": `"$($record.Follows[$_])`"" }) -join ", "
        $comma = if ($i -lt $assets.Count - 1) { "," } else { "" }
        $lines.Add("  `"$($assets[$i])`": { `"sourceSha256`": `"$($record.SourceSha256)`", `"follows`": { $follows } }$comma")
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $syncRecordsPath) | Out-Null
    [System.IO.File]::WriteAllText($syncRecordsPath, "{`n$($lines -join "`n")`n}`n", (New-Object System.Text.UTF8Encoding($false)))
}

function Get-ArtFollowedFingerprints($Manifest, $Manifests) {
    # What an asset follows as it is now: of a body whose export it reads, the fit and the paint layout
    # of that export; of every other asset, its content.
    $fingerprints = @{}
    foreach ($followed in @(Get-ArtFollowed $Manifest)) {
        $other = $Manifests[$followed.Asset]
        $fingerprints[$followed.Asset] = if ($null -eq $other) {
            "missing"
        }
        elseif ($followed.ReadsExport) {
            if ($null -eq $other.Export) { "not exported" } else { "fit=$($other.Export.fitSha256);paint=$($other.Export.paintSha256)" }
        }
        else {
            "content=$(Get-ArtExportedContent $other)"
        }
    }

    return $fingerprints
}

function Test-ArtSyncRecordHolds($Manifest, $Manifests, $Records) {
    # Whether the asset's file and everything it follows are exactly as the build's last sync of it
    # left them, so a sync would change nothing.
    $record = $Records[$Manifest.RelativeDirectory]
    if ($null -eq $record -or -not (Test-Path -LiteralPath $Manifest.SourcePath -PathType Leaf) -or
        (Get-ArtFileSha256 $Manifest.SourcePath) -cne $record.SourceSha256) {
        return $false
    }

    $now = Get-ArtFollowedFingerprints $Manifest $Manifests
    if ($now.Count -ne $record.Follows.Count) {
        return $false
    }

    foreach ($asset in $now.Keys) {
        if (-not $record.Follows.ContainsKey($asset) -or $record.Follows[$asset] -cne $now[$asset]) {
            return $false
        }
    }

    return $true
}

function Get-ArtBuildPlan {
    # What every asset needs, decided in the order they follow each other, each from the needs of what
    # it follows: a sync waits for the syncs of what it follows, and for the export of a body whose fit
    # or paint layout it reads; an export waits for every approval of what it follows, also through
    # other assets.
    $manifests = Read-ArtAllManifests
    $bodies = @($manifests.Values | ForEach-Object {
            if ($_.Kind -eq "cage" -or $_.Kind -eq "paint") { @($_.Bodies | ForEach-Object { $_.Asset }) }
            if ($_.Kind -eq "animations") { $_.Rig.Asset }
        } | Sort-Object -Unique)
    # A template whose approvals hold is exported by this build if it has no export yet, so it counts as
    # a template of a lower layer for the others, as in Export-ArtAsset.ps1.
    $script:ArtExportingTemplates = @($manifests.Values | Where-Object { $_.Kind -eq "garment" -and $null -eq (Get-ArtExportGateProblem $_) } |
            ForEach-Object { $_.RelativeDirectory })
    $ordered = @($manifests.Values | Sort-Object -Property @{ Expression = { Get-ArtGroup $_ $bodies } },
        @{ Expression = { if ($_.Kind -eq "garment") { $_.Layer } else { 0 } } }, @{ Expression = { $_.RelativeDirectory } })
    $syncRecords = Read-ArtSyncRecords
    $needs = @{}
    # The assets whose sync, export, or approval an asset that waits or comes later waits for, through
    # the assets between them.
    $causes = @{}
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($manifest in $ordered) {
        $entry = [pscustomobject]@{
            Asset = $manifest.RelativeDirectory
            Manifest = $manifest
            Need = "nothing"
            Reasons = @()
            Stages = @($manifest.Stages | Where-Object { -not (Test-ArtStageHolds $manifest $_) })
            Follows = @()
        }
        $followed = @(Get-ArtFollowed $manifest)
        $syncing = @($followed | Where-Object { @("sync", "later") -contains $needs[$_.Asset] -or
                ($_.ReadsExport -and $needs[$_.Asset] -eq "export") })
        $waiting = @($followed | Where-Object { @("approval", "waits") -contains $needs[$_.Asset] })
        $syncReasons = if ($synced.Contains($manifest.RelativeDirectory)) { @() } else { @(Get-ArtSyncReasons $manifest $manifests) }
        if ($syncReasons.Count -gt 0 -and (Test-ArtSyncRecordHolds $manifest $manifests $syncRecords)) {
            # The build synced it already, and neither its file nor what it follows changed since.
            $syncReasons = @()
        }
        $cageProblem = if ($manifest.Kind -eq "cage" -and -not (Test-ArtAssetInReview $manifest)) { Get-ArtCageBodyProblem $manifest } else { $null }
        if ($syncing.Count -gt 0) {
            $entry.Need = "later"
            $entry.Follows = @($syncing | ForEach-Object { if ($needs[$_.Asset] -eq "later") { $causes[$_.Asset] } else { $_.Asset } } | Sort-Object -Unique)
            $entry.Reasons = @("it follows $($entry.Follows -join ', '), which this build syncs or exports first")
        }
        elseif ($syncReasons.Count -gt 0) {
            $entry.Need = "sync"
            $entry.Reasons = $syncReasons
        }
        elseif ($entry.Stages.Count -gt 0 -or $null -ne $cageProblem) {
            $entry.Need = "approval"
            if ($entry.Stages.Count -eq 0) {
                $entry.Stages = @($manifest.Stages[-1])
            }

            $entry.Reasons = @(@($entry.Stages | ForEach-Object { "its '$_' stage waits for an approval" }) + @($cageProblem | Where-Object { $null -ne $_ }))
        }
        else {
            $exportReasons = @(Get-ArtExportReasons $manifest $manifests)
            if ($exportReasons.Count -gt 0 -and $waiting.Count -gt 0) {
                $entry.Need = "waits"
                $entry.Follows = @($waiting | ForEach-Object { if ($needs[$_.Asset] -eq "waits") { $causes[$_.Asset] } else { $_.Asset } } | Sort-Object -Unique)
                $entry.Reasons = @("it follows $($entry.Follows -join ', '), which waits for an approval") + $exportReasons
            }
            elseif ($exportReasons.Count -gt 0) {
                $entry.Need = "export"
                $entry.Reasons = $exportReasons
            }
        }

        $needs[$manifest.RelativeDirectory] = $entry.Need
        $causes[$manifest.RelativeDirectory] = $entry.Follows
        $entries.Add($entry)
    }

    return $entries.ToArray()
}

function Write-ArtPlan($Entries, [switch]$All) {
    # The plan, one line per asset, with the first reason; -All also lists the assets that need nothing.
    $shown = @($Entries | Where-Object { $All -or $_.Need -ne "nothing" })
    $width = [Math]::Max(10, @($Entries | ForEach-Object { $_.Asset.Length } | Measure-Object -Maximum).Maximum)
    foreach ($entry in $shown) {
        $why = if (@($entry.Reasons).Count -gt 0) { ": $(@($entry.Reasons)[0])" } else { "" }
        $more = if (@($entry.Reasons).Count -gt 1) { " (and $(@($entry.Reasons).Count - 1) more)" } else { "" }
        Write-Host ("  {0} {1}{2}{3}" -f $entry.Asset.PadRight($width), $entry.Need, $why, $more)
    }

    $counts = @($Entries | Group-Object Need | Sort-Object Name | ForEach-Object { "$($_.Count) $($_.Name)" })
    Write-Host "  In all: $($counts -join ', ')."
}

function Get-ArtCommittedParts([string]$Asset) {
    # The parts of the content record in the committed asset.json, or $null for an asset without one.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $text = @(& git -C $script:ArtRepositoryRoot show "HEAD:$Asset/asset.json" 2>$null) -join "`n"
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($exitCode -ne 0) {
        return $null
    }

    $json = $text | ConvertFrom-Json
    $parts = @{}
    if ($null -ne $json.content -and $null -ne $json.content.parts) {
        foreach ($property in $json.content.parts.PSObject.Properties) {
            $parts[$property.Name] = [string]$property.Value
        }
    }

    return $parts
}

function Get-ArtChangedParts($Manifest) {
    # The parts of the asset's content whose fingerprints differ from the committed asset.json.
    $committed = Get-ArtCommittedParts $Manifest.RelativeDirectory
    if ($null -eq $committed) {
        return "it is a new asset"
    }

    $current = if ($null -ne $Manifest.Content) { $Manifest.Content.parts } else { [ordered]@{} }
    $names = @(@($current.Keys) + @($committed.Keys) | Sort-Object -Unique)
    $changed = @($names | Where-Object { -not $committed.ContainsKey($_) -or -not $current.Contains($_) -or $committed[$_] -cne $current[$_] })
    if ($changed.Count -eq 0) {
        return "its content is the committed one"
    }

    return "the parts $($changed -join ', ') changed"
}

function Write-ArtApprovals($Entries) {
    # Every review stage that waits for an approval, in the order the assets follow each other, so the
    # cage comes before its garments and a template before its textures.
    $waiting = @($Entries | Where-Object { $_.Need -eq "approval" -or ($_.Need -ne "later" -and @($_.Stages).Count -gt 0) })
    if ($waiting.Count -eq 0) {
        return 0
    }

    Write-Host "Review stages that wait for an approval, in the order to approve them:"
    $count = 0
    foreach ($entry in $waiting) {
        $manifest = $entry.Manifest
        $bodyOfCage = $manifest.Kind -eq "skinned" -and @($Entries | Where-Object { $_.Manifest.Kind -eq "cage" -and @($_.Manifest.Bodies | ForEach-Object { $_.Asset }) -ccontains $manifest.RelativeDirectory }).Count -gt 0
        $sheet = if ($bodyOfCage) { Join-Path $script:ArtRepositoryRoot "artifacts\human-base\review" } else { Join-Path $script:ArtRepositoryRoot "artifacts\art-previews\$($manifest.Name).png" }
        $sheetNote = if (Test-Path -LiteralPath $sheet) { $sheet } else { "$sheet, not rendered yet: run .\tools\art\Render-ArtPreview.ps1 -Asset $($manifest.RelativeDirectory)" }
        $other = @($entry.Reasons | Where-Object { $_ -notlike "its '*' stage waits for an approval" })
        Write-Host "  $($manifest.RelativeDirectory): $(Get-ArtChangedParts $manifest)$(if ($other.Count -gt 0) { "; $($other -join '; ')" })."
        Write-Host "    Review sheet: $sheetNote"
        foreach ($stage in @($entry.Stages)) {
            Write-Host "    .\tools\art\Approve-ArtAsset.ps1 -Asset $($manifest.RelativeDirectory) -Stage $stage"
            $count++
        }
    }

    return $count
}

function Invoke-ArtSync($Entry, [string]$Blender) {
    # The sync of one asset, by its kind's script.
    $syncScript = switch ($Entry.Manifest.Kind) {
        "cage" { "Sync-CageAsset.ps1" }
        "garment" { "Sync-GarmentAsset.ps1" }
        "texture" { "Sync-TextureAsset.ps1" }
        "animations" { "Sync-AnimationLibrary.ps1" }
        "paint" { "Sync-PaintAsset.ps1" }
    }
    $arguments = @{ Asset = $Entry.Manifest.Directory; BlenderPath = $Blender }
    if ($NoCache -and @("garment", "texture") -contains $Entry.Manifest.Kind) {
        $arguments.NoCache = $true
    }

    Write-Host "Syncing $($Entry.Asset) with tools/art/$syncScript, since $(@($Entry.Reasons)[0])."
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & (Join-Path $PSScriptRoot $syncScript) @arguments
    $null = $synced.Add($Entry.Asset)
    $syncedOrder.Add($Entry.Asset)
    $assetTimes.Add([pscustomobject]@{ Asset = $Entry.Asset; Phase = "sync"; Seconds = $watch.Elapsed.TotalSeconds; Units = 0; Cached = 0 })

    # What the sync left, so the next build does not run it again while nothing changed.
    $manifests = Read-ArtAllManifests
    $manifest = $manifests[$Entry.Asset]
    $records = Read-ArtSyncRecords
    $records[$Entry.Asset] = [pscustomobject]@{
        SourceSha256 = Get-ArtFileSha256 $manifest.SourcePath
        Follows = Get-ArtFollowedFingerprints $manifest $manifests
    }
    Save-ArtSyncRecords $records
}

function Write-ArtTimes([int]$Approvals, $Entries) {
    # Where the time went, and what is left for a person.
    Write-Host ""
    Write-Host "Seconds per phase:"
    foreach ($phase in $phases) {
        Write-Host ("  {0,-22} {1,8}" -f $phase.Phase, $phase.Seconds.ToString("0.0", $culture))
    }

    foreach ($record in $exportReport) {
        $assetTimes.Add([pscustomobject]@{ Asset = $record.Asset; Phase = "export"; Seconds = $record.Seconds; Units = $record.Units; Cached = $record.Cached })
    }

    if ($assetTimes.Count -gt 0) {
        Write-Host "Seconds per asset:"
        Write-Host ("  {0,-34} {1,-8} {2,8} {3,12}" -f "Asset", "Phase", "Seconds", "From cache")
        foreach ($time in $assetTimes) {
            $units = if ($time.Units -gt 0) { "$($time.Cached) of $($time.Units)" } else { "" }
            Write-Host ("  {0,-34} {1,-8} {2,8} {3,12}" -f $time.Asset, $time.Phase, $time.Seconds.ToString("0.0", $culture), $units)
        }
    }

    $units = ($exportReport | Measure-Object -Property Units -Sum).Sum
    $cached = ($exportReport | Measure-Object -Property Cached -Sum).Sum
    Write-Host "Check units of the exports from the cache: $([int]$cached) of $([int]$units)."
    if ($syncedOrder.Count -gt 0) {
        Write-Host "Synced by this run, so their files changed: $($syncedOrder -join ', '). To reject a change that they follow, restore the changed asset and them from Git: git checkout -- <the changed asset> $($syncedOrder -join ' ')."
    }

    $waits = @($Entries | Where-Object { $_.Need -eq "waits" })
    if ($Approvals -gt 0) {
        Write-Host "Left for a person: $Approvals review stage(s) to approve, listed above, and then this command again for the $($waits.Count) asset(s) that follow them."
    }
    else {
        Write-Host "Left for a person: nothing."
    }
}

$total = [System.Diagnostics.Stopwatch]::StartNew()
$failure = $null
$entries = @()
try {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $blender = Resolve-ArtBlender $BlenderPath
    Update-ArtContentRecords $blender
    Add-ArtPhase "content records" $watch

    # Rounds of syncs and exports until nothing is left to sync or export, at most 12.
    $settled = $false
    for ($round = 1; $round -le 12; $round++) {
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $entries = @(Get-ArtBuildPlan)
        Add-ArtPhase "plan $round" $watch
        Write-Host "Plan$(if ($round -gt 1) { " after round $($round - 1)" }):"
        Write-ArtPlan $entries -All:($round -eq 1)
        if ($Plan) {
            $null = Write-ArtApprovals $entries
            exit 0
        }

        $syncs = @($entries | Where-Object { $_.Need -eq "sync" })
        if ($syncs.Count -gt 0) {
            $watch = [System.Diagnostics.Stopwatch]::StartNew()
            foreach ($entry in $syncs) {
                Invoke-ArtSync $entry $blender
            }

            Add-ArtPhase "syncs $round" $watch
            continue
        }

        $exports = @($entries | Where-Object { $_.Need -eq "export" })
        if ($exports.Count -eq 0) {
            $settled = $true
            break
        }

        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        & (Join-Path $PSScriptRoot "Export-ArtAsset.ps1") -Asset @($exports | ForEach-Object { $_.Manifest.Directory }) -Jobs $Jobs -NoCache:$NoCache -BlenderPath $blender -Report $exportReport
        Add-ArtPhase "exports $round" $watch
    }

    if (-not $settled) {
        throw "The build did not settle after 12 rounds of syncs and exports; run it again and read what each round did."
    }

    # The marker of an export whose Unity follow-up has not run yet, of this run or an earlier one.
    if (Test-Path -LiteralPath $unityPendingPath -PathType Leaf) {
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        & (Join-Path $PSScriptRoot "Update-UnityArt.ps1")
        $unityExit = $LASTEXITCODE
        Add-ArtPhase "unity follow-up" $watch
        if ($unityExit -ne 0) {
            throw "The Unity follow-up failed with exit code $unityExit; the next run of this command runs it again, or run tools/art/Update-UnityArt.ps1 after the cause is fixed."
        }
    }

    # The problems of the assets that wait for an approval, or follow one that does, are listed apart;
    # every other problem fails the build.
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & (Join-Path $PSScriptRoot "Verify-ArtAssets.ps1") -Waiting @($entries | Where-Object { $_.Need -ne "nothing" } | ForEach-Object { $_.Asset })
    Add-ArtPhase "verify" $watch
}
catch {
    $failure = $_.Exception.Message
}

# The approvals of the final plan, once, so a stage that an export round brought is listed too.
$approvals = if ($entries.Count -gt 0) { Write-ArtApprovals $entries } else { 0 }
$phases.Add([pscustomobject]@{ Phase = "in all"; Seconds = $total.Elapsed.TotalSeconds })
Write-ArtTimes $approvals $entries
if ($null -ne $failure) {
    Write-Host "Result: the build failed: $failure"
    exit 1
}

if ($approvals -gt 0) {
    Write-Host "Result: $approvals review stage(s) wait for an approval; everything else that could be exported was."
    exit 2
}

Write-Host "Result: every asset is exported and verified."
exit 0
