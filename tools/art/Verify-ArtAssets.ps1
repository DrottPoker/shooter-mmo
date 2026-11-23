# Checks every art asset without Blender, for CI: each asset.json is valid, its content record was
# read from the current .blend file, every review stage is approved and every approval holds for that
# content, the committed FBX and textures are the recorded export of that content, and every model
# below src/Client/Assets/Art/Models and every texture below src/Client/Assets/Art/Textures comes from
# exactly one asset. An asset in review, such as a skinned model before its rig, has approvals that
# hold for its content and no export yet. Git LFS pointers are read directly, so the .blend files need
# not be downloaded. -Waiting names assets that wait for an approval, such as art/Garments/Trousers, as
# tools/art/Build-Art.ps1 passes them: their problems are listed and do not fail the verification;
# every other problem does, also one that belongs to no asset, such as a stray file or a target or
# material of two assets.
param(
    [string[]]$Waiting = @()
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

$problems = New-Object System.Collections.Generic.List[string]
$waitingAssets = @($Waiting | ForEach-Object { "$_".Split(",") } | ForEach-Object { $_.Trim().Replace([char]92, [char]47).TrimEnd([char]47) } | Where-Object { $_ })
$waitingProblems = New-Object System.Collections.Generic.List[string]

function Add-ArtAssetProblem([string]$Asset, [string]$Problem) {
    # A problem of one asset, which does not fail the verification while the asset waits for an approval.
    if ($waitingAssets -ccontains $Asset) {
        $waitingProblems.Add($Problem)
    }
    else {
        $problems.Add($Problem)
    }
}

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

    # The garment cage has no export: it is complete when its stage is approved for its content and
    # its bodies still have the exports it was approved on.
    if ($manifest.Kind -eq "cage") {
        $problem = if (Test-ArtAssetInReview $manifest) { Get-ArtReviewProblem $manifest } else { Get-ArtExportGateProblem $manifest }
        if ($null -eq $problem -and -not (Test-ArtAssetInReview $manifest)) {
            $problem = Get-ArtCageBodyProblem $manifest
        }

        if ($null -ne $problem) {
            Add-ArtAssetProblem $where "${where}: $problem."
        }

        continue
    }

    if (Test-ArtAssetInReview $manifest) {
        $reviewProblem = Get-ArtReviewProblem $manifest
        if ($null -ne $reviewProblem) {
            Add-ArtAssetProblem $where "${where}: $reviewProblem."
        }

        if ($null -ne $manifest.Export) {
            Add-ArtAssetProblem $where "${where}: an asset in review has no export; export it after its last review stage."
        }

        foreach ($target in $modelTargets) {
            if (Test-Path -LiteralPath (Join-Path $script:ArtRepositoryRoot $target)) {
                Add-ArtAssetProblem $where "${where}: $target exists although the asset is still in review."
            }
        }

        continue
    }

    $gateProblem = Get-ArtExportGateProblem $manifest
    if ($null -ne $gateProblem) {
        Add-ArtAssetProblem $where "${where}: $gateProblem."
    }

    if ($null -eq $manifest.Export) {
        Add-ArtAssetProblem $where "${where}: the asset was never exported; run tools/art/Export-ArtAsset.ps1."
        continue
    }

    if ($null -ne $manifest.ExportProblem) {
        Add-ArtAssetProblem $where "${where}/asset.json: $($manifest.ExportProblem)."
    }

    # The gate reports a content record that is not current.
    if ($null -eq (Get-ArtContentProblem $manifest) -and $manifest.Export.contentSha256 -cne (Get-ArtExportedContent $manifest)) {
        Add-ArtAssetProblem $where "${where}: the export comes from another version of $($manifest.Name).blend; run tools/art/Export-ArtAsset.ps1."
    }

    if ($manifest.Kind -eq "texture") {
        # The texture was exported against the current content of its template, and is painted in the
        # template's current UVs. A template whose content record is not current is reported with it.
        if ((Get-ArtExportedContent $manifest.Garment) -cne $manifest.Export.garmentContentSha256) {
            Add-ArtAssetProblem $where "${where}: was exported against other content of its template $($manifest.Garment.RelativeDirectory); run tools/art/Sync-TextureAsset.ps1, review and approve it if it changed, then run tools/art/Export-ArtAsset.ps1."
        }

        $templateProblem = Get-ArtTextureTemplateProblem $manifest
        if ($null -ne $templateProblem) {
            Add-ArtAssetProblem $where "${where}: $templateProblem."
        }

        # A texture with masks has them for exactly the bodies of its template.
        $masked = @($manifest.Export.masks | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_.body } | Sort-Object)
        $roots = @($manifest.Masks | ForEach-Object { $_.Body } | Sort-Object)
        if ($masked.Count -gt 0 -and ($masked -join ",") -cne ($roots -join ",")) {
            Add-ArtAssetProblem $where "${where}: has masks for other bodies ($($masked -join ', ')) than its template has ($($roots -join ', ')); run tools/art/Sync-TextureAsset.ps1, review and approve it, then run tools/art/Export-ArtAsset.ps1."
        }
    }

    # A texture with masks of its own names the faces they hide of these exports of the templates of
    # lower layers worn under its template. No clip: its masks' checks step no frame of a clip.
    if ($manifest.Kind -eq "texture" -and @($manifest.Export.masks | Where-Object { $null -ne $_ }).Count -gt 0) {
        $pairProblem = Get-ArtPairSourcesProblem $manifest $manifest.Garment "tools/art/Sync-TextureAsset.ps1"
        if ($null -ne $pairProblem) {
            Add-ArtAssetProblem $where "${where}: $pairProblem."
        }
    }

    if ($manifest.Kind -eq "paint") {
        # The paint was moved to the current exports of its bodies, and through the current cage.
        foreach ($dependencyProblem in @(Get-ArtPaintDependencyProblems $manifest)) {
            Add-ArtAssetProblem $where "${where}: $dependencyProblem."
        }
    }

    if ($manifest.Kind -eq "garment") {
        # The template still matches its cage, the bodies of the cage, and the content of the templates
        # of lower layers it was checked over; a new or changed clip needs no garment exported.
        foreach ($dependencyProblem in @(Get-ArtGarmentDependencyProblems $manifest)) {
            Add-ArtAssetProblem $where "${where}: $dependencyProblem."
        }
    }

    # The written files are the recorded export.
    foreach ($fileProblem in @(Get-ArtExportFileProblems $manifest)) {
        Add-ArtAssetProblem $where "${where}: $fileProblem."
    }

    # An animation library was exported against the current content of its rig; the export itself
    # checked in Blender that the library's armature is that rig.
    if ($manifest.Kind -eq "animations") {
        $rig = $null
        try {
            $rig = Read-ArtAssetManifest (Split-Path -Parent $manifest.Rig.ManifestPath)
        }
        catch {
            # The rig's own problems are reported with the rig.
        }

        if ($null -eq $rig -or (Get-ArtExportedContent $rig) -cne $manifest.Export.rigContentSha256) {
            Add-ArtAssetProblem $where "${where}: was exported against other content of its rig $($manifest.Rig.Asset); run tools/art/Sync-AnimationLibrary.ps1, and if it changed the library, review and approve it again, then run tools/art/Export-ArtAsset.ps1."
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
            $problems.Add("${relative}: every model comes from an approved asset in art/ through tools/art/Export-ArtAsset.ps1.")
        }
    }
}

