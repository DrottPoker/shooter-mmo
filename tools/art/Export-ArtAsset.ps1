# Exports approved art assets from art/ to their FBX target below src/Client/Assets/Art/Models,
# with the images of painted materials as PNG files below src/Client/Assets/Art/Textures, and
# records the content fingerprint of the exported stage, the hashes of the written files, the
# materials, and the animation clips in asset.json. It first makes the content records of the assets
# and of those whose content its records name current, then refuses an asset whose review stages are
# not all approved for the content of the current .blend file. An animation library exports its
# armature and clips only, and records the content of the rig it was authored on. A paint layer exports
# its images only, and a garment one model per body below src/Client/Assets/Art/Models/Garments. The
# garment cage has no export, so -All leaves it out. Unity imports the FBX with the rules in
# ArtModelImportRules. The checks of a garment template or a masked texture come from their cache below
# artifacts/art-cache where an approval or a sync computed them.
#
# Several Blender processes export at once, -Jobs of them: first the bodies and the other skinned and
# static assets, whose exports the cage, the garments, and the paint layers read the fit and the paint
# layout from, then everything else, the longest first. A record names the content of the other assets
# it was made with, never their exports, so the order within a wave changes no byte. This script alone
# writes each asset.json, when the Blender run of that asset ended, and prints each asset's lines
# together, ending with a line of the seconds it took; each run's output is kept below
# artifacts/art-build/export. Once it wrote a record, it leaves the marker artifacts/art-build/unity/pending
# until tools/art/Update-UnityArt.ps1 brings the Unity project up to date. See docs/ART_PIPELINE.md.
param(
    [string[]]$Asset,
    # Export every asset below art/.
    [switch]$All,
    # Computes every check of a garment template or a masked texture, without the cache of the checks.
    [switch]$NoCache,
    # How many Blender processes export at once: by default half the logical processors, at most 8. 1
    # exports one asset after the other in the order of the records.
    [int]$Jobs = 0,
    # A list that gets one record per asset whose export ran: its folder, name, kind, seconds, check
    # units, units from the cache, and error, for tools/art/Build-Art.ps1.
    [System.Collections.IList]$Report,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

if ($All -eq ($Asset.Count -gt 0)) {
    throw "Pass either -Asset <folder> or -All."
}

if ($Jobs -lt 0) {
    throw "-Jobs is how many Blender processes export at once, 1 or more."
}

if ($Jobs -eq 0) {
    $Jobs = Get-ArtDefaultJobs
}

$directories = if ($All) { Get-ArtAssetDirectories } else { @($Asset | ForEach-Object { Resolve-ArtAssetDirectory $_ }) }
$read = @($directories | ForEach-Object { Read-ArtAssetManifest $_ })
$cages = @($read | Where-Object { $_.Kind -eq "cage" })
if (-not $All -and $cages.Count -gt 0) {
    throw "$($cages[0].Name) is a garment cage, which has no export; garments record its approved content."
}

# The order of the records: the models and paint layers, the animation libraries, the garments from the
# lowest layer up, and the textures, which -Jobs 1 keeps.
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

# The templates of this run count as exported for the templates of higher layers checked over them, so
# a template exported for the first time is among their templates of lower layers in any order.
$script:ArtExportingTemplates = @($manifests | Where-Object { $_.Kind -eq "garment" } | ForEach-Object { $_.RelativeDirectory })

function Get-ArtCurrentManifest([string]$Blender, [string]$Directory) {
    # Another asset whose content an export records, with its content record made current.
    $other = Read-ArtAssetManifest $Directory
    $null = Update-ArtContentRecord $Blender $other
    return $other
}

function New-ArtExportJob($Manifest, [string[]]$Arguments, [hashtable]$Context) {
    # The Blender run of one asset's export, with the file's hash before it and what the record needs.
    $log = Join-Path (Get-ArtBuildFolder "export") "$($Manifest.Name).log"
    return New-ArtBlenderJob $Manifest $Arguments $log @{
        SourceSha256 = Get-ArtFileSha256 $Manifest.SourcePath
        Context = $Context
        Lines = $null
    }
}

function New-ArtPaintJob([string]$Blender, $Manifest) {
    # A paint layer exports one image per body, cropped to its paint, with its place in the body's
    # full paint layout, the fingerprint of the body it was moved to, and how it was moved, and the
    # smoothness and metallic of its paint, and the cage's content when a body was moved through it.
    $folder = $script:ArtPaintTextureFolder
    return New-ArtExportJob $Manifest (@("export") + (Get-ArtValidationArguments $Manifest "paint") + @(
            "--textures", (Join-Path $script:ArtRepositoryRoot $folder))) @{ Folder = $folder }
}

function Complete-ArtPaintJob($Job) {
    $Manifest = $Job.Manifest
    $results = $Job.Results
    $folder = $Job.Context.Folder
    Assert-ArtExportedContent $Manifest $results $Job.SourceSha256
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
    Write-ArtLine "Exported $($Manifest.Name) to $folder ($(@($textures | ForEach-Object { $_.name }) -join ', '))."
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

function New-ArtTextureJob([string]$Blender, $Manifest) {
    # A garment texture exports its image and its material, and with transparent parts its masks
    # per body of its template, checked at rest and in the test poses, with the faces they hide of the
    # templates of lower layers worn under its template. It records the content of its template and of
    # the lower templates its masks were made with, so a change of any of them needs a new export of
    # the texture; a change of a clip needs none. The masks are computed in the template's file, so
    # its approvals must hold for that file's content.
    $template = Get-ArtCurrentManifest $Blender $Manifest.Garment.Directory
    $templateProblem = Get-ArtExportGateProblem $template
    if ($null -ne $templateProblem) {
        throw "$($Manifest.Name) cannot be exported before its template $($Manifest.Garment.RelativeDirectory) is approved for its content: $templateProblem."
    }

    # The masks are computed with the bodies and the cage in the template's file, which a sync keeps
    # current. A template of this run is held to them by its own export; any other template must be
    # exported from its current content, fitted to its cage and its bodies as they are now.
    if (@($script:ArtExportingTemplates) -cnotcontains $template.RelativeDirectory) {
        $templateExportProblem = if ($null -eq $template.Export) {
            "it was never exported"
        }
        elseif ($template.Export.contentSha256 -cne (Get-ArtExportedContent $template)) {
            "its export comes from other content than its file holds"
        }
        else {
            @(Get-ArtGarmentFitProblems $template | ForEach-Object { "it $_" }) | Select-Object -First 1
        }

        if ($null -ne $templateExportProblem) {
            throw "$($Manifest.Name) cannot be exported before its template $($Manifest.Garment.RelativeDirectory) is exported again: $templateExportProblem. Export the template in the same run or first."
        }
    }

    # The texture is painted in its template's current UVs, which their content records compare.
    $Manifest.Garment = $template
    $reviewProblem = Get-ArtTextureTemplateProblem $Manifest
    if ($null -ne $reviewProblem) {
        throw "$($Manifest.Name) cannot be exported: $reviewProblem."
    }

    # Only the masks of a texture with transparent parts name the faces they hide of the templates of
    # lower layers worn under its template, so such a template whose approvals do not hold for the
    # content Blender reads stops only such a texture, in Blender, before anything is written; whether
    # the texture has them shows only there.
    $pairProblem = $null
    $pairSources = @()
    $pairs = @(Get-ArtGarmentPairs $template)
    foreach ($pair in $pairs) {
        $current = Get-ArtCurrentManifest $Blender $pair.Directory
        $problem = Get-ArtExportGateProblem $current
        if ($null -eq $pairProblem -and $null -ne $problem) {
            $pairProblem = "the template of a lower layer $($pair.RelativeDirectory) is not approved for its content: $problem"
        }

        $pairSources += [ordered]@{ garment = $pair.RelativeDirectory; contentSha256 = Get-ArtExportedContent $current }
    }

    $opaqueReason = @($pairProblem | Where-Object { $null -ne $_ }) -join "; "
    $opaqueOnly = if ($opaqueReason) { @("--opaque-only", $opaqueReason) } else { @() }
    $folder = $script:ArtGarmentTextureFolder
    return New-ArtExportJob $Manifest (
        @("export") + (Get-ArtValidationArguments $Manifest "texture") + @(
            "--textures", (Join-Path $script:ArtRepositoryRoot $folder),
            "--target", (Join-Path $script:ArtRepositoryRoot $script:ArtGarmentModelFolder)) + $opaqueOnly +
        (Get-ArtCacheArguments $Blender $Manifest -NoCache:$NoCache -Pairs $pairs)) @{
        Folder = $folder
        garmentContentSha256 = Get-ArtExportedContent $template
        PairSources = $pairSources
    }
}

function Complete-ArtTextureJob($Job) {
    $Manifest = $Job.Manifest
    $results = $Job.Results
    $folder = $Job.Context.Folder
    Assert-ArtExportedContent $Manifest $results $Job.SourceSha256
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
        garmentContentSha256 = [string]$Job.Context.garmentContentSha256
        masks = $masks
        pairSources = $(if ($masks.Count -gt 0) { $Job.Context.PairSources } else { @() })
        materials = $materials
        textures = $textures
    }
    Write-ArtAssetManifest $Manifest
    foreach ($line in @($results | Where-Object { $_ -like "ART_REPORT *" })) {
        Write-ArtLine $line.Substring("ART_REPORT ".Length)
    }

    $maskNote = if ($masks.Count -gt 0) { " and its masks $(@($masks | ForEach-Object { $_.data }) -join ', ')" } else { "" }
    Write-ArtLine "Exported $($Manifest.Name) to $(@($textures | ForEach-Object { $_.target }) -join ', ') with its material $($materials[0].name)$maskNote."
}

function New-ArtGarmentJob([string]$Blender, $Manifest) {
    # A garment template exports one model per body, each with that body's armature and its data.
    # It records the approved content of its cage, the fit of the bodies' exports, and the content of
    # the templates of lower layers it was checked over, so a change of any of them needs a new export
    # of the template. Its checks read no clip, so a change of a clip needs none.
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

    # The templates of a lower layer it is checked over, each approved for its current content, since
    # Blender reads their current files.
    $pairs = @(Get-ArtGarmentPairs $Manifest)
    $pairSources = @($pairs | ForEach-Object {
            $current = Get-ArtCurrentManifest $Blender $_.Directory
            $problem = Get-ArtExportGateProblem $current
            if ($null -ne $problem) {
                throw "$($Manifest.Name) cannot be exported while the template of a lower layer $($_.RelativeDirectory) is not approved for its content: $problem."
            }

            [ordered]@{ garment = $_.RelativeDirectory; contentSha256 = Get-ArtExportedContent $current }
        })
    $folder = $script:ArtGarmentTextureFolder
    return New-ArtExportJob $Manifest (
        @("export") + (Get-ArtValidationArguments $Manifest $Manifest.Stages[-1]) + @(
            "--target", (Join-Path $script:ArtRepositoryRoot $script:ArtGarmentModelFolder),
            "--textures", (Join-Path $script:ArtRepositoryRoot $folder)) +
        (Get-ArtCacheArguments $Blender $Manifest -NoCache:$NoCache -Pairs $pairs)) @{
        Folder = $folder
        BodyFits = $bodyExports
        cageContentSha256 = Get-ArtCageContent $Manifest.Cage
        PairSources = $pairSources
    }
}

function Complete-ArtGarmentJob($Job) {
    $Manifest = $Job.Manifest
    $results = $Job.Results
    $folder = $Job.Context.Folder
    Assert-ArtExportedContent $Manifest $results $Job.SourceSha256
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
                bodyFitSha256 = $Job.Context.BodyFits[$model.Body]
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
        cageContentSha256 = [string]$Job.Context.cageContentSha256
        models = $models
        pairSources = $Job.Context.PairSources
        materials = $materials
        textures = $textures
    }
    Write-ArtAssetManifest $Manifest
    $triangles = ($results | Where-Object { $_ -like "ART_VALIDATION *" }) -replace '^ART_VALIDATION ', ''
    foreach ($line in @($results | Where-Object { $_ -like "ART_REPORT *" })) {
        Write-ArtLine $line.Substring("ART_REPORT ".Length)
    }

    Write-ArtLine "Exported $($Manifest.Name) to $(@($models | ForEach-Object { $_.target }) -join ', ') with their data ($triangles)."
}

