# Exports approved art assets from art/ to their FBX target below src/Client/Assets/Art/Models,
# with the images of painted materials as PNG files below src/Client/Assets/Art/Textures, and
# records the content fingerprint of the exported stage, the hashes of the written files, the
# materials, and the animation clips in asset.json. It first makes the content records of the assets
# and of those whose exports it records current, then refuses an asset whose review stages are not
# all approved for the content of the current .blend file. An animation library exports its armature
# and clips only, and records the export of the rig it was authored on, so it is exported after that
# rig. A paint layer exports its images only, and a garment one model per body below
# src/Client/Assets/Art/Models/Garments. The garment cage has no export, so -All leaves it out. Unity
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
# Animation libraries come after the rigs they record the export of, garments after them, because
# they record the exports of their bodies and of the libraries they were checked in, from the lowest
# layer up, because a template records the exports of the lower templates it was checked over, and
# textures last, because they record the exports of their templates.
$read = @($directories | ForEach-Object { Read-ArtAssetManifest $_ })
$cages = @($read | Where-Object { $_.Kind -eq "cage" })
if (-not $All -and $cages.Count -gt 0) {
    throw "$($cages[0].Name) is a garment cage, which has no export; garments record its approved content."
}

$read = @($read | Where-Object { $_.Kind -ne "cage" })
$manifests = @($read | Where-Object { @("animations", "garment", "texture") -notcontains $_.Kind }) +
    @($read | Where-Object { $_.Kind -eq "animations" }) +
    @($read | Where-Object { $_.Kind -eq "garment" } | Sort-Object -Property Layer, Name) +
    @($read | Where-Object { $_.Kind -eq "texture" })
$blender = Resolve-ArtBlender $BlenderPath
foreach ($manifest in $manifests) {
    $null = Update-ArtContentRecord $blender $manifest
    $problem = Get-ArtExportGateProblem $manifest
    if ($null -ne $problem) {
        throw "$($manifest.Name) cannot be exported: $problem. Review it with Render-ArtPreview.ps1 or in Blender, then record the approval with Approve-ArtAsset.ps1."
    }
}

function Get-ArtCurrentManifest([string]$Blender, [string]$Directory) {
    # Another asset whose export an export records, with its content record made current.
    $other = Read-ArtAssetManifest $Directory
    $null = Update-ArtContentRecord $Blender $other
    return $other
}

