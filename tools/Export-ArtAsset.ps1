# Exports approved art assets from art/ to their FBX target below src/Client/Assets/Art/Models
# and records the hashes of the approved source and the written FBX in asset.json. An asset
# whose review stages are not all approved for the current .blend file is refused. Unity
# imports the FBX with the rules in ArtModelImportRules. See docs/ART_PIPELINE.md.
param(
    [string[]]$Asset,
    # Export every asset below art/.
    [switch]$All,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

if ($All -eq ($Asset.Count -gt 0)) {
    throw "Pass either -Asset <folder> or -All."
}

$directories = if ($All) { Get-ArtAssetDirectories } else { @($Asset | ForEach-Object { Resolve-ArtAssetDirectory $_ }) }
$manifests = @($directories | ForEach-Object { Read-ArtAssetManifest $_ })
foreach ($manifest in $manifests) {
    $problem = Get-ArtExportGateProblem $manifest (Get-ArtFileSha256 $manifest.SourcePath)
    if ($null -ne $problem) {
        throw "$($manifest.Name) cannot be exported: $problem. Review it with Render-ArtPreview.ps1 or in Blender, then record the approval with Approve-ArtAsset.ps1."
    }
}

$blender = Resolve-ArtBlender $BlenderPath
foreach ($manifest in $manifests) {
    $sourceSha256 = Get-ArtFileSha256 $manifest.SourcePath
    $results = Invoke-ArtBlender $blender $manifest (
        @("export") + (Get-ArtValidationArguments $manifest) + @("--target", $manifest.TargetPath))
    $materials = @(ConvertTo-ArtMaterialRecords @($results | Where-Object { $_ -like "ART_MATERIAL *" } |
            ForEach-Object { $_.Substring("ART_MATERIAL ".Length) | ConvertFrom-Json }))
    if ($materials.Count -eq 0 -or @($materials | Where-Object { $null -eq $_ }).Count -gt 0) {
        throw "Blender reported no valid material records for $($manifest.Name)."
    }

    $manifest.Export = [ordered]@{
        sourceSha256 = $sourceSha256
        targetSha256 = Get-ArtFileSha256 $manifest.TargetPath
        materials = $materials
    }
    Write-ArtAssetManifest $manifest
    $triangles = ($results | Where-Object { $_ -like "ART_VALIDATION *" }) -replace '^ART_VALIDATION ', ''
    Write-Host "Exported $($manifest.Name) to $($manifest.Target) ($triangles)."
}
