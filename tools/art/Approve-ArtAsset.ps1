# Records the approval of one review stage of an art asset in its asset.json, after the
# reviewer has looked at the model in Blender or on the Render-ArtPreview.ps1 sheet. The
# approval stores the content fingerprint of its stage, which the validation reads from the .blend
# file, so a change of that content, or of an earlier stage's, needs a new approval before
# Export-ArtAsset.ps1 accepts it, while a file saved again with the same content keeps it. See
# docs/ART_PIPELINE.md.
param(
    [Parameter(Mandatory = $true)]
    [string]$Asset,
    # Review stages of a static asset: model. Of a skinned asset: model, rig, and animation when it
    # has clips of its own. Of an animation library: animation. Of a paint layer: paint. Of a
    # garment template: model and rig. Of the garment cage: model. Of a garment texture: texture.
    [string]$Stage = "model",
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

$manifest = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Asset)
$stageIndex = [array]::IndexOf($manifest.Stages, $Stage)
if ($stageIndex -lt 0) {
    throw "A $($manifest.Kind) asset has the review stages: $($manifest.Stages -join ', ')."
}

if ($manifest.ImplementedStages -notcontains $Stage) {
    throw "The '$Stage' stage of a $($manifest.Kind) asset cannot be checked yet, so it cannot be approved."
}

# A new library has no clips until the first is made.
if ($manifest.Kind -eq "animations" -and @($manifest.Clips).Count -eq 0) {
    throw "$($manifest.Name) has no clips yet; make one in $($manifest.SourcePath) and add its name to clips in asset.json."
}

for ($i = 0; $i -lt $stageIndex; $i++) {
    if (-not $manifest.Approvals.Contains($manifest.Stages[$i])) {
        throw "Approve the '$($manifest.Stages[$i])' stage of $($manifest.Name) before '$Stage'."
    }
}

# A template is approved only on an approved cage that was fitted to the current bodies.
if ($manifest.Kind -eq "garment") {
    $cageProblem = Get-ArtCageGateProblem $manifest.Cage
    if ($null -ne $cageProblem) {
        throw "Approve the cage $($manifest.Cage.RelativeDirectory) of $($manifest.Name) first: $cageProblem."
    }
}

# Only an asset that meets the export rules can be approved. The validation also reads the content of
# the file, which the record and the approval take.
$blender = Resolve-ArtBlender $BlenderPath
$validation = Invoke-ArtBlender $blender $manifest (@("validate") + (Get-ArtValidationArguments $manifest $Stage))
$null = Update-ArtContentRecord $blender $manifest $validation

# Every earlier stage's approval must hold for this content.
for ($i = 0; $i -lt $stageIndex; $i++) {
    $earlier = $manifest.Stages[$i]
    if (-not (Test-ArtStageHolds $manifest $earlier)) {
        throw "The '$earlier' content of $($manifest.Name).blend changed after its approval; review it and approve the '$earlier' stage again before '$Stage'."
    }
}

$manifest.Approvals[$Stage] = [ordered]@{
    date = (Get-Date).ToString("yyyy-MM-dd", [System.Globalization.CultureInfo]::InvariantCulture)
    contentSha256 = $manifest.Content.stages[$Stage]
}

# An approved cage records the exports of the bodies it was fitted to.
if ($manifest.Kind -eq "cage") {
    $manifest.FittedTo = Get-ArtCageBodies $manifest
}

Write-ArtAssetManifest $manifest
$triangles = ($validation | Where-Object { $_ -like "ART_VALIDATION *" }) -replace '^ART_VALIDATION ', ''
$what = if ($manifest.Kind -eq "animations") { "clips=$($manifest.Clips -join ',')" } else { $triangles }
Write-Host "Approved the '$Stage' stage of $($manifest.Name) ($what)."

# A later stage keeps its approval, which holds as long as the content up to it is the approved one.
foreach ($line in @(Get-ArtStageReport $manifest | Select-Object -Skip ($stageIndex + 1))) {
    Write-Host $line
}
