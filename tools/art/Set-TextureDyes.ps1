# Makes a garment texture dyeable, or sets its dyes again: the dye mask marks the texels each dye
# channel tints, and each channel's natural color, its region's middle tone over the dye level, is
# what the undyed garment wears. The image is divided by its natural colors where the mask reaches,
# so the texture undyed looks exactly as it was painted and a dye color gives the same cloth on every
# dyeable texture. Then it writes the review sheet and checks every rule, like Sync-TextureAsset.ps1.
# See docs/ART_PIPELINE.md.
param(
    [Parameter(Mandatory = $true)]
    [string]$Asset,
    # A PNG of the texture's size whose red, green, and blue mark the texels each channel tints. Without
    # it, the first channel tints every texel.
    [string]$Regions,
    # The linear level of a region's middle tone in the neutral image, the same for every dyeable texture.
    [double]$Level = 0.7,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

$manifest = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Asset)
if ($manifest.Kind -ne "texture") {
    throw "$($manifest.Name) is a $($manifest.Kind) asset; only a garment texture is dyed with this script."
}

if ($manifest.Garment.Dyes -lt 1) {
    throw "The template $($manifest.Garment.Name) allows no dye channels; set its `"dyes`" first."
}

$arguments = @("--file", $manifest.SourcePath, "--texture", $manifest.Name, "--level", $Level.ToString([System.Globalization.CultureInfo]::InvariantCulture))
if (-not [string]::IsNullOrEmpty($Regions)) {
    $arguments += @("--regions", (Resolve-Path -LiteralPath $Regions).Path)
}

$blender = Resolve-ArtBlender $BlenderPath
$output = Invoke-ArtNative $blender (@(
        "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
        "--python", $script:ArtBlenderCli, "--", "texture-dyes") + $arguments)
$errors = @($output | Where-Object { $_ -like "ART_PIPELINE_ERROR *" } |
    ForEach-Object { $_.Substring("ART_PIPELINE_ERROR ".Length) })
if ($errors.Count -gt 0) {
    throw "$($manifest.Name) cannot be dyed:`n - $($errors -join "`n - ")"
}

if ($script:ArtNativeExitCode -ne 0 -or @($output | Where-Object { $_ -like "ART_TEXTURE_FILE dyed *" }).Count -ne 1) {
    throw "Blender failed with exit code $script:ArtNativeExitCode for $($manifest.Name):`n$(($output | Select-Object -Last 30) -join "`n")"
}

foreach ($line in @($output | Where-Object { $_ -like "ART_REPORT *" })) {
    Write-Host $line.Substring("ART_REPORT ".Length)
}

# The review sheet, which Git ignores, and every rule of the texture.
& (Join-Path $PSScriptRoot "Sync-TextureAsset.ps1") -Asset $manifest.Directory -BlenderPath $blender
