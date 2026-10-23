# Fits the garment cage to every body its asset.json names and checks it: bodies that changed in
# their own files are taken in again, and the cage's version on every body is fitted again from its
# reference with the figure scale and the standard lines of asset.json, keeping the corrections
# made on it by hand. A file that this leaves as it was is not saved, so its approval stays valid.
# Then it writes the review sheet and checks every rule of the cage. See docs/ART_PIPELINE.md.
param(
    [string]$Asset = "art/Cages/Humanoid",
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

$manifest = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Asset)
if ($manifest.Kind -ne "cage") {
    throw "$($manifest.Name) is a $($manifest.Kind) asset; only a garment cage is fitted with this script."
}

foreach ($body in @($manifest.Bodies)) {
    $item = Get-Item -LiteralPath $body.SourcePath
    if ($item.Length -lt 1024 -and [System.IO.File]::ReadAllText($item.FullName).StartsWith("version https://git-lfs")) {
        throw "$($body.SourcePath) is a Git LFS pointer; run git lfs pull first."
    }
}

$bodyArguments = @($manifest.Bodies | ForEach-Object { @("--body", "$($_.SourcePath)=$($_.Root)") })
$blender = Resolve-ArtBlender $BlenderPath
$output = Invoke-ArtNative $blender (@(
        "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
        "--python", $script:ArtBlenderCli, "--", "cage-sync",
        "--file", $manifest.SourcePath, "--manifest", $manifest.ManifestPath) + $bodyArguments)
$errors = @($output | Where-Object { $_ -like "ART_PIPELINE_ERROR *" } |
    ForEach-Object { $_.Substring("ART_PIPELINE_ERROR ".Length) })
if ($errors.Count -gt 0) {
    throw "$($manifest.Name) cannot be synced:`n - $($errors -join "`n - ")"
}

$result = @($output | Where-Object { $_ -like "ART_CAGE *" }) | Select-Object -First 1
if ($script:ArtNativeExitCode -ne 0 -or $null -eq $result) {
    throw "Blender failed with exit code $script:ArtNativeExitCode for $($manifest.Name):`n$(($output | Select-Object -Last 30) -join "`n")"
}

foreach ($line in @($output | Where-Object { $_ -like "ART_REPORT *" })) {
    Write-Host $line.Substring("ART_REPORT ".Length)
}

if ($result -like "ART_CAGE up-to-date *") {
    Write-Host "$($manifest.Name) is up to date: every version is fitted to the current bodies with the figure scale and the lines of asset.json."
}
else {
    Write-Host "Synced $($manifest.Name): $($manifest.SourcePath)"
}

# The review sheet, which Git ignores.
$sheet = Join-Path $script:ArtRepositoryRoot "artifacts/art-previews/$($manifest.Name).png"
Invoke-ArtBlender $blender $manifest @("preview", "--output", $sheet, "--kind", "cage", "--roots",
    ($manifest.RootObjects -join ","), "--manifest", $manifest.ManifestPath) | Out-Null
Write-Host "Review sheet: $sheet"

# Every rule of the cage, so a broken one shows before the review.
Invoke-ArtBlender $blender $manifest (@("validate") + (Get-ArtValidationArguments $manifest "model")) | Out-Null
if ($null -eq (Get-ArtExportGateProblem $manifest (Get-ArtFileSha256 $manifest.SourcePath))) {
    Write-Host "$($manifest.Name) meets every rule, and its stage is approved for this file."
}
else {
    Write-Host "$($manifest.Name) meets every rule. Review it, then record the approval with tools/Approve-ArtAsset.ps1 -Asset $($manifest.RelativeDirectory) -Stage model."
}
