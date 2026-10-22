# Fits a garment to every body its asset.json names and checks it: bodies that changed in their own
# files are taken in again, every other body's version is fitted again from the first body's
# version, keeping the corrections made on it by hand, and every version gets the weights of the
# body under it, keeping weights painted by hand on the first version. A file that this leaves as it
# was is not saved, so its approvals stay valid. Then it writes the review sheet and the review file
# with the test poses, and checks every rule of the garment. See docs/ART_PIPELINE.md.
param(
    [Parameter(Mandatory = $true)]
    [string]$Asset,
    # Transfers every weight from the bodies again, also the weights painted by hand.
    [switch]$Weights,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

$manifest = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Asset)
if ($manifest.Kind -ne "garment") {
    throw "$($manifest.Name) is a $($manifest.Kind) asset; only a garment is fitted to its bodies."
}

foreach ($body in @($manifest.Bodies)) {
    $item = Get-Item -LiteralPath $body.SourcePath
    if ($item.Length -lt 1024 -and [System.IO.File]::ReadAllText($item.FullName).StartsWith("version https://git-lfs")) {
        throw "$($body.SourcePath) is a Git LFS pointer; run git lfs pull first."
    }
}

$bodyArguments = @($manifest.Bodies | ForEach-Object { @("--body", "$($_.SourcePath)=$($_.Root)") })
$blender = Resolve-ArtBlender $BlenderPath
$arguments = @(
    "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
    "--python", $script:ArtBlenderCli, "--", "garment-sync",
    "--file", $manifest.SourcePath, "--garment", $manifest.Name) + $bodyArguments
if ($Weights) {
    $arguments += "--weights"
}

$output = Invoke-ArtNative $blender $arguments
$errors = @($output | Where-Object { $_ -like "ART_PIPELINE_ERROR *" } |
    ForEach-Object { $_.Substring("ART_PIPELINE_ERROR ".Length) })
if ($errors.Count -gt 0) {
    throw "$($manifest.Name) cannot be synced:`n - $($errors -join "`n - ")"
}

$result = @($output | Where-Object { $_ -like "ART_GARMENT *" }) | Select-Object -First 1
if ($script:ArtNativeExitCode -ne 0 -or $null -eq $result) {
    throw "Blender failed with exit code $script:ArtNativeExitCode for $($manifest.Name):`n$(($output | Select-Object -Last 30) -join "`n")"
}

foreach ($line in @($output | Where-Object { $_ -like "ART_REPORT *" })) {
    Write-Host $line.Substring("ART_REPORT ".Length)
}

if ($result -like "ART_GARMENT up-to-date *") {
    Write-Host "$($manifest.Name) is up to date: every version fits the current bodies and has their weights."
}
else {
    Write-Host "Synced $($manifest.Name): $($manifest.SourcePath)"
}

# The review sheet and the review file, which Git ignores.
$previews = Join-Path $script:ArtRepositoryRoot "artifacts/art-previews"
$sheet = Join-Path $previews "$($manifest.Name).png"
Invoke-ArtBlender $blender $manifest @("preview", "--output", $sheet, "--kind", "garment") | Out-Null
$reviewFile = Join-Path $previews "$($manifest.Name)_poses.blend"
$output = Invoke-ArtNative $blender (@(
        "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
        "--python", $script:ArtBlenderCli, "--", "garment-review-file",
        "--file", $manifest.SourcePath, "--garment", $manifest.Name, "--output", $reviewFile) + $bodyArguments)
if ($script:ArtNativeExitCode -ne 0 -or @($output | Where-Object { $_ -like "ART_GARMENT_REVIEW *" }).Count -eq 0) {
    throw "Blender could not write the review file of $($manifest.Name):`n$(($output | Select-Object -Last 30) -join "`n")"
}

Write-Host "Review sheet: $sheet"
Write-Host "Review file with the test poses (play the timeline): $reviewFile"

# Every rule of both stages, so a broken one shows before the review.
Invoke-ArtBlender $blender $manifest (@("validate") + (Get-ArtValidationArguments $manifest $manifest.Stages[-1])) | Out-Null
if ($null -eq (Get-ArtExportGateProblem $manifest (Get-ArtFileSha256 $manifest.SourcePath))) {
    Write-Host "$($manifest.Name) meets every rule, and both stages are approved for this file."
}
else {
    Write-Host "$($manifest.Name) meets every rule. Review it, then record the approvals with tools/Approve-ArtAsset.ps1 -Stage model and -Stage rig."
}
