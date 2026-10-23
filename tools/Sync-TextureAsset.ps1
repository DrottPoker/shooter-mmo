# Brings a garment texture up to date with its template and checks it: when the template's UVs
# changed, the texture moves to the new UVs by projection from its preview, which keeps the UVs it
# was painted in. A file that this leaves as it was is not saved, so its approval stays valid. Then
# it writes the review sheet, the texture on every body of its template, and checks every rule of
# the texture. See docs/ART_PIPELINE.md.
param(
    [Parameter(Mandatory = $true)]
    [string]$Asset,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

$manifest = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Asset)
if ($manifest.Kind -ne "texture") {
    throw "$($manifest.Name) is a $($manifest.Kind) asset; only a garment texture is synced with this script."
}

$blender = Resolve-ArtBlender $BlenderPath
$output = Invoke-ArtNative $blender @(
    "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
    "--python", $script:ArtBlenderCli, "--", "texture-sync",
    "--file", $manifest.SourcePath, "--texture", $manifest.Name, "--garment-file", $manifest.Garment.SourcePath,
    "--garment", $manifest.Garment.Name, "--label", $manifest.RootObjects[0])
$errors = @($output | Where-Object { $_ -like "ART_PIPELINE_ERROR *" } |
    ForEach-Object { $_.Substring("ART_PIPELINE_ERROR ".Length) })
if ($errors.Count -gt 0) {
    throw "$($manifest.Name) cannot be synced:`n - $($errors -join "`n - ")"
}

$result = @($output | Where-Object { $_ -like "ART_TEXTURE_FILE *" }) | Select-Object -First 1
if ($script:ArtNativeExitCode -ne 0 -or $null -eq $result) {
    throw "Blender failed with exit code $script:ArtNativeExitCode for $($manifest.Name):`n$(($output | Select-Object -Last 30) -join "`n")"
}

if ($result -like "ART_TEXTURE_FILE up-to-date *") {
    Write-Host "$($manifest.Name) is up to date: it is painted in the current UVs of $($manifest.Garment.Name)."
}
else {
    Write-Host "Moved $($manifest.Name) to the new UVs of $($manifest.Garment.Name): $($manifest.SourcePath)"
}

# The review sheet, which Git ignores.
$sheet = Join-Path $script:ArtRepositoryRoot "artifacts/art-previews/$($manifest.Name).png"
& (Join-Path $PSScriptRoot "Render-ArtPreview.ps1") -Asset $manifest.Directory -Output $sheet -BlenderPath $blender | Out-Null
Write-Host "Review sheet: $sheet"

# Every rule of the texture, so a broken one shows before the review.
Invoke-ArtBlender $blender $manifest (@("validate") + (Get-ArtValidationArguments $manifest "texture")) | Out-Null
if ($null -eq (Get-ArtExportGateProblem $manifest (Get-ArtFileSha256 $manifest.SourcePath))) {
    Write-Host "$($manifest.Name) meets every rule, and its stage is approved for this file."
}
else {
    Write-Host "$($manifest.Name) meets every rule. Review it, then record the approval with tools/Approve-ArtAsset.ps1 -Asset $($manifest.RelativeDirectory) -Stage texture."
}
