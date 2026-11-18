# Renders the clips of an animation library, or of a skinned asset with clips of its own, as videos
# below artifacts/art-previews, <Name>_<Clip>.mp4: every body the file shows, from the front right on a
# checkered ground, the clip played three times, with each tool and weapon held in the clips the
# review sheet holds it in. -Speed moves the ground back at the speed the game moves the body, so a
# foot that slides shows. Per clip it prints the clip's motion numbers: its length, how far the root
# travels and sinks, the fastest bone, the share of the joints that turn, the lowest point, and for a
# loop how far it jumps. It never saves the file. See docs/ART_PIPELINE.md.
param(
    [Parameter(Mandatory = $true)]
    [string]$Asset,
    # The clips to render; every clip of the asset by default.
    [string[]]$Clip = @(),
    # m/s the ground moves back at, such as 1.4 for Walk; 0 keeps it still.
    [double]$Speed = 0.0,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

$manifest = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Asset)
if ($manifest.Kind -ne "animations" -and -not ($manifest.Kind -eq "skinned" -and @($manifest.Clips).Count -gt 0)) {
    throw "$($manifest.Name) has no clips of its own; only an animation library or a skinned asset with clips is rendered as videos."
}

$Clip = @($Clip | Select-Object -Unique)
if ($Clip.Count -eq 0) {
    $Clip = @($manifest.Clips)
}

$unknown = @($Clip | Where-Object { $manifest.Clips -cnotcontains $_ })
if ($unknown.Count -gt 0) {
    throw "$($manifest.Name) has no clip $($unknown -join ', '); its clips are $($manifest.Clips -join ', ')."
}

if ($Speed -lt 0.0 -or $Speed -gt 20.0) {
    throw "-Speed is the speed the ground moves back at, from 0 to 20 m/s."
}

$output = Join-Path $script:ArtRepositoryRoot "artifacts/art-previews"
$arguments = @("clips", "--output", $output, "--armature", $manifest.RootObjects[0],
    "--speed", $Speed.ToString([System.Globalization.CultureInfo]::InvariantCulture))
foreach ($name in $Clip) {
    $arguments += @("--clip", $name)
}

if ($null -ne $manifest.SkeletonPath) {
    $arguments += @("--skeleton", $manifest.SkeletonPath)
}

if ($manifest.Kind -eq "animations") {
    $arguments += Get-ArtHeldAttachmentArguments $manifest
}

$blender = Resolve-ArtBlender $BlenderPath
$results = Invoke-ArtBlender $blender $manifest $arguments
foreach ($line in @($results | Where-Object { $_ -like "ART_MOTION *" })) {
    $motion = $line.Substring("ART_MOTION ".Length) | ConvertFrom-Json
    Write-Host "$(Format-ArtMotion $motion): $($motion.video)"
}
