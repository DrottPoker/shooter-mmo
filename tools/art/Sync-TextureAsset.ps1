# Brings a garment texture up to date with its template and checks it: when the template's faces or
# UVs changed, the texture moves to the new UVs by correspondence: its preview, which keeps the UVs
# it was painted in, follows the template, and every texel takes the paint of the nearest point of
# the moved preview. A file that this leaves as it was is not saved. Then it writes the review sheet,
# the texture on every body of its template, checks every rule of the texture, records the content of
# the file, and says whether its approval holds. See docs/ART_PIPELINE.md.
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
    "--garment", $manifest.Garment.Name, "--label", $manifest.RootObjects[0], "--manifest", $manifest.Garment.ManifestPath)
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
    $output | Where-Object { $_ -like "ART_TEXTURE_NOTE *" } |
        ForEach-Object { Write-Host $_.Substring("ART_TEXTURE_NOTE ".Length) }
}

# The review sheet, which Git ignores.
$sheet = Join-Path $script:ArtRepositoryRoot "artifacts/art-previews/$($manifest.Name).png"
& (Join-Path $PSScriptRoot "Render-ArtPreview.ps1") -Asset $manifest.Directory -Output $sheet -BlenderPath $blender | Out-Null
Write-Host "Review sheet: $sheet"

# Every rule of the texture, so a broken one shows before the review, and the content of the file,
# and of its template's, whose UVs it is compared with.
$validation = Invoke-ArtBlender $blender $manifest (@("validate") + (Get-ArtValidationArguments $manifest "texture"))
$null = Update-ArtContentRecord $blender $manifest $validation
$null = Update-ArtContentRecord $blender $manifest.Garment
foreach ($line in @(Get-ArtStageReport $manifest)) {
    Write-Host $line
}

if ($null -eq (Get-ArtExportGateProblem $manifest) -and $null -eq (Get-ArtTextureTemplateProblem $manifest)) {
    Write-Host "$($manifest.Name) meets every rule, and its stage is approved for this content in the current UVs of $($manifest.Garment.Name)."
}
else {
    Write-Host "$($manifest.Name) meets every rule. Review it, then record the approval with tools/art/Approve-ArtAsset.ps1 -Asset $($manifest.RelativeDirectory) -Stage texture."
}
