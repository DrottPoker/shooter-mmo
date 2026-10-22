# Records the approval of one review stage of an art asset in its asset.json, after the
# reviewer has looked at the model in Blender or on the Render-ArtPreview.ps1 sheet. The
# approval stores the SHA-256 of the .blend file, so any later change to the file needs a new
# approval before Export-ArtAsset.ps1 accepts it. See docs/ART_PIPELINE.md.
param(
    [Parameter(Mandatory = $true)]
    [string]$Asset,
    # Review stages of a static asset: model. Of a skinned asset: model, rig, and animation when it
    # has clips of its own. Of an animation library: animation. Of a paint layer: paint.
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

# Only an asset that meets the export rules can be approved.
$blender = Resolve-ArtBlender $BlenderPath
$validation = Invoke-ArtBlender $blender $manifest (@("validate") + (Get-ArtValidationArguments $manifest $Stage))

$manifest.Approvals[$Stage] = [ordered]@{
    date = (Get-Date).ToString("yyyy-MM-dd", [System.Globalization.CultureInfo]::InvariantCulture)
    sourceSha256 = Get-ArtFileSha256 $manifest.SourcePath
}

# A new approval of an earlier stage reopens every later stage.
for ($i = $stageIndex + 1; $i -lt $manifest.Stages.Count; $i++) {
    $manifest.Approvals.Remove($manifest.Stages[$i])
}

Write-ArtAssetManifest $manifest
$triangles = ($validation | Where-Object { $_ -like "ART_VALIDATION *" }) -replace '^ART_VALIDATION ', ''
$what = if ($manifest.Kind -eq "animations") { "clips=$($manifest.Clips -join ',')" } else { $triangles }
Write-Host "Approved the '$Stage' stage of $($manifest.Name) ($what)."
