# Checks every art asset without Blender, for CI: each asset.json is valid, every review stage
# is approved for the current .blend file, the committed FBX and textures are the recorded
# export of that approved file, and every model below src/Client/Assets/Art/Models and every
# texture below src/Client/Assets/Art/Textures comes from exactly one asset. An asset in review, such as a skinned model before its rig, has its latest approval on
# the current .blend file and no export yet. Git LFS pointers are read directly, so the .blend
# files need not be downloaded.
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

$problems = New-Object System.Collections.Generic.List[string]
$targets = @{}
$textures = @{}
$materials = @{}
$directories = @(Get-ArtAssetDirectories)
foreach ($directory in $directories) {
    try {
        $manifest = Read-ArtAssetManifest $directory
    }
    catch {
        $problems.Add($_.Exception.Message)
        continue
    }

    $where = $manifest.RelativeDirectory
    # A paint layer exports images only, so it has no model target, a garment a model and its data
    # per body, also of a body that left its cage until it is exported again, and a texture with
    # transparent parts its masks per body of its template.
    $modelTargets = @(if ($manifest.Kind -eq "garment") {
            @(@($manifest.Models | ForEach-Object { $_.Target; $_.Data }) +
                @($manifest.Export.models | Where-Object { $null -ne $_ } | ForEach-Object { $_.target; $_.data }) | Sort-Object -Unique)
        }
        elseif ($manifest.Kind -eq "texture") {
            if ($null -ne $manifest.Export) { @($manifest.Export.masks | Where-Object { $null -ne $_ } | ForEach-Object { $_.data }) }
            else { @($manifest.Masks | ForEach-Object { $_.Data }) }
        }
        elseif ($null -ne $manifest.Target) { @($manifest.Target) })
    # A body with an editable mesh exports its topology next to its model.
    if ($null -ne $manifest.Export -and $manifest.Export.Contains("dataSha256") -and $null -ne $manifest.Export.dataSha256) {
        $modelTargets += Get-ArtDataTarget $manifest.Target
    }
    foreach ($target in $modelTargets) {
        if ($targets.ContainsKey($target)) {
            $problems.Add("${where}: $target is also the target of $($targets[$target]).")
        }

        $targets[$target] = $where
    }

    $sourceSha256 = Get-ArtFileSha256 $manifest.SourcePath
    # The garment cage has no export: it is complete when its stage is approved for its file and its
    # bodies still have the exports it was approved on.
    if ($manifest.Kind -eq "cage") {
        $problem = if (Test-ArtAssetInReview $manifest) { Get-ArtReviewProblem $manifest $sourceSha256 } else { Get-ArtExportGateProblem $manifest $sourceSha256 }
        if ($null -eq $problem -and -not (Test-ArtAssetInReview $manifest)) {
            $problem = Get-ArtCageBodyProblem $manifest
        }

        if ($null -ne $problem) {
            $problems.Add("${where}: $problem.")
        }

        continue
    }

    if (Test-ArtAssetInReview $manifest) {
        $reviewProblem = Get-ArtReviewProblem $manifest $sourceSha256
        if ($null -ne $reviewProblem) {
            $problems.Add("${where}: $reviewProblem.")
        }

        if ($null -ne $manifest.Export) {
            $problems.Add("${where}: an asset in review has no export; export it after its last review stage.")
        }

        foreach ($target in $modelTargets) {
            if (Test-Path -LiteralPath (Join-Path $script:ArtRepositoryRoot $target)) {
                $problems.Add("${where}: $target exists although the asset is still in review.")
            }
        }

        continue
    }

    $gateProblem = Get-ArtExportGateProblem $manifest $sourceSha256
    if ($null -ne $gateProblem) {
        $problems.Add("${where}: $gateProblem.")
    }

    if ($null -eq $manifest.Export) {
        $problems.Add("${where}: the asset was never exported; run tools/Export-ArtAsset.ps1.")
        continue
    }

    if ($null -ne $manifest.ExportProblem) {
        $problems.Add("${where}/asset.json: $($manifest.ExportProblem).")
    }

    if ($manifest.Export.sourceSha256 -ne $sourceSha256) {
        $problems.Add("${where}: the export comes from another version of $($manifest.Name).blend; run tools/Export-ArtAsset.ps1.")
    }

    if ($manifest.Kind -eq "texture") {
        # The texture was exported against the current export of its template.
        $templateExport = $null
        try {
            $templateExport = (Read-ArtAssetManifest $manifest.Garment.Directory).Export
        }
        catch {
            # The template's own problems are reported with the template.
        }

        if ($null -eq $templateExport -or $templateExport.sourceSha256 -ne $manifest.Export.garmentSourceSha256) {
            $problems.Add("${where}: was exported against another export of its template $($manifest.Garment.RelativeDirectory); run tools/Sync-TextureAsset.ps1, review and approve it if it changed, then run tools/Export-ArtAsset.ps1.")
        }
        elseif ($manifest.Approvals["texture"].garmentSourceSha256 -ne $manifest.Export.garmentSourceSha256) {
            $problems.Add("${where}: was approved on another file of its template $($manifest.Garment.RelativeDirectory) than it was exported against; run tools/Sync-TextureAsset.ps1, review and approve it again, then run tools/Export-ArtAsset.ps1.")
        }

        # A texture with masks has them for exactly the bodies of its template.
        $masked = @($manifest.Export.masks | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_.body } | Sort-Object)
        $roots = @($manifest.Masks | ForEach-Object { $_.Body } | Sort-Object)
        if ($masked.Count -gt 0 -and ($masked -join ",") -cne ($roots -join ",")) {
            $problems.Add("${where}: has masks for other bodies ($($masked -join ', ')) than its template has ($($roots -join ', ')); run tools/Sync-TextureAsset.ps1, review and approve it, then run tools/Export-ArtAsset.ps1.")
        }

        foreach ($mask in @($manifest.Export.masks | Where-Object { $null -ne $_ })) {
            $maskPath = Join-Path $script:ArtRepositoryRoot $mask.data
            if (-not (Test-Path -LiteralPath $maskPath -PathType Leaf)) {
                $problems.Add("${where}: $($mask.data) is missing; run tools/Export-ArtAsset.ps1.")
            }
            elseif ((Get-ArtFileSha256 $maskPath) -ne $mask.dataSha256) {
                $problems.Add("${where}: $($mask.data) differs from the recorded export; export it with tools/Export-ArtAsset.ps1 instead of changing it by hand.")
            }
        }
    }

    # A texture with masks of its own was checked in the clips of these libraries.
    if ($manifest.Kind -eq "texture" -and @($manifest.Export.masks | Where-Object { $null -ne $_ }).Count -gt 0) {
        $clipProblem = Get-ArtClipSourcesProblem $manifest "tools/Sync-TextureAsset.ps1"
        if ($null -ne $clipProblem) {
            $problems.Add("${where}: $clipProblem.")
        }
    }

    if ($manifest.Kind -eq "paint") {
        # The paint was moved to the current exports of its bodies, and through the current cage.
        foreach ($dependencyProblem in @(Get-ArtPaintDependencyProblems $manifest)) {
            $problems.Add("${where}: $dependencyProblem.")
        }
    }

    if ($manifest.Kind -eq "garment") {
        # The template still matches its cage, the bodies of the cage, and the clips it was checked in.
        foreach ($dependencyProblem in @(Get-ArtGarmentDependencyProblems $manifest)) {
            $problems.Add("${where}: $dependencyProblem.")
        }

        foreach ($model in @($manifest.Export.models)) {
            foreach ($file in @(@($model.target, $model.sha256), @($model.data, $model.dataSha256))) {
                $filePath = Join-Path $script:ArtRepositoryRoot $file[0]
                if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
                    $problems.Add("${where}: $($file[0]) is missing; run tools/Export-ArtAsset.ps1.")
                }
                elseif ((Get-ArtFileSha256 $filePath) -ne $file[1]) {
                    $problems.Add("${where}: $($file[0]) differs from the recorded export; export it with tools/Export-ArtAsset.ps1 instead of changing it by hand.")
                }
            }
        }
    }
    elseif ($null -eq $manifest.Target) {
        # Only the images below.
    }
    elseif (-not (Test-Path -LiteralPath $manifest.TargetPath -PathType Leaf)) {
        $problems.Add("${where}: $($manifest.Target) is missing; run tools/Export-ArtAsset.ps1.")
    }
    elseif ((Get-ArtFileSha256 $manifest.TargetPath) -ne $manifest.Export.targetSha256) {
        $problems.Add("${where}: $($manifest.Target) differs from the recorded export; export it with tools/Export-ArtAsset.ps1 instead of changing it by hand.")
    }

    if ($null -ne $manifest.Target -and $manifest.Export.Contains("dataSha256") -and $null -ne $manifest.Export.dataSha256) {
        $dataTarget = Get-ArtDataTarget $manifest.Target
        $dataPath = Join-Path $script:ArtRepositoryRoot $dataTarget
        if (-not (Test-Path -LiteralPath $dataPath -PathType Leaf)) {
            $problems.Add("${where}: $dataTarget is missing; run tools/Export-ArtAsset.ps1.")
        }
        elseif ((Get-ArtFileSha256 $dataPath) -ne $manifest.Export.dataSha256) {
            $problems.Add("${where}: $dataTarget differs from the recorded export; export it with tools/Export-ArtAsset.ps1 instead of changing it by hand.")
        }
    }

    # An animation library was exported against the current export of its rig; the export itself
    # checked in Blender that the library's armature is that rig.
    if ($manifest.Kind -eq "animations") {
        $rigExport = $null
        try {
            $rigExport = (Read-ArtAssetManifest (Split-Path -Parent $manifest.Rig.ManifestPath)).Export
        }
        catch {
            # The rig's own problems are reported with the rig.
        }

        if ($null -eq $rigExport -or $rigExport.sourceSha256 -ne $manifest.Export.rigSourceSha256) {
            $problems.Add("${where}: was exported against another export of its rig $($manifest.Rig.Asset); run tools/Sync-AnimationLibrary.ps1, and if it changed the library, review and approve it again, then run tools/Export-ArtAsset.ps1.")
        }
    }

    # Unity finds a project material by its name, so no two assets export one of the same name.
    foreach ($material in @($manifest.Export.materials | Where-Object { $null -ne $_ })) {
        if ($materials.ContainsKey($material.name)) {
            $problems.Add("${where}: the material $($material.name) is also a material of $($materials[$material.name]); give one of them another name.")
        }

        $materials[$material.name] = $where
    }

    foreach ($texture in @($manifest.Export.textures)) {
        if ($textures.ContainsKey($texture.target)) {
            $problems.Add("${where}: $($texture.target) is also a texture of $($textures[$texture.target]).")
        }

        $textures[$texture.target] = $where
        $texturePath = Join-Path $script:ArtRepositoryRoot $texture.target
        if (-not (Test-Path -LiteralPath $texturePath -PathType Leaf)) {
            $problems.Add("${where}: $($texture.target) is missing; run tools/Export-ArtAsset.ps1.")
        }
        elseif ((Get-ArtFileSha256 $texturePath) -ne $texture.sha256) {
            $problems.Add("${where}: $($texture.target) differs from the recorded export; export it with tools/Export-ArtAsset.ps1 instead of changing it by hand.")
        }
    }
}

