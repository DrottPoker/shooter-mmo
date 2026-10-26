# Renders the review sheet of an art asset: one row per exported root object, seen from four
# sides at the game camera pitch, plus a view with a 1.8 m mannequin for scale. The sheet of an
# animation library shows every clip as frames on its preview bodies instead, with each tool held
# in the clip it is used with, the sheet of a
# garment template shows it on every body at rest and in the test poses, the sheet of a garment
# texture shows it on its template, the sheet of the garment cage shows it on every body with
# its distance from the skin and its standard lines, and the sheet of a paint layer shows it on
# every body with the underwear the body always wears, up close where it paints, and its canvas.
# The sheet is what a reviewer looks at before Approve-ArtAsset.ps1. See docs/ART_PIPELINE.md.
param(
    [Parameter(Mandatory = $true)]
    [string]$Asset,
    # Defaults to artifacts/art-previews/<Name>.png, which Git ignores.
    [string]$Output,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

$manifest = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Asset)
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $script:ArtRepositoryRoot "artifacts/art-previews/$($manifest.Name).png"
}

$Output = [System.IO.Path]::GetFullPath($Output)
$blender = Resolve-ArtBlender $BlenderPath
$arguments = @("preview", "--output", $Output, "--kind", $manifest.Kind)
if ($manifest.Kind -eq "garment") {
    # The sheet of a garment also shows what it covers, at rest and in the clips.
    $arguments += @("--garment", $manifest.Name) + (Get-ArtGarmentCoverArguments $manifest) +
        (Get-ArtGarmentClipArguments $manifest)
}
elseif ($null -ne $manifest.Socket) {
    # The sheet of an attachment also shows it held on its socket by the bodies of the animation
    # library of its skeleton, in the first frame of Idle with the hand pose Grip when the library has it.
    $library = Get-ArtHeldLibrary $manifest.Socket.Skeleton
    if ($null -eq $library) {
        throw "No animation library of the $($manifest.Socket.Skeleton) skeleton has the clip Idle to show $($manifest.Name) held."
    }

    $arguments += @("--socket-bone", $manifest.Socket.Bone, "--held-library", "$($library.SourcePath)=$($library.Root)")
}
elseif ($manifest.Kind -eq "animations") {
    # The sheet of an animation library shows each tool held in the rows of the clip it is used with.
    foreach ($directory in @(Get-ArtAssetDirectories)) {
        $json = Get-Content -Raw -LiteralPath (Join-Path $directory "asset.json") | ConvertFrom-Json
        if ($null -eq $json.PSObject.Properties['useClip'] -or $manifest.Clips -cnotcontains [string]$json.useClip) {
            continue
        }

        $tool = Read-ArtAssetManifest $directory
        if ($tool.Socket.Skeleton -ceq $manifest.Skeleton) {
            $arguments += @("--held-attachment", "$($tool.SourcePath)=$($tool.UseClip):$($tool.Socket.Bone)")
        }
    }
}
elseif ($manifest.Kind -eq "cage") {
    $arguments += @("--roots", ($manifest.RootObjects -join ","), "--manifest", $manifest.ManifestPath)
}
elseif ($manifest.Kind -eq "paint") {
    # The sheet of a paint layer shows it with the underwear its bodies always wear, as the game does.
    $arguments += @("--paint", $manifest.Name, "--part", $manifest.Part, "--layer", $manifest.Layer,
        "--roots", (@($manifest.Bodies | ForEach-Object { $_.Root }) -join ","))
    $assets = @($manifest.Bodies | ForEach-Object { $_.Asset })
    foreach ($directory in @(Get-ArtAssetDirectories)) {
        $json = Get-Content -Raw -LiteralPath (Join-Path $directory "asset.json") | ConvertFrom-Json
        $other = Split-Path -Leaf $directory
        if ([string]$json.kind -eq "paint" -and [string]$json.category -eq "underwear" -and [string]$json.part -ceq $manifest.Part -and
            $other -cne $manifest.Name -and @($json.bodies | Where-Object { $assets -ccontains [string]$_ }).Count -gt 0) {
            $arguments += @("--other-paint", "$(Join-Path $directory "$other.blend")=${other}:$($json.layer)")
        }
    }
}
elseif ($manifest.Kind -eq "texture") {
    # The sheet of a texture is rendered in its template's file, with the texture on every version.
    # A texture with transparent parts also shows its own masks, at rest and in the clips.
    $arguments += @("--roots", ($manifest.RootObjects -join ","), "--garment", $manifest.Garment.Name,
        "--texture", $manifest.Name, "--texture-file", $manifest.SourcePath) +
        (Get-ArtGarmentCoverArguments $manifest.Garment) + (Get-ArtGarmentClipArguments $manifest)
    Invoke-ArtBlender $blender $manifest.Garment $arguments | Out-Null
    Write-Host "Review sheet for $($manifest.Name): $Output"
    return
}

Invoke-ArtBlender $blender $manifest $arguments | Out-Null
Write-Host "Review sheet for $($manifest.Name): $Output"