$textureRoot = Join-Path $script:ArtRepositoryRoot $script:ArtTextureFolder
if (Test-Path -LiteralPath $textureRoot) {
    foreach ($file in @(Get-ChildItem -LiteralPath $textureRoot -Recurse -File | Where-Object { $_.Extension -ne ".meta" })) {
        $relative = Get-ArtRelativePath $file.FullName
        if (-not $textures.ContainsKey($relative)) {
            $problems.Add("${relative}: every texture comes from an approved asset in art/ through tools/art/Export-ArtAsset.ps1.")
        }
    }
}

# The baseline of the game views was measured on the committed content of every asset its views read; a
# view that one of its assets waiting for an approval changed is that asset's problem.
. (Join-Path $PSScriptRoot "ArtViews.ps1")
$viewProblems = @()
try {
    $viewProblems = @(Get-ArtViewBaselineProblems)
}
catch {
    $problems.Add($_.Exception.Message)
}

foreach ($viewProblem in $viewProblems) {
    $owner = Get-ArtViewProblemOwner $viewProblem $waitingAssets
    if ($null -ne $owner) {
        Add-ArtAssetProblem $owner $viewProblem.Problem
    }
    else {
        $problems.Add($viewProblem.Problem)
    }
}

if ($waitingProblems.Count -gt 0) {
    Write-Host "These problems belong to assets that wait for an approval, so they do not fail the verification:`n - $($waitingProblems -join "`n - ")"
}

if ($problems.Count -gt 0) {
    throw "Art asset verification failed:`n - $($problems -join "`n - ")"
}

$waitingNote = if ($waitingProblems.Count -gt 0) { " $($waitingProblems.Count) problem(s) above wait for an approval." } else { "" }
Write-Host "Verified $($directories.Count) art asset(s).$waitingNote"
if ($viewProblems.Count -eq 0) {
    Write-Host "The baseline of the game views holds for the committed content of their $(@(Read-ArtViewSet).Count) view(s)."
}