function New-ArtModelJob([string]$Blender, $Manifest) {
    # A static or skinned asset exports its FBX with its images, and an animation library its armature
    # and clips with the content of the rig it was authored on, whose approvals must hold for it.
    $rigContentSha256 = $null
    if ($Manifest.Kind -eq "animations") {
        $rig = Get-ArtCurrentManifest $Blender (Split-Path -Parent $Manifest.Rig.ManifestPath)
        $rigProblem = Get-ArtExportGateProblem $rig
        if ($null -ne $rigProblem) {
            throw "$($Manifest.Name) cannot be exported before its rig $($Manifest.Rig.Asset) is approved for its content: $rigProblem."
        }

        $rigContentSha256 = Get-ArtExportedContent $rig
    }

    $textureFolder = Get-ArtTextureFolder $Manifest.Target
    return New-ArtExportJob $Manifest (
        @("export") + (Get-ArtValidationArguments $Manifest $Manifest.Stages[-1]) + @(
            "--target", $Manifest.TargetPath, "--textures", (Join-Path $script:ArtRepositoryRoot $textureFolder))) @{
        Folder = $textureFolder
        RigContentSha256 = $rigContentSha256
    }
}

function Complete-ArtModelJob($Job) {
    $Manifest = $Job.Manifest
    $results = $Job.Results
    $textureFolder = $Job.Context.Folder
    Assert-ArtExportedContent $Manifest $results $Job.SourceSha256
    $materials = @()
    if ($Manifest.Kind -ne "animations") {
        $materials = ConvertTo-ArtMaterialRecords @($results | Where-Object { $_ -like "ART_MATERIAL *" } |
                ForEach-Object { $_.Substring("ART_MATERIAL ".Length) | ConvertFrom-Json })
        if ($null -eq $materials) {
            throw "Blender reported no valid material records for $($Manifest.Name)."
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
        throw "Blender reported invalid texture records for $($Manifest.Name)."
    }

    $clipRecords = $null
    if ($script:ArtAnimatedKinds -contains $Manifest.Kind) {
        # In the order of the manifest, so the record reads like it.
        $reported = @($results | Where-Object { $_ -like "ART_CLIP *" } |
                ForEach-Object { $_.Substring("ART_CLIP ".Length) | ConvertFrom-Json })
        $clipRecords = ConvertTo-ArtClipRecords @($Manifest.Clips | ForEach-Object {
                $clip = $_
                $reported | Where-Object { $_.name -ceq $clip }
            }) $Manifest.Clips
        if ($null -eq $clipRecords) {
            throw "Blender did not report every clip of $($Manifest.Name) once."
        }
    }

    if ($null -ne $Manifest.Export) {
        Remove-ArtStaleFiles @($Manifest.Export.textures | ForEach-Object { $_.target }) @($textures | ForEach-Object { $_.target })
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
        if ($data.Count -ne 1 -or (Get-ArtRelativePath $data[0].path) -cne (Get-ArtDataTarget $Manifest.Target) -or
            $fits.Count -ne 1 -or $fits[0] -cnotmatch $script:ArtShaPattern -or
            $layouts.Count -ne 1 -or $layouts[0] -cnotmatch $script:ArtShaPattern) {
            throw "Blender reported unexpected data for $($Manifest.Name)."
        }

        $dataSha256 = Get-ArtFileSha256 $data[0].path
        $fitSha256 = $fits[0]
        $paintSha256 = $layouts[0]
    }
    elseif ($null -ne $Manifest.Export -and $Manifest.Export.Contains("dataSha256") -and $null -ne $Manifest.Export.dataSha256) {
        Remove-ArtStaleFiles @(Get-ArtDataTarget $Manifest.Target) @()
    }

    $Manifest.Export = [ordered]@{
        contentSha256 = Get-ArtExportedContent $Manifest
        targetSha256 = Get-ArtFileSha256 $Manifest.TargetPath
        dataSha256 = $dataSha256
        fitSha256 = $fitSha256
        paintSha256 = $paintSha256
        rigContentSha256 = $Job.Context.RigContentSha256
        materials = $materials
        textures = $textures
        clips = $clipRecords
    }
    Write-ArtAssetManifest $Manifest
    $triangles = ($results | Where-Object { $_ -like "ART_VALIDATION *" }) -replace '^ART_VALIDATION ', ''
    $what = if ($Manifest.Kind -eq "animations") { "clips=$($Manifest.Clips -join ',')" } else { $triangles }
    Write-ArtLine "Exported $($Manifest.Name) to $($Manifest.Target) ($what)."
}

function New-ArtKindJob([string]$Blender, $Manifest) {
    switch ($Manifest.Kind) {
        "paint" { return New-ArtPaintJob $Blender $Manifest }
        "garment" { return New-ArtGarmentJob $Blender $Manifest }
        "texture" { return New-ArtTextureJob $Blender $Manifest }
        default { return New-ArtModelJob $Blender $Manifest }
    }
}

function Complete-ArtKindJob($Job) {
    switch ($Job.Manifest.Kind) {
        "paint" { Complete-ArtPaintJob $Job }
        "garment" { Complete-ArtGarmentJob $Job }
        "texture" { Complete-ArtTextureJob $Job }
        default { Complete-ArtModelJob $Job }
    }
}

# The seconds each asset's last export took with nothing from the cache of the checks, which put the
# longest first when several run at once, kept below artifacts/art-build beside the logs.
$secondsPath = Join-Path (Get-ArtBuildFolder "export") "seconds.json"
$seconds = @{}
if (Test-Path -LiteralPath $secondsPath -PathType Leaf) {
    try {
        foreach ($property in (Get-Content -Raw -LiteralPath $secondsPath | ConvertFrom-Json).PSObject.Properties) {
            $seconds[$property.Name] = [double]$property.Value
        }
    }
    catch {
        # A broken file only loses the order of one run.
    }
}

function Get-ArtExpectedSeconds($Manifest) {
    # The seconds an asset's export took with its checks computed, or a guess by its kind.
    if ($seconds.ContainsKey($Manifest.RelativeDirectory)) {
        return $seconds[$Manifest.RelativeDirectory]
    }

    switch ($Manifest.Kind) {
        "garment" { return 300.0 }
        "texture" { return 100.0 }
        "paint" { return 20.0 }
        default { return 10.0 }
    }
}

function Save-ArtExpectedSeconds {
    $names = [string[]]@($seconds.Keys)
    [Array]::Sort($names, [StringComparer]::Ordinal)
    $lines = @($names | ForEach-Object { "  `"$_`": $($seconds[$_].ToString('0.0', [System.Globalization.CultureInfo]::InvariantCulture))" })
    $text = "{`n$($lines -join ",`n")`n}`n"
    [System.IO.File]::WriteAllText($secondsPath, $text, (New-Object System.Text.UTF8Encoding($false)))
}

# Whether this run left the marker that the Unity follow-up has not run since an export.
$script:UnityMarked = $false

$onDone = {
    param($Job)
    # Finishes one asset as soon as its Blender ended: checks what it reported, writes its asset.json,
    # and prints its lines together.
    $script:ArtLineSink = $Job.Lines
    $Job.Watch.Start()
    try {
        if ($null -eq $Job.Error) {
            Complete-ArtKindJob $Job
        }
    }
    catch {
        $Job.Error = $_.Exception.Message
    }
    finally {
        $Job.Watch.Stop()
        $script:ArtLineSink = $null
    }

    foreach ($line in $Job.Lines) {
        Write-Host $line
    }

    if ($null -ne $Job.Error) {
        Write-Host "$($Job.Name) was not exported: $($Job.Error)"
    }
    elseif (-not $script:UnityMarked) {
        # The Unity project now lags behind the export until tools/art/Update-UnityArt.ps1 succeeds, which
        # removes the marker; tools/art/Build-Art.ps1 runs it while the marker is there.
        $marker = Join-Path (Get-ArtBuildFolder "unity") "pending"
        $line = "$((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture)) Export-ArtAsset.ps1 wrote the record of $($Job.Manifest.RelativeDirectory)"
        [System.IO.File]::AppendAllText($marker, "$line`n", (New-Object System.Text.UTF8Encoding($false)))
        $script:UnityMarked = $true
    }

    Write-Host (Format-ArtElapsed $Job.Name $Job.Watch $Job.Results)
    $counts = Get-ArtCacheCounts $Job.Results
    if ($null -eq $Job.Error -and ($counts.Units -eq 0 -or $counts.Cached -lt $counts.Units)) {
        $seconds[$Job.Manifest.RelativeDirectory] = $Job.Watch.Elapsed.TotalSeconds
    }

    if ($null -ne $Report) {
        $null = $Report.Add([pscustomobject]@{
                Asset = $Job.Manifest.RelativeDirectory
                Name = $Job.Name
                Kind = $Job.Manifest.Kind
                Seconds = $Job.Watch.Elapsed.TotalSeconds
                Units = $counts.Units
                Cached = $counts.Cached
                Error = $Job.Error
            })
    }
}

# The bodies and the other skinned and static assets first, since the cage, the garments, and the paint
# layers read the fit and the paint layout of a body's export; then everything else.
$waves = New-Object System.Collections.Generic.List[object]
$waves.Add(@($manifests | Where-Object { @("static", "skinned") -contains $_.Kind }))
$waves.Add(@($manifests | Where-Object { @("static", "skinned") -notcontains $_.Kind }))
foreach ($wave in $waves) {
    if ($wave.Count -eq 0) {
        continue
    }

    # Each export is planned before the wave starts, so the content records of the other assets it
    # names are made current, and their asset.json written, before any Blender of the wave runs.
    $planned = New-Object System.Collections.Generic.List[object]
    foreach ($manifest in $wave) {
        $lines = New-Object System.Collections.Generic.List[string]
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $script:ArtLineSink = $lines
        try {
            $job = New-ArtKindJob $blender $manifest
        }
        catch {
            $script:ArtLineSink = $null
            foreach ($line in $lines) {
                Write-Host $line
            }

            throw
        }
        finally {
            $script:ArtLineSink = $null
        }

        $watch.Stop()
        $job.Lines = $lines
        $job.Watch = $watch
        $planned.Add($job)
    }

    # One at a time in the order of the records, or the longest first, alike ones in that order.
    $ordered = $planned.ToArray()
    if ($Jobs -gt 1) {
        $ordered = @(@(for ($index = 0; $index -lt $planned.Count; $index++) {
                    [pscustomobject]@{ Job = $planned[$index]; Index = $index; Seconds = Get-ArtExpectedSeconds $planned[$index].Manifest }
                }) | Sort-Object -Property @{ Expression = "Seconds"; Descending = $true }, @{ Expression = "Index"; Descending = $false } |
                ForEach-Object { $_.Job })
    }

    try {
        Invoke-ArtBlenderJobs $blender $ordered $Jobs $onDone
    }
    finally {
        Save-ArtExpectedSeconds
    }

    $failed = @($planned | Where-Object { $null -ne $_.Error })
    if ($failed.Count -gt 0) {
        throw "The export of $(@($failed | ForEach-Object { $_.Name }) -join ', ') failed:`n - $(@($failed | ForEach-Object { $_.Error }) -join "`n - ")"
    }
}
