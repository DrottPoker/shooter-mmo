# Builds or updates the .blend file of an animation library from the characters its asset.json
# names: the rig's armature, renamed after the library's root object, in the Export collection
# to key the clips on, and in the Reference collection the rig's body bound to it, every preview
# character copying its pose, and a ground grid. A library whose armature is already the current
# rig is left as it is. Otherwise its armature and bodies are replaced and every action is kept,
# so the file changes and needs a new review and approval. See docs/ART_PIPELINE.md.
param(
    [Parameter(Mandatory = $true)]
    [string]$Asset,
    # Rebuilds the armature and the bodies even when the rig did not change.
    [switch]$Force,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

$manifest = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Asset) -AllowMissingSource
if ($manifest.Kind -ne "animations") {
    throw "$($manifest.Name) is a $($manifest.Kind) asset; only an animation library is synced from its rig."
}

foreach ($character in @($manifest.Rig) + @($manifest.Previews)) {
    $item = Get-Item -LiteralPath $character.SourcePath
    if ($item.Length -lt 1024 -and [System.IO.File]::ReadAllText($item.FullName).StartsWith("version https://git-lfs")) {
        throw "$($character.SourcePath) is a Git LFS pointer; run git lfs pull first."
    }
}

$arguments = @(
    "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
    "--python", $script:ArtBlenderCli, "--", "library-sync",
    "--library", $manifest.SourcePath,
    "--armature", $manifest.RootObjects[0],
    "--rig-source", $manifest.Rig.SourcePath,
    "--rig-root", $manifest.Rig.Root)
foreach ($preview in @($manifest.Previews)) {
    $arguments += @("--preview", "$($preview.SourcePath)=$($preview.Root)")
}

if ($Force) {
    $arguments += "--force"
}

$blender = Resolve-ArtBlender $BlenderPath
$output = Invoke-ArtNative $blender $arguments
$errors = @($output | Where-Object { $_ -like "ART_PIPELINE_ERROR *" } |
    ForEach-Object { $_.Substring("ART_PIPELINE_ERROR ".Length) })
if ($errors.Count -gt 0) {
    throw "$($manifest.Name) cannot be synced:`n - $($errors -join "`n - ")"
}

$result = @($output | Where-Object { $_ -like "ART_LIBRARY *" }) | Select-Object -First 1
if ($script:ArtNativeExitCode -ne 0 -or $null -eq $result) {
    throw "Blender failed with exit code $script:ArtNativeExitCode for $($manifest.Name):`n$(($output | Select-Object -Last 30) -join "`n")"
}

if ($result -like "ART_LIBRARY up-to-date *") {
    Write-Host "$($manifest.Name) is up to date: its armature is the current rig of $($manifest.Rig.Asset)."
}
else {
    Write-Host "Synced $($manifest.Name) from $($manifest.Rig.Asset): $($manifest.SourcePath)"
    Write-Host "Review the clips on the new rig, then record the approval with tools/Approve-ArtAsset.ps1 -Stage animation."
}
