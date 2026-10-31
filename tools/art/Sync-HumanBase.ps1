# Brings the human base characters art/Characters/Human_Male and Human_Female up to date after an
# edit in Blender. Their files are the source: each holds its whole body as one editable mesh,
# <Label>_Body in the Source collection, and this script writes the ten body parts of the Export
# collection from it, with its normals and weights. -Weights also solves the skin weights again from
# the joints the files have, and -Rig places the joints again from the models first. Then it checks
# both characters, replaces a committed file only when its content changed, and writes review
# files, review sheets, and a quality report. A changed file needs a new review and approval with
# Approve-ArtAsset.ps1. See docs/ART_PIPELINE.md.
[CmdletBinding(DefaultParameterSetName = "Parts")]
param(
    # Solves the skin weights again from the joints of the rigs.
    [Parameter(ParameterSetName = "Weights")]
    [switch]$Weights,
    # Places the joints again from the models and solves the skin weights. The clips of the
    # animation libraries keyed on the old rig then need a new sync and review.
    [Parameter(ParameterSetName = "Rig")]
    [switch]$Rig,
    # Work files, review files, sheets, and report. Defaults to artifacts/human-base, which Git ignores.
    [string]$WorkDirectory,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

$cli = Join-Path $PSScriptRoot "blender/human_base_cli.py"
$characters = @(
    [pscustomobject]@{ Label = "Male"; Asset = "Human_Male" },
    [pscustomobject]@{ Label = "Female"; Asset = "Human_Female" })
$skeleton = Join-Path $script:ArtSkeletonFolder "Humanoid.json"
$step = if ($Rig) { "rig" } elseif ($Weights) { "weights" } else { "sync" }

if ([string]::IsNullOrWhiteSpace($WorkDirectory)) {
    $WorkDirectory = Join-Path $script:ArtRepositoryRoot "artifacts/human-base"
}

$work = [System.IO.Path]::GetFullPath($WorkDirectory)
New-Item -ItemType Directory -Force -Path $work | Out-Null
$blender = Resolve-ArtBlender $BlenderPath
$report = New-Object System.Collections.Generic.List[string]

function Invoke-HumanBaseStep([string]$Step, [string[]]$Arguments, [string]$LogName = $Step) {
    # Runs one step of human_base_cli.py, keeps its log, collects its report lines, and returns
    # its output.
    $started = Get-Date
    $output = Invoke-ArtNative $blender (@(
            "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
            "--python", $cli, "--", $Step) + $Arguments)
    $exitCode = $script:ArtNativeExitCode
    $log = Join-Path $work "log_$LogName.txt"
    [System.IO.File]::WriteAllLines($log, [string[]]$output)
    $prefix = "HUMAN_BASE_REPORT "
    foreach ($line in @($output | Where-Object { $_.StartsWith($prefix) })) {
        $report.Add($line.Substring($prefix.Length))
    }

    $failures = @($output | Where-Object { $_ -like "HUMAN_BASE_FAILED *" } |
        ForEach-Object { $_.Substring("HUMAN_BASE_FAILED ".Length) })
    if ($failures.Count -gt 0) {
        throw "The human base '$Step' step found problems:`n - $($failures -join "`n - ")`nLog: $log"
    }

    if ($exitCode -ne 0) {
        throw "The human base '$Step' step failed with exit code ${exitCode}:`n$(($output | Select-Object -Last 30) -join "`n")`nLog: $log"
    }

    Write-Host ("{0,-22} {1,5:0} s" -f $LogName, ((Get-Date) - $started).TotalSeconds)
    return $output
}

function Test-GitLfsPointer([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    return $item.Length -lt 1024 -and [System.IO.File]::ReadAllText($item.FullName).StartsWith("version https://git-lfs")
}

$assetWork = Join-Path $work "assets"
New-Item -ItemType Directory -Force -Path $assetWork | Out-Null
foreach ($character in $characters) {
    $directory = Join-Path $script:ArtSourceRoot "Characters/$($character.Asset)"
    $source = Join-Path $directory "$($character.Asset).blend"
    if (-not (Test-Path -LiteralPath $source)) {
        throw "$source is missing."
    }

    if (Test-GitLfsPointer $source) {
        throw "$source is a Git LFS pointer; run git lfs pull first."
    }

    $before = Join-Path $assetWork "$($character.Asset)_before.blend"
    Copy-Item -LiteralPath $source -Destination $before -Force
    $built = Join-Path $assetWork "$($character.Asset).blend"
    $null = Invoke-HumanBaseStep $step @($before, $character.Label, $skeleton, $built) "$($step)_$($character.Label)"
    $null = Invoke-HumanBaseStep "check-rig" @($before, $built, $character.Label, $skeleton) "check_rig_$($character.Label)"
    $character | Add-Member -NotePropertyName Source -NotePropertyValue $source
    $character | Add-Member -NotePropertyName Built -NotePropertyValue $built
}

$report.Add("")
$male, $female = $characters[0].Built, $characters[1].Built
$null = Invoke-HumanBaseStep "check-mesh" @($male, $female)
$report.Add("")
$null = Invoke-HumanBaseStep "check-seams" @($male, $female)
$report.Add("")
$null = Invoke-HumanBaseStep "check-parts" @($male, $female)
$report.Add("")

# Blender's files differ in their bytes from run to run, so a file only replaces the committed one
# when its content changed, and an unchanged file keeps its approval.
foreach ($character in $characters) {
    $output = Invoke-HumanBaseStep "compare" @($character.Source, $character.Built) "compare_$($character.Label)"
    $same = @($output | Where-Object { $_ -eq "HUMAN_BASE_SAME yes" }).Count -gt 0
    if (-not $same) {
        Copy-Item -LiteralPath $character.Built -Destination $character.Source -Force
    }

    $character | Add-Member -NotePropertyName Changed -NotePropertyValue (-not $same)
}

$reviewSheets = Join-Path $work "review"
$modelReview = Join-Path $work "Human_Base.blend"
$null = Invoke-HumanBaseStep "review-file" @($characters[0].Source, $characters[1].Source, $modelReview)
$null = Invoke-HumanBaseStep "review" @($modelReview, $reviewSheets, "Human_Base")
foreach ($character in $characters) {
    $null = Invoke-HumanBaseStep "review-rig" @($character.Source, $character.Label, $reviewSheets, $character.Asset) "review_rig_$($character.Label)"
}

$rigReview = Join-Path $work "Human_Base_Rig.blend"
$null = Invoke-HumanBaseStep "rig-review-file" @($characters[0].Source, $characters[1].Source, $rigReview)
$reportPath = Join-Path $work "report.txt"
[System.IO.File]::WriteAllLines($reportPath, [string[]]$report)

Write-Host "Review file of the models: $modelReview"
Write-Host "Review file of the rigs, with the test poses as an animation: $rigReview"
Write-Host "Review sheets: $reviewSheets"
Write-Host "Quality report: $reportPath"
$toReview = 0
foreach ($character in $characters) {
    $state = if ($character.Changed) { "updated" } else { "unchanged, the committed file is kept" }
    $manifest = Read-ArtAssetManifest (Split-Path -Parent $character.Source)
    $problem = Get-ArtReviewProblem $manifest (Get-ArtFileSha256 $manifest.SourcePath)
    if ($null -eq $problem) {
        Write-Host "$($character.Asset): $state; its approval covers the file."
    }
    else {
        Write-Host "$($character.Asset): $state; $problem."
        $toReview++
    }
}

if ($toReview -gt 0) {
    Write-Host "Review the sheets and the review files, then record the approvals with tools/art/Approve-ArtAsset.ps1."
}

if ($Rig -and @($characters | Where-Object { $_.Changed }).Count -gt 0) {
    Write-Host "The rigs changed: update the animation libraries keyed on them with tools/art/Sync-AnimationLibrary.ps1 and review their clips again."
}
