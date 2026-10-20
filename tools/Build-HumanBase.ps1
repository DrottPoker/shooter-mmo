# Builds the human base characters from the Human Base Meshes bundle and writes the art assets
# art/Characters/Human_Male/Human_Male.blend and art/Characters/Human_Female/Human_Female.blend,
# plus a review file with both characters side by side, the review sheets, and a quality report.
# The bundle is a download and is read as data only: every Blender step runs with factory
# settings and with the file's scripts disabled. A rebuilt asset replaces the committed file only
# when its content changed, and then needs a new review and approval with Approve-ArtAsset.ps1.
# See docs/ART_PIPELINE.md.
param(
    # human_base_meshes_bundle.blend of Human Base Meshes v1.4.1 (Blender Studio, CC0).
    [Parameter(Mandatory = $true)]
    [string]$ReferenceBundle,
    # Work files, review file, sheets, and report. Defaults to artifacts/human-base, which Git ignores.
    [string]$WorkDirectory,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

# The build is fitted to this bundle; another version would give another model.
$expectedBundleSha256 = "3c121505651140ceb4d69fd1d8923f7788ffadd81672f5be14845a5f2c75c137"
$cli = Join-Path $PSScriptRoot "blender/human_base_cli.py"
$characters = @(
    [pscustomobject]@{ Label = "Male"; Asset = "Human_Male" },
    [pscustomobject]@{ Label = "Female"; Asset = "Human_Female" })

$bundle = (Resolve-Path -LiteralPath $ReferenceBundle).Path
$bundleSha256 = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash.ToLowerInvariant()
if ($bundleSha256 -ne $expectedBundleSha256) {
    throw "'$bundle' is not human_base_meshes_bundle.blend of Human Base Meshes v1.4.1 (its SHA-256 is $bundleSha256)."
}

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

    Write-Host ("{0,-16} {1,5:0} s" -f $LogName, ((Get-Date) - $started).TotalSeconds)
    return $output
}

function Test-GitLfsPointer([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    return $item.Length -lt 1024 -and [System.IO.File]::ReadAllText($item.FullName).StartsWith("version https://git-lfs")
}

$refs = Join-Path $work "refs.blend"
$geometry = Join-Path $work "geometry.blend"
$textured = Join-Path $work "textured.blend"
$review = Join-Path $work "Human_Base.blend"
$null = Invoke-HumanBaseStep "references" @($bundle, $refs)
$null = Invoke-HumanBaseStep "geometry" @($refs, $geometry)
$null = Invoke-HumanBaseStep "textures" @($geometry, $refs, $textured, (Join-Path $work "textures"))
$report.Add("")
$null = Invoke-HumanBaseStep "check-mesh" @($textured)
$null = Invoke-HumanBaseStep "check-seams" @($textured)
$null = Invoke-HumanBaseStep "characters" @($textured, $review)
$report.Add("")
$null = Invoke-HumanBaseStep "check-parts" @($review)
$report.Add("")

# Blender's files differ in their bytes from run to run, so a rebuilt asset only replaces the
# committed file when its content changed, and an unchanged rebuild keeps its approval.
$assetWork = Join-Path $work "assets"
New-Item -ItemType Directory -Force -Path $assetWork | Out-Null
foreach ($character in $characters) {
    $built = Join-Path $assetWork "$($character.Asset).blend"
    $null = Invoke-HumanBaseStep "asset" @($review, $character.Label, $built) "asset_$($character.Label)"
    $directory = Join-Path $script:ArtSourceRoot "Characters/$($character.Asset)"
    $source = Join-Path $directory "$($character.Asset).blend"
    $same = $false
    if (Test-Path -LiteralPath $source) {
        if (Test-GitLfsPointer $source) {
            throw "$source is a Git LFS pointer; run git lfs pull first."
        }

        $output = Invoke-HumanBaseStep "compare" @($source, $built) "compare_$($character.Label)"
        $same = @($output | Where-Object { $_ -eq "HUMAN_BASE_SAME yes" }).Count -gt 0
    }

    if (-not $same) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
        Copy-Item -LiteralPath $built -Destination $source -Force
    }

    $character | Add-Member -NotePropertyName Changed -NotePropertyValue (-not $same)
}

$report.Add("")
$null = Invoke-HumanBaseStep "review" @($review, (Join-Path $work "review"), "Human_Base")
$reportPath = Join-Path $work "report.txt"
[System.IO.File]::WriteAllLines($reportPath, [string[]]$report)

Write-Host "Review file: $review"
Write-Host "Review sheets: $(Join-Path $work "review")"
Write-Host "Quality report: $reportPath"
$toReview = 0
foreach ($character in $characters) {
    $directory = Join-Path $script:ArtSourceRoot "Characters/$($character.Asset)"
    $state = if ($character.Changed) { "rebuilt with changes" } else { "unchanged, the committed file is kept" }
    if (-not (Test-Path -LiteralPath (Join-Path $directory "asset.json"))) {
        Write-Host "$($character.Asset): $state; a new asset without asset.json."
        $toReview++
        continue
    }

    $manifest = Read-ArtAssetManifest $directory
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
    Write-Host "Review the sheets and the review file, then record the approvals with tools/Approve-ArtAsset.ps1."
}