function Export-ArtPaint([string]$Blender, $Manifest) {
    # A paint layer exports one image per body, cropped to its paint, with its place in the body's
    # full paint layout, the fingerprint of the body it was moved to, and how it was moved, and the
    # smoothness and metallic of its paint, and the cage's content when a body was moved through it.
    $folder = $script:ArtPaintTextureFolder
    $sourceSha256 = Get-ArtFileSha256 $Manifest.SourcePath
    $results = Invoke-ArtBlender $Blender $Manifest (
        @("export") + (Get-ArtValidationArguments $Manifest "paint") + @(
            "--textures", (Join-Path $script:ArtRepositoryRoot $folder)))
    Assert-ArtExportedContent $Manifest $results $sourceSha256
    $textures = ConvertTo-ArtPaintTextureRecords @($results | Where-Object { $_ -like "ART_TEXTURE *" } |
            ForEach-Object {
                $texture = $_.Substring("ART_TEXTURE ".Length) | ConvertFrom-Json
                [pscustomobject]@{
                    name = $texture.name
                    target = Get-ArtRelativePath $texture.path
                    sha256 = Get-ArtFileSha256 $texture.path
                    layout = $texture.layout
                    rect = $texture.rect
                    move = $texture.move
                    bodyPaintSha256 = $texture.bodyPaintSha256
                }
            }) $folder
    $surface = @($results | Where-Object { $_ -like "ART_PAINT *" } |
            ForEach-Object { $_.Substring("ART_PAINT ".Length) | ConvertFrom-Json }) | Select-Object -First 1
    if ($null -eq $textures -or $null -eq $surface) {
        throw "Blender reported no valid paint records for $($Manifest.Name)."
    }

    $Manifest.Export = [ordered]@{
        contentSha256 = Get-ArtExportedContent $Manifest
        cageContentSha256 = if ($null -ne $surface.cageContentSha256) { [string]$surface.cageContentSha256 } else { $null }
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

function Get-ArtClipSourceProblem([string]$Blender, $Manifest) {
    # Returns $null when every animation library of the skeleton is exported from its current
    # content, otherwise the reason it is not. Blender reads the libraries' current files for the
    # motion check, so each must hold the content of its export.
    foreach ($library in @(Get-ArtClipLibraries $Manifest.Skeleton)) {
        $current = Get-ArtCurrentManifest $Blender (Join-Path $script:ArtRepositoryRoot $library.Asset)
        $problem = Get-ArtExportChangeProblem $current
        if ($null -ne $problem) {
            return "the animation library $problem"
        }
    }

    return $null
}

function Get-ArtClipSourceRecords([string]$Blender, $Manifest) {
    # The exports of the animation libraries whose clips a garment or a texture's masks are checked in.
    $problem = Get-ArtClipSourceProblem $Blender $Manifest
    if ($null -ne $problem) {
        throw "$($Manifest.Name) cannot be exported while $problem."
    }

    return @(Get-ArtClipLibraries $Manifest.Skeleton | ForEach-Object {
            [ordered]@{ library = $_.Asset; contentSha256 = [string]$_.Export.contentSha256 }
        })
}

function Export-ArtTexture([string]$Blender, $Manifest) {
    # A garment texture exports its image and its material, and with transparent parts its masks
    # per body of its template, checked in the clips of the skeleton's libraries, with the faces they
    # hide of the templates of lower layers worn under its template. It records its template's
    # export and those of the libraries and the lower templates its masks were made with, so a new
    # export of any of them needs a new export of the texture. The masks are computed in the
    # template's file, so that file must hold the content its export came from.
    $template = Get-ArtCurrentManifest $Blender $Manifest.Garment.Directory
    $templateExport = $template.Export
    if ($null -eq $templateExport) {
        throw "$($Manifest.Name) cannot be exported before its template $($Manifest.Garment.RelativeDirectory) is exported."
    }

    $templateProblem = Get-ArtExportGateProblem $template
    if ($null -eq $templateProblem -and $templateExport.contentSha256 -cne (Get-ArtExportedContent $template)) {
        $templateProblem = "$($template.Name).blend changed after its export"
    }

    if ($null -ne $templateProblem) {
        throw "$($Manifest.Name) cannot be exported before its template $($Manifest.Garment.RelativeDirectory) is approved and exported again: $templateProblem."
    }

    # The masks are computed with the template's bodies, cage, and clips, so its export must still
    # match them.
    $dependencyProblem = @(Get-ArtGarmentDependencyProblems $template) | Select-Object -First 1
    if ($null -ne $dependencyProblem) {
        throw "$($Manifest.Name) cannot be exported before its template $($Manifest.Garment.RelativeDirectory) is exported again: it $dependencyProblem."
    }

    # The texture is painted in its template's current UVs, which their content records compare.
    $Manifest.Garment = $template
    $reviewProblem = Get-ArtTextureTemplateProblem $Manifest
    if ($null -ne $reviewProblem) {
        throw "$($Manifest.Name) cannot be exported: $reviewProblem."
    }

    # Only the masks of a texture with transparent parts are checked in the clips and name the faces
    # they hide of the templates of lower layers worn under its template, so a library or such a
    # template that is not exported from its current content, which Blender reads, stops only such a
    # texture, in Blender, before anything is written; whether the texture has them shows only there.
    $clipProblem = Get-ArtClipSourceProblem $Blender $Manifest
    $clipSources = if ($null -eq $clipProblem) { Get-ArtClipSourceRecords $Blender $Manifest } else { @() }
    $pairProblem = $null
    $pairSources = @()
    foreach ($pair in @(Get-ArtGarmentPairs $template)) {
        $problem = Get-ArtExportChangeProblem (Get-ArtCurrentManifest $Blender $pair.Directory)
        if ($null -eq $pairProblem -and $null -ne $problem) {
            $pairProblem = "the template of a lower layer $problem"
        }

        $pairSources += [ordered]@{ garment = $pair.RelativeDirectory; contentSha256 = [string]$pair.Export.contentSha256 }
    }

    $opaqueReason = @($clipProblem, $pairProblem | Where-Object { $null -ne $_ }) -join "; "
    $opaqueOnly = if ($opaqueReason) { @("--opaque-only", $opaqueReason) } else { @() }
    $folder = $script:ArtGarmentTextureFolder
    $sourceSha256 = Get-ArtFileSha256 $Manifest.SourcePath
    $results = Invoke-ArtBlender $Blender $Manifest (
        @("export") + (Get-ArtValidationArguments $Manifest "texture") + @(
            "--textures", (Join-Path $script:ArtRepositoryRoot $folder),
            "--target", (Join-Path $script:ArtRepositoryRoot $script:ArtGarmentModelFolder)) + $opaqueOnly)
    Assert-ArtExportedContent $Manifest $results $sourceSha256
    $reported = @($results | Where-Object { $_ -like "ART_TEXTURE_MASK *" } |
            ForEach-Object { $_.Substring("ART_TEXTURE_MASK ".Length) | ConvertFrom-Json })
    $masks = @(if ($reported.Count -gt 0) {
            $Manifest.Masks | ForEach-Object {
                $mask = $_
                $record = @($reported | Where-Object { [string]$_.label -ceq $mask.Body })
                if ($record.Count -ne 1 -or (Get-ArtRelativePath $record[0].data) -cne $mask.Data) {
                    throw "Blender did not report the masks $($mask.Data) of $($Manifest.Name) once."
                }

                [ordered]@{ body = $mask.Body; data = $mask.Data; dataSha256 = Get-ArtFileSha256 $mask.DataPath }
            }
        })
    $materials = ConvertTo-ArtMaterialRecords @($results | Where-Object { $_ -like "ART_MATERIAL *" } |
            ForEach-Object { $_.Substring("ART_MATERIAL ".Length) | ConvertFrom-Json })
    $textures = ConvertTo-ArtTextureRecords @($results | Where-Object { $_ -like "ART_TEXTURE *" } |
            ForEach-Object {
                $texture = $_.Substring("ART_TEXTURE ".Length) | ConvertFrom-Json
                [pscustomobject]@{ name = $texture.name; target = Get-ArtRelativePath $texture.path; sha256 = Get-ArtFileSha256 $texture.path }
            }) $folder
    # The image, and the dye mask of a dyeable texture, which its material names.
    $expected = @("T_Garment_$($Manifest.Name)") + @($materials | Where-Object { $null -ne $_ -and $_.Contains("dyeMap") } | ForEach-Object { $_.dyeMap })
    if ($null -eq $materials -or $null -eq $textures -or (@($textures | ForEach-Object { $_.name }) -join ",") -cne ($expected -join ",")) {
        throw "Blender reported invalid material or texture records for $($Manifest.Name)."
    }

    if ($null -ne $Manifest.Export) {
        Remove-ArtStaleFiles @($Manifest.Export.masks | Where-Object { $null -ne $_ } | ForEach-Object { $_.data }) @($masks | ForEach-Object { $_.data })
        Remove-ArtStaleFiles @($Manifest.Export.textures | ForEach-Object { $_.target }) @($textures | ForEach-Object { $_.target })
    }

    $Manifest.Export = [ordered]@{
        contentSha256 = Get-ArtExportedContent $Manifest
        garmentContentSha256 = [string]$templateExport.contentSha256
        masks = $masks
        clipSources = $(if ($masks.Count -gt 0) { $clipSources } else { @() })
        pairSources = $(if ($masks.Count -gt 0) { $pairSources } else { @() })
        materials = $materials
        textures = $textures
    }
    Write-ArtAssetManifest $Manifest
    foreach ($line in @($results | Where-Object { $_ -like "ART_REPORT *" })) {
        Write-Host $line.Substring("ART_REPORT ".Length)
    }

    $maskNote = if ($masks.Count -gt 0) { " and its masks $(@($masks | ForEach-Object { $_.data }) -join ', ')" } else { "" }
    Write-Host "Exported $($Manifest.Name) to $(@($textures | ForEach-Object { $_.target }) -join ', ') with its material $($materials[0].name)$maskNote."
}

function Export-ArtGarment([string]$Blender, $Manifest) {
    # A garment template exports one model per body, each with that body's armature and its data.
    # It records the approved content of its cage and the exports of the bodies and the animation
    # libraries it was checked against, so a change of any of them needs a new export of the
    # template.
    $null = Update-ArtContentRecord $Blender $Manifest.Cage
    $cageProblem = Get-ArtCageGateProblem $Manifest.Cage
    if ($null -ne $cageProblem) {
        throw "$($Manifest.Name) cannot be exported before its cage $($Manifest.Cage.RelativeDirectory) is approved: $cageProblem."
    }

    $bodyExports = @{}
    foreach ($body in @($Manifest.Bodies)) {
        $bodyExport = (Read-ArtAssetManifest (Split-Path -Parent $body.ManifestPath)).Export
        if ($null -eq $bodyExport -or [string]::IsNullOrEmpty($bodyExport.fitSha256)) {
            throw "$($Manifest.Name) cannot be exported before its body $($body.Asset) is exported with the fingerprint of its fit; export the body again."
        }

        $bodyExports[$body.Root] = [string]$bodyExport.fitSha256
    }

    $clipSources = Get-ArtClipSourceRecords $Blender $Manifest
    # The templates of a lower layer it is checked over, each exported from its current content,
    # since Blender reads their current files.
    $pairSources = @(Get-ArtGarmentPairs $Manifest | ForEach-Object {
            $problem = Get-ArtExportChangeProblem (Get-ArtCurrentManifest $Blender $_.Directory)
            if ($null -ne $problem) {
                throw "$($Manifest.Name) cannot be exported while the template of a lower layer $problem."
            }

            [ordered]@{ garment = $_.RelativeDirectory; contentSha256 = [string]$_.Export.contentSha256 }
        })
    $folder = $script:ArtGarmentTextureFolder
    $sourceSha256 = Get-ArtFileSha256 $Manifest.SourcePath
    $results = Invoke-ArtBlender $Blender $Manifest (
        @("export") + (Get-ArtValidationArguments $Manifest $Manifest.Stages[-1]) + @(
            "--target", (Join-Path $script:ArtRepositoryRoot $script:ArtGarmentModelFolder),
            "--textures", (Join-Path $script:ArtRepositoryRoot $folder)))
    Assert-ArtExportedContent $Manifest $results $sourceSha256
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
                bodyFitSha256 = $bodyExports[$model.Body]
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
        contentSha256 = Get-ArtExportedContent $Manifest
        cageContentSha256 = Get-ArtCageContent $Manifest.Cage
        models = $models
        clipSources = $clipSources
        pairSources = $pairSources
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

foreach ($manifest in $manifests) {
    $rigContentSha256 = $null
    if ($manifest.Kind -eq "animations") {
        $rigExport = (Read-ArtAssetManifest (Split-Path -Parent $manifest.Rig.ManifestPath)).Export
        if ($null -eq $rigExport) {
            throw "$($manifest.Name) cannot be exported before its rig $($manifest.Rig.Asset) is exported."
        }

        $rigContentSha256 = $rigExport.contentSha256
    }

    if ($manifest.Kind -eq "paint") {
        Export-ArtPaint $blender $manifest
        continue
    }

    if ($manifest.Kind -eq "garment") {
        Export-ArtGarment $blender $manifest
        continue
    }

    if ($manifest.Kind -eq "texture") {
        Export-ArtTexture $blender $manifest
        continue
    }

    $textureFolder = Get-ArtTextureFolder $manifest.Target
    $sourceSha256 = Get-ArtFileSha256 $manifest.SourcePath
    $results = Invoke-ArtBlender $blender $manifest (
        @("export") + (Get-ArtValidationArguments $manifest $manifest.Stages[-1]) + @(
            "--target", $manifest.TargetPath, "--textures", (Join-Path $script:ArtRepositoryRoot $textureFolder)))
    Assert-ArtExportedContent $manifest $results $sourceSha256
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

    # A body with an editable mesh exports its topology next to the model, and records the
    # fingerprint of what the garment cage and garments read from it: its rig, its surface with its
    # faces and their body parts, and its weights; and of what paint layers read from it: its
    # meshes joined with their faces, materials, and paint UV map, and its rig's landmarks.
    $dataSha256 = $null
    $fitSha256 = $null
    $paintSha256 = $null
    $data = @($results | Where-Object { $_ -like "ART_DATA *" } | ForEach-Object { $_.Substring("ART_DATA ".Length) | ConvertFrom-Json })
    if ($data.Count -gt 0) {
        $fits = @($results | Where-Object { $_ -like "ART_FIT *" } | ForEach-Object { $_.Substring("ART_FIT ".Length).Trim() })
        $layouts = @($results | Where-Object { $_ -like "ART_PAINT_LAYOUT *" } | ForEach-Object { $_.Substring("ART_PAINT_LAYOUT ".Length).Trim() })
        if ($data.Count -ne 1 -or (Get-ArtRelativePath $data[0].path) -cne (Get-ArtDataTarget $manifest.Target) -or
            $fits.Count -ne 1 -or $fits[0] -cnotmatch $script:ArtShaPattern -or
            $layouts.Count -ne 1 -or $layouts[0] -cnotmatch $script:ArtShaPattern) {
            throw "Blender reported unexpected data for $($manifest.Name)."
        }

        $dataSha256 = Get-ArtFileSha256 $data[0].path
        $fitSha256 = $fits[0]
        $paintSha256 = $layouts[0]
    }
    elseif ($null -ne $manifest.Export -and $manifest.Export.Contains("dataSha256") -and $null -ne $manifest.Export.dataSha256) {
        Remove-ArtStaleFiles @(Get-ArtDataTarget $manifest.Target) @()
    }

    $manifest.Export = [ordered]@{
        contentSha256 = Get-ArtExportedContent $manifest
        targetSha256 = Get-ArtFileSha256 $manifest.TargetPath
        dataSha256 = $dataSha256
        fitSha256 = $fitSha256
        paintSha256 = $paintSha256
        rigContentSha256 = $rigContentSha256
        materials = $materials
        textures = $textures
        clips = $clipRecords
    }
    Write-ArtAssetManifest $manifest
    $triangles = ($results | Where-Object { $_ -like "ART_VALIDATION *" }) -replace '^ART_VALIDATION ', ''
    $what = if ($manifest.Kind -eq "animations") { "clips=$($manifest.Clips -join ',')" } else { $triangles }
    Write-Host "Exported $($manifest.Name) to $($manifest.Target) ($what)."
}
