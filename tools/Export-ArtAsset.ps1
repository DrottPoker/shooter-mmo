# Exports approved art assets from art/ to their FBX target below src/Client/Assets/Art/Models,
# with the images of painted materials as PNG files below src/Client/Assets/Art/Textures, and
# records the hashes of the approved source and the written files, the materials, and the
# animation clips in asset.json. An asset
# whose review stages are not all approved for the current .blend file is refused. An animation
# library exports its armature and clips only, and records the export of the rig it was authored
# on, so it is exported after that rig. A paint layer exports its images only, and a garment one
# model per body below src/Client/Assets/Art/Models/Garments. Unity imports the FBX with the rules
# in ArtModelImportRules. See docs/ART_PIPELINE.md.
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
# Animation libraries come after the rigs they record the export of, and garments last, because
# they record the exports of their bodies and of the libraries they were checked in.
$read = @($directories | ForEach-Object { Read-ArtAssetManifest $_ })
$manifests = @($read | Where-Object { @("animations", "garment") -notcontains $_.Kind }) +
    @($read | Where-Object { $_.Kind -eq "animations" }) + @($read | Where-Object { $_.Kind -eq "garment" })
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

function Remove-ArtStaleFiles([string[]]$Old, [string[]]$Written) {
    # A file that an earlier export wrote and this one no longer does is removed with its Unity
    # .meta file, so nothing stale stays in the project.
    foreach ($target in @($Old | Where-Object { $Written -cnotcontains $_ })) {
        foreach ($path in @($target, "$target.meta")) {
            $full = Join-Path $script:ArtRepositoryRoot $path
            if (Test-Path -LiteralPath $full -PathType Leaf) {
                Remove-Item -LiteralPath $full
            }
        }
    }
}

function Export-ArtGarment([string]$Blender, $Manifest, [string]$SourceSha256) {
    # A garment exports one model per body, each with that body's armature and its data, and its
    # images. It records the exports of the bodies and the animation libraries it was checked
    # against, so a change of either needs a new export of the garment.
    $bodyExports = @{}
    foreach ($body in @($Manifest.Bodies)) {
        $bodyExport = (Read-ArtAssetManifest (Split-Path -Parent $body.ManifestPath)).Export
        if ($null -eq $bodyExport) {
            throw "$($Manifest.Name) cannot be exported before its body $($body.Asset) is exported."
        }

        $bodyExports[$body.Root] = [string]$bodyExport.sourceSha256
    }

    $clipSources = @(Get-ArtClipLibraries $Manifest.Skeleton | ForEach-Object {
            if ($null -eq $_.Export) {
                throw "$($Manifest.Name) cannot be exported before the animation library $($_.Asset) is exported."
            }

            [ordered]@{ library = $_.Asset; sourceSha256 = [string]$_.Export.sourceSha256 }
        })
    $folder = $script:ArtGarmentTextureFolder
    $results = Invoke-ArtBlender $Blender $Manifest (
        @("export") + (Get-ArtValidationArguments $Manifest $Manifest.Stages[-1]) + @(
            "--target", (Join-Path $script:ArtRepositoryRoot $script:ArtGarmentModelFolder),
            "--textures", (Join-Path $script:ArtRepositoryRoot $folder)))
    $reported = @($results | Where-Object { $_ -like "ART_GARMENT_FBX *" } |
            ForEach-Object { $_.Substring("ART_GARMENT_FBX ".Length) | ConvertFrom-Json })
    $models = @($Manifest.Models | ForEach-Object {
            $model = $_
            $record = @($reported | Where-Object { [string]$_.label -ceq $model.Body })
            if ($record.Count -ne 1 -or (Get-ArtRelativePath $record[0].path) -cne $model.Target -or
                (Get-ArtRelativePath $record[0].data) -cne $model.Data) {
                throw "Blender did not report the model $($model.Target) and its data of $($Manifest.Name) once."
            }

            [ordered]@{
                body = $model.Body
                bodySourceSha256 = $bodyExports[$model.Body]
                target = $model.Target
                sha256 = Get-ArtFileSha256 $model.TargetPath
                data = $model.Data
                dataSha256 = Get-ArtFileSha256 $model.DataPath
            }
        })
    $materials = ConvertTo-ArtMaterialRecords @($results | Where-Object { $_ -like "ART_MATERIAL *" } |
            ForEach-Object { $_.Substring("ART_MATERIAL ".Length) | ConvertFrom-Json })
    $textures = ConvertTo-ArtTextureRecords @($results | Where-Object { $_ -like "ART_TEXTURE *" } |
            ForEach-Object {
                $texture = $_.Substring("ART_TEXTURE ".Length) | ConvertFrom-Json
                [pscustomobject]@{
                    name = $texture.name
                    target = Get-ArtRelativePath $texture.path
                    sha256 = Get-ArtFileSha256 $texture.path
                }
            }) $folder
    if ($null -eq $materials -or $null -eq $textures) {
        throw "Blender reported invalid material or texture records for $($Manifest.Name)."
    }

    if ($null -ne $Manifest.Export) {
        Remove-ArtStaleFiles @($Manifest.Export.models | ForEach-Object { $_.target; $_.data }) @($models | ForEach-Object { $_.target; $_.data })
        Remove-ArtStaleFiles @($Manifest.Export.textures | ForEach-Object { $_.target }) @($textures | ForEach-Object { $_.target })
    }

    $Manifest.Export = [ordered]@{
        sourceSha256 = $SourceSha256
        models = $models
        clipSources = $clipSources
        materials = $materials
        textures = $textures
    }
    Write-ArtAssetManifest $Manifest
    $triangles = ($results | Where-Object { $_ -like "ART_VALIDATION *" }) -replace '^ART_VALIDATION ', ''
    foreach ($line in @($results | Where-Object { $_ -like "ART_REPORT *" })) {
        Write-Host $line.Substring("ART_REPORT ".Length)
    }

    Write-Host "Exported $($Manifest.Name) to $(@($models | ForEach-Object { $_.target }) -join ', ') with their data ($triangles)."
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

    if ($manifest.Kind -eq "garment") {
        Export-ArtGarment $blender $manifest $sourceSha256
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

    if ($null -ne $manifest.Export) {
        Remove-ArtStaleFiles @($manifest.Export.textures | ForEach-Object { $_.target }) @($textures | ForEach-Object { $_.target })
    }

    # A body with an editable mesh exports its topology next to the model.
    $dataSha256 = $null
    $data = @($results | Where-Object { $_ -like "ART_DATA *" } | ForEach-Object { $_.Substring("ART_DATA ".Length) | ConvertFrom-Json })
    if ($data.Count -gt 0) {
        if ($data.Count -ne 1 -or (Get-ArtRelativePath $data[0].path) -cne (Get-ArtDataTarget $manifest.Target)) {
            throw "Blender reported unexpected data for $($manifest.Name)."
        }

        $dataSha256 = Get-ArtFileSha256 $data[0].path
    }

    $manifest.Export = [ordered]@{
        sourceSha256 = $sourceSha256
        targetSha256 = Get-ArtFileSha256 $manifest.TargetPath
        dataSha256 = $dataSha256
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
