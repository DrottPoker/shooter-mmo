# Exports approved art assets from art/ to their FBX target below src/Client/Assets/Art/Models,
# with the images of painted materials as PNG files below src/Client/Assets/Art/Textures, and
# records the hashes of the approved source and the written files, the materials, and the
# animation clips in asset.json. An asset
# whose review stages are not all approved for the current .blend file is refused. An animation
# library exports its armature and clips only, and records the export of the rig it was authored
# on, so it is exported after that rig. Unity imports the FBX with the rules in
# ArtModelImportRules. See docs/ART_PIPELINE.md.
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
# Animation libraries go last, so they record the export of a rig exported in the same run.
$read = @($directories | ForEach-Object { Read-ArtAssetManifest $_ })
$manifests = @($read | Where-Object { $_.Kind -ne "animations" }) + @($read | Where-Object { $_.Kind -eq "animations" })
foreach ($manifest in $manifests) {
    $problem = Get-ArtExportGateProblem $manifest (Get-ArtFileSha256 $manifest.SourcePath)
    if ($null -ne $problem) {
        throw "$($manifest.Name) cannot be exported: $problem. Review it with Render-ArtPreview.ps1 or in Blender, then record the approval with Approve-ArtAsset.ps1."
    }
}

function Export-ArtPaint([string]$Blender, $Manifest, [string]$SourceSha256) {
    # A paint layer exports one image per body and the smoothness and metallic of its paint.
    $folder = $script:ArtPaintTextureFolder
    $results = Invoke-ArtBlender $Blender $Manifest (
        @("export") + (Get-ArtValidationArguments $Manifest "paint") + @(
            "--textures", (Join-Path $script:ArtRepositoryRoot $folder)))
    $textures = ConvertTo-ArtTextureRecords @($results | Where-Object { $_ -like "ART_TEXTURE *" } |
            ForEach-Object {
                $texture = $_.Substring("ART_TEXTURE ".Length) | ConvertFrom-Json
                [pscustomobject]@{
                    name = $texture.name
                    target = Get-ArtRelativePath $texture.path
                    sha256 = Get-ArtFileSha256 $texture.path
                }
            }) $folder
    $surface = @($results | Where-Object { $_ -like "ART_PAINT *" } |
            ForEach-Object { $_.Substring("ART_PAINT ".Length) | ConvertFrom-Json }) | Select-Object -First 1
    if ($null -eq $textures -or $null -eq $surface) {
        throw "Blender reported no valid paint records for $($Manifest.Name)."
    }

    $Manifest.Export = [ordered]@{
        sourceSha256 = $SourceSha256
        textures = $textures
        smoothness = [double]$surface.smoothness
        metallic = [double]$surface.metallic
    }
    Write-ArtAssetManifest $Manifest
    Write-Host "Exported $($Manifest.Name) to $folder ($(@($textures | ForEach-Object { $_.name }) -join ', '))."
}

$blender = Resolve-ArtBlender $BlenderPath
foreach ($manifest in $manifests) {
    $sourceSha256 = Get-ArtFileSha256 $manifest.SourcePath
    $rigSourceSha256 = $null
    if ($manifest.Kind -eq "animations") {
        $rigExport = (Read-ArtAssetManifest (Split-Path -Parent $manifest.Rig.ManifestPath)).Export
        if ($null -eq $rigExport) {
            throw "$($manifest.Name) cannot be exported before its rig $($manifest.Rig.Asset) is exported."
        }

        $rigSourceSha256 = $rigExport.sourceSha256
    }

    if ($manifest.Kind -eq "paint") {
        Export-ArtPaint $blender $manifest $sourceSha256
        continue
    }

    $textureFolder = Get-ArtTextureFolder $manifest.Target
    $results = Invoke-ArtBlender $blender $manifest (
        @("export") + (Get-ArtValidationArguments $manifest $manifest.Stages[-1]) + @(
            "--target", $manifest.TargetPath, "--textures", (Join-Path $script:ArtRepositoryRoot $textureFolder)))
    $materials = @()
    if ($manifest.Kind -ne "animations") {
        $materials = ConvertTo-ArtMaterialRecords @($results | Where-Object { $_ -like "ART_MATERIAL *" } |
                ForEach-Object { $_.Substring("ART_MATERIAL ".Length) | ConvertFrom-Json })
        if ($null -eq $materials) {
            throw "Blender reported no valid material records for $($manifest.Name)."
        }
    }

    $textures = ConvertTo-ArtTextureRecords @($results | Where-Object { $_ -like "ART_TEXTURE *" } |
            ForEach-Object {
                $texture = $_.Substring("ART_TEXTURE ".Length) | ConvertFrom-Json
                [pscustomobject]@{
                    name = $texture.name
                    target = Get-ArtRelativePath $texture.path
                    sha256 = Get-ArtFileSha256 $texture.path
                }
            }) $textureFolder
    if ($null -eq $textures) {
        throw "Blender reported invalid texture records for $($manifest.Name)."
    }

    $clipRecords = $null
    if ($script:ArtAnimatedKinds -contains $manifest.Kind) {
        # In the order of the manifest, so the record reads like it.
        $reported = @($results | Where-Object { $_ -like "ART_CLIP *" } |
                ForEach-Object { $_.Substring("ART_CLIP ".Length) | ConvertFrom-Json })
        $clipRecords = ConvertTo-ArtClipRecords @($manifest.Clips | ForEach-Object {
                $clip = $_
                $reported | Where-Object { $_.name -ceq $clip }
            }) $manifest.Clips
        if ($null -eq $clipRecords) {
            throw "Blender did not report every clip of $($manifest.Name) once."
        }
    }

    # A texture that an earlier export wrote and this one no longer does is removed with its
    # Unity .meta file, so no stale image stays in the project.
    if ($null -ne $manifest.Export) {
        $written = @($textures | ForEach-Object { $_.target })
        foreach ($old in @($manifest.Export.textures | Where-Object { $written -cnotcontains $_.target })) {
            foreach ($path in @($old.target, "$($old.target).meta")) {
                $full = Join-Path $script:ArtRepositoryRoot $path
                if (Test-Path -LiteralPath $full -PathType Leaf) {
                    Remove-Item -LiteralPath $full
                }
            }
        }
    }

    $manifest.Export = [ordered]@{
        sourceSha256 = $sourceSha256
        targetSha256 = Get-ArtFileSha256 $manifest.TargetPath
        rigSourceSha256 = $rigSourceSha256
        materials = $materials
        textures = $textures
        clips = $clipRecords
    }
    Write-ArtAssetManifest $manifest
    $triangles = ($results | Where-Object { $_ -like "ART_VALIDATION *" }) -replace '^ART_VALIDATION ', ''
    $what = if ($manifest.Kind -eq "animations") { "clips=$($manifest.Clips -join ',')" } else { $triangles }
    Write-Host "Exported $($manifest.Name) to $($manifest.Target) ($what)."
}