$modelRoot = Join-Path $script:ArtRepositoryRoot $script:ArtModelFolder
if (Test-Path -LiteralPath $modelRoot) {
    # A garment's data lies next to its model.
    $modelExtensions = @(".fbx", ".obj", ".blend", ".gltf", ".glb", ".dae", ".3ds", ".max", ".ma", ".mb", ".json")
    $models = @(Get-ChildItem -LiteralPath $modelRoot -Recurse -File |
        Where-Object { $modelExtensions -contains $_.Extension.ToLowerInvariant() })
    foreach ($model in $models) {
        $relative = Get-ArtRelativePath $model.FullName
        if (-not $targets.ContainsKey($relative)) {
            $problems.Add("${relative}: every model comes from an approved asset in art/ through tools/Export-ArtAsset.ps1.")
        }
    }
}

$textureRoot = Join-Path $script:ArtRepositoryRoot $script:ArtTextureFolder
if (Test-Path -LiteralPath $textureRoot) {
    foreach ($file in @(Get-ChildItem -LiteralPath $textureRoot -Recurse -File | Where-Object { $_.Extension -ne ".meta" })) {
        $relative = Get-ArtRelativePath $file.FullName
        if (-not $textures.ContainsKey($relative)) {
            $problems.Add("${relative}: every texture comes from an approved asset in art/ through tools/Export-ArtAsset.ps1.")
        }
    }
}

if ($problems.Count -gt 0) {
    throw "Art asset verification failed:`n - $($problems -join "`n - ")"
}

Write-Host "Verified $($directories.Count) art asset(s)."
