# Builds or updates the .blend file of a paint layer from the bodies its asset.json names. The paint
# is kept on the layer's canvas, a copy of the body it is painted on with an image of its own, which
# a new file copies from the first body and which never changes with the bodies. Every body gets
# one joined mesh in its rest pose, drawn with the paint over its skin, and its image
# T_Paint_<Name>_<Label>: the canvas's paint moved to the body by projection, exactly through the
# shared topology or through the garment cage for a body of another topology, and cropped to its
# paint. A file whose bodies and cage are as their files hold them and whose moved paint is current
# is left as it is. Run it again after painting the canvas, so the bodies show the new paint; then
# the file needs a new review and approval. See docs/ART_PIPELINE.md.
param(
    [Parameter(Mandatory = $true)]
    [string]$Asset,
    # Moves the canvas to this body of the layer, such as Human_Female or a new base model, with
    # the paint moved to it, so the paint is painted on that body from then on.
    [string]$Canvas,
    # Rebuilds the bodies even when the file is current.
    [switch]$Force,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

$manifest = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Asset) -AllowMissingSource
if ($manifest.Kind -ne "paint") {
    throw "$($manifest.Name) is a $($manifest.Kind) asset; only a paint layer is synced from its bodies."
}

function Assert-ArtFileDownloaded([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -lt 1024 -and [System.IO.File]::ReadAllText($item.FullName).StartsWith("version https://git-lfs")) {
        throw "$Path is a Git LFS pointer; run git lfs pull first."
    }
}

$arguments = @(
    "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
    "--python", $script:ArtBlenderCli, "--", "paint-sync",
    "--file", $manifest.SourcePath, "--paint", $manifest.Name, "--part", $manifest.Part)
foreach ($body in @($manifest.Bodies)) {
    Assert-ArtFileDownloaded $body.SourcePath
    $arguments += @("--body", "$($body.SourcePath)=$($body.Root)")
}

# The canvas keeps its body's version of the garment cage, and a body of another topology than the
# canvas is reached through the cage.
$cage = @(Get-ArtPaintCage $manifest) | Select-Object -First 1
if ($null -ne $cage) {
    Assert-ArtFileDownloaded $cage.SourcePath
    $arguments += @("--cage", $cage.SourcePath)
}

if (-not [string]::IsNullOrEmpty($Canvas)) {
    $target = @($manifest.Bodies | Where-Object { $_.Asset -ceq $Canvas -or $_.Root -ceq $Canvas -or
            (Split-Path -Leaf $_.Asset) -ceq $Canvas }) | Select-Object -First 1
    if ($null -eq $target) {
        throw "$Canvas is not a body of $($manifest.Name): $(@($manifest.Bodies | ForEach-Object { $_.Asset }) -join ', ')."
    }

    $arguments += @("--canvas", $target.Root)
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

$result = @($output | Where-Object { $_ -like "ART_PAINT_SYNC *" }) | Select-Object -First 1
if ($script:ArtNativeExitCode -ne 0 -or $null -eq $result) {
    throw "Blender failed with exit code $script:ArtNativeExitCode for $($manifest.Name):`n$(($output | Select-Object -Last 30) -join "`n")"
}

if ($result -like "ART_PAINT_SYNC up-to-date *") {
    Write-Host "$($manifest.Name) is up to date: every body is there and shows the current paint of the canvas."
}
else {
    Write-Host "Synced $($manifest.Name): $($manifest.SourcePath)"
    Write-Host "Review the paint on every body, then record the approval with tools/art/Approve-ArtAsset.ps1 -Stage paint."
}
