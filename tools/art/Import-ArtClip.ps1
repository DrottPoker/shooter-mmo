# Adds one clip to an animation library on the Humanoid skeleton from clips of another skeleton, such
# as a clip library in a .glb file, as a starting point that a person then edits by hand, like the
# gait model's clips of Sync-AnimationLibrary.ps1 -Generate. The source's clips play one after another,
# and every frame turns each Humanoid bone as the map file's source bone turns, with the limbs, hands,
# and fingers aligned to the source's rest pose, the hips moving as the source's do, scaled to the
# legs, and the legs solved again so the feet stay where the source's are. -Grip gives the fingers of
# a hand that holds something the library's Grip clip. The script never replaces a clip: a library
# that has the clip stops it. It adds the clip to asset.json, prints its motion numbers, and the
# clip then needs a review and approval. See docs/ART_PIPELINE.md.
param(
    [Parameter(Mandatory = $true)]
    [string]$Asset,
    # A .glb, .gltf, .fbx, or .bvh file with the clips.
    [Parameter(Mandatory = $true)]
    [string]$Source,
    # The map from the Humanoid bones to the source's bones, such as tools/art/retarget/UniversalAnimationLibrary.json.
    [Parameter(Mandatory = $true)]
    [string]$Map,
    # The source's clips, played one after another.
    [Parameter(Mandatory = $true)]
    [string[]]$Clip,
    # The name of the new clip.
    [Parameter(Mandatory = $true)]
    [string]$Name,
    # The hands whose fingers take the Grip clip: L, R, or LR.
    [ValidateSet("L", "R", "LR")]
    [string]$Grip,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

$manifest = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Asset)
if ($manifest.Kind -ne "animations") {
    throw "$($manifest.Name) is a $($manifest.Kind) asset; a clip is imported into an animation library."
}

if ($manifest.Skeleton -ne "Humanoid") {
    throw "A clip is imported onto the Humanoid skeleton only; $($manifest.Name) has the $($manifest.Skeleton) skeleton."
}

if ($Name -cnotmatch $script:ArtNamePattern) {
    throw "The clip name '$Name' must use only letters, digits, and underscores, and start with a letter."
}

if ($manifest.Clips -ccontains $Name) {
    throw "${Name}: $($manifest.Name) has the clip already, and a clip is never replaced. Choose another -Name."
}

$sourcePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Source)
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf) -or
    @(".glb", ".gltf", ".fbx", ".bvh") -notcontains [System.IO.Path]::GetExtension($sourcePath).ToLowerInvariant()) {
    throw "-Source must be a .glb, .gltf, .fbx, or .bvh file; '$Source' is not one."
}

$mapPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Map)
if (-not (Test-Path -LiteralPath $mapPath -PathType Leaf)) {
    throw "-Map must be a map file, such as tools/art/retarget/UniversalAnimationLibrary.json; '$Map' is not one."
}

$Clip = @($Clip | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($Clip.Count -eq 0) {
    throw "-Clip must name at least one clip of the source."
}

$grip = if ([string]::IsNullOrEmpty($Grip)) { "-" } else { $Grip.ToUpperInvariant() }
$blender = Resolve-ArtBlender $BlenderPath
$humanBase = Join-Path $PSScriptRoot "blender/human_base_cli.py"
$output = Invoke-ArtNative $blender (@(
        "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
        "--python", $humanBase, "--") + @("import-clip", $manifest.SourcePath, $manifest.RootObjects[0], $sourcePath, $mapPath, ($Clip -join "+"), $Name, $grip, $manifest.SkeletonPath))
$failures = @($output | Where-Object { $_ -like "HUMAN_BASE_FAILED *" } |
    ForEach-Object { $_.Substring("HUMAN_BASE_FAILED ".Length) })
if ($failures.Count -gt 0) {
    throw "$Name cannot be imported into $($manifest.Name):`n - $($failures -join "`n - ")"
}

$motion = @($output | Where-Object { $_ -like "HUMAN_BASE_MOTION *" }) | Select-Object -First 1
if ($script:ArtNativeExitCode -ne 0 -or $null -eq $motion) {
    throw "Blender failed with exit code $script:ArtNativeExitCode for $($manifest.Name):`n$(($output | Select-Object -Last 30) -join "`n")"
}

foreach ($line in @($output | Where-Object { $_ -like "HUMAN_BASE_REPORT *" })) {
    Write-Host $line.Substring("HUMAN_BASE_REPORT ".Length)
}

# The record of the last export no longer lists every clip; the next export writes it.
$manifest.Clips = [string[]](@($manifest.Clips) + $Name)
$manifest.Export = $null
Write-ArtAssetManifest $manifest
Write-Host "Added $Name to $($manifest.Name) and its asset.json; edit it in Blender as you like."
Write-Host (Format-ArtMotion ($motion.Substring("HUMAN_BASE_MOTION ".Length) | ConvertFrom-Json))
Write-Host "Review the clips, then record the approval with tools/art/Approve-ArtAsset.ps1 -Stage animation."
