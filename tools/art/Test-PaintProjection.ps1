# Checks the move of a paint layer that a body of another topology than its canvas gets, through
# the garment cage and the rigs' landmarks: every body of the layer is cut into a subdivided copy,
# another topology of the same surface, and the paint moved to it through the cage is compared with
# the body's own move, exact for a body of the canvas's topology and through the cage otherwise.
# The canvas's own body must take the paint within a millimeter of its place; for the other bodies
# the script reports how far the cage places the paint from where their shared topology does, or
# for a body of another topology how much its move through the cage depends on its faces. Run it
# after a change of the move or the cage. It changes no file. See docs/ART_PIPELINE.md.
param(
    [Parameter(Mandatory = $true)]
    [string]$Asset,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

$manifest = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Asset)
if ($manifest.Kind -ne "paint") {
    throw "$($manifest.Name) is a $($manifest.Kind) asset; only a paint layer is moved through the cage."
}

$cage = @(Get-ArtPaintCage $manifest) | Select-Object -First 1
if ($null -eq $cage) {
    throw "No garment cage holds every body of $($manifest.Name)."
}

$blender = Resolve-ArtBlender $BlenderPath
$output = Invoke-ArtNative $blender @(
    "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
    "--python", $script:ArtBlenderCli, "--", "paint-projection",
    "--file", $manifest.SourcePath, "--paint", $manifest.Name, "--part", $manifest.Part,
    "--roots", (@($manifest.Bodies | ForEach-Object { $_.Root }) -join ","), "--cage", $cage.SourcePath)
foreach ($line in @($output | Where-Object { $_ -like "ART_PROJECTION *" })) {
    $report = $line.Substring("ART_PROJECTION ".Length) | ConvertFrom-Json
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $painted = if ($null -ne $report.paintedMedianMm) {
        [string]::Format($culture, ", painted texels {0} mm (median) and {1} mm (95%)", $report.paintedMedianMm, $report.paintedP95Mm)
    }
    else { "" }
    $own = if ($report.move -ceq "topology") { "the exact move" } else { "its own move through the cage" }
    Write-Host ([string]::Format($culture, "{0}: through the cage {1} mm (median) and {2} mm (95%) from {3} over {4} texels{5}.",
            $report.body, $report.medianMm, $report.p95Mm, $own, $report.texels, $painted))
}

$errors = @($output | Where-Object { $_ -like "ART_PIPELINE_ERROR *" } | ForEach-Object { $_.Substring("ART_PIPELINE_ERROR ".Length) })
if ($errors.Count -gt 0) {
    throw "The move of $($manifest.Name) through the cage is broken:`n - $($errors -join "`n - ")"
}

if ($script:ArtNativeExitCode -ne 0) {
    throw "Blender failed with exit code $script:ArtNativeExitCode for $($manifest.Name):`n$(($output | Select-Object -Last 30) -join "`n")"
}
