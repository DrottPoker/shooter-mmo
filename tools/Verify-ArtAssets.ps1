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
    if ($targets.ContainsKey($manifest.Target)) {
        $problems.Add("${where}: $($manifest.Target) is also the target of $($targets[$manifest.Target]).")
    }

    $targets[$manifest.Target] = $where
    $sourceSha256 = Get-ArtFileSha256 $manifest.SourcePath
    if (Test-ArtAssetInReview $manifest) {
        $reviewProblem = Get-ArtReviewProblem $manifest $sourceSha256
        if ($null -ne $reviewProblem) {
            $problems.Add("${where}: $reviewProblem.")
        }

        if ($null -ne $manifest.Export) {
            $problems.Add("${where}: an asset in review has no export; export it after its last review stage.")
        }

        if (Test-Path -LiteralPath $manifest.TargetPath) {
            $problems.Add("${where}: $($manifest.Target) exists although the asset is still in review.")
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

    if ($manifest.Export.sourceSha256 -ne $sourceSha256) {
        $problems.Add("${where}: $($manifest.Target) was exported from another version of $($manifest.Name).blend; run tools/Export-ArtAsset.ps1.")
    }

    if (-not (Test-Path -LiteralPath $manifest.TargetPath -PathType Leaf)) {
        $problems.Add("${where}: $($manifest.Target) is missing; run tools/Export-ArtAsset.ps1.")
    }
    elseif ((Get-ArtFileSha256 $manifest.TargetPath) -ne $manifest.Export.targetSha256) {
        $problems.Add("${where}: $($manifest.Target) differs from the recorded export; export it with tools/Export-ArtAsset.ps1 instead of changing it by hand.")
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
    $modelExtensions = @(".fbx", ".obj", ".blend", ".gltf", ".glb", ".dae", ".3ds", ".max", ".ma", ".mb")
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
