# Creates a new art asset: its folder below art/, its asset.json with the fields of its kind, and
# its first .blend file, so the next step is the work in Blender. A static model starts as an
# empty scene with the Export collection and its root objects, an animation library from the rig
# it is keyed on, optionally with clips of the gait model, and a paint layer from its bodies. The
# asset then goes through review, approval, and export like any other. See docs/ART_PIPELINE.md.
param(
    # static for a model without a skeleton, animations for an animation library, paint for a paint
    # layer. A new body type starts from a human base body and comes with body types.
    [Parameter(Mandatory = $true)]
    [ValidateSet("static", "animations", "paint")]
    [string]$Kind,
    # The new asset folder art/<Category>/<Name>, such as art/WorldActors/Resource_CopperVein,
    # art/Animations/Human_Combat, or art/Paint/ChainShirt.
    [Parameter(Mandatory = $true)]
    [string]$Asset,
    # static: the names of the top-level objects the game code expects, such as Available, Depleted.
    [string[]]$Roots = @(),
    # static: the largest number of triangles the model may export.
    [int]$TriangleBudget = 0,
    # animations: the skinned asset whose rig the clips are keyed on, and other bodies that show them.
    [string]$Rig = "art/Characters/Human_Male",
    [string[]]$Previews = @("art/Characters/Human_Female"),
    # animations: clips of the gait model to start with, from Idle, Walk, and Jog.
    [string[]]$Generate = @(),
    # paint: the painted material part, the bodies the paint fits with the painted one first, and
    # the drawing order from 0 to 1000, the lowest first.
    [string]$Part = "Body",
    [string[]]$Bodies = @("art/Characters/Human_Male", "art/Characters/Human_Female"),
    [int]$Layer = 20,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

$relative = ($Asset -replace '\\', '/').TrimEnd('/')
if ($relative -cnotmatch '^art/[A-Za-z][A-Za-z0-9_]*/[A-Za-z][A-Za-z0-9_]*$') {
    throw "Pass the new asset folder as art/<Category>/<Name>, such as art/WorldActors/Resource_CopperVein."
}

$parts = $relative.Split('/')
$category, $name = $parts[1], $parts[2]
$expectedCategory = @{ animations = "Animations"; paint = "Paint" }[$Kind]
if ($null -ne $expectedCategory -and $category -cne $expectedCategory) {
    throw "A new $Kind asset goes below art/$expectedCategory, such as art/$expectedCategory/$name."
}

$directory = Join-Path $script:ArtRepositoryRoot $relative
if (Test-Path -LiteralPath $directory) {
    throw "$relative exists already; a new asset needs a folder of its own."
}

function Format-ArtList([string[]]$Values) {
    return (@($Values) | ForEach-Object { '"' + $_ + '"' }) -join ", "
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("{")
$lines.Add('    "schemaVersion": 1,')
$lines.Add("    `"kind`": `"$Kind`",")
switch ($Kind) {
    "static" {
        if ($Roots.Count -eq 0 -or $TriangleBudget -le 0) {
            throw "A static asset needs -Roots, the names of its top-level objects, and -TriangleBudget."
        }

        $lines.Add("    `"target`": `"$script:ArtModelFolder/$category/$name.fbx`",")
        $lines.Add('    "rootObjects": [')
        for ($i = 0; $i -lt $Roots.Count; $i++) {
            $comma = if ($i -lt $Roots.Count - 1) { "," } else { "" }
            $lines.Add("        `"$($Roots[$i])`"$comma")
        }
        $lines.Add("    ],")
        $lines.Add("    `"triangleBudget`": $TriangleBudget,")
    }
    "animations" {
        $rigManifest = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Rig)
        if ($rigManifest.Kind -ne "skinned") {
            throw "$Rig is a $($rigManifest.Kind) asset; an animation library is keyed on the rig of a skinned asset."
        }

        $lines.Add("    `"target`": `"$script:ArtModelFolder/$category/$name.fbx`",")
        $lines.Add('    "rootObjects": [')
        $lines.Add("        `"$($rigManifest.Skeleton)`"")
        $lines.Add("    ],")
        $lines.Add("    `"skeleton`": `"$($rigManifest.Skeleton)`",")
        $lines.Add("    `"rig`": `"$($rigManifest.RelativeDirectory)`",")
        $previewPaths = @($Previews | ForEach-Object { (Read-ArtAssetManifest (Resolve-ArtAssetDirectory $_)).RelativeDirectory })
        $lines.Add("    `"previews`": [$(Format-ArtList $previewPaths)],")
    }
    "paint" {
        $bodyPaths = @($Bodies | ForEach-Object { (Read-ArtAssetManifest (Resolve-ArtAssetDirectory $_)).RelativeDirectory })
        $lines.Add("    `"part`": `"$Part`",")
        $lines.Add("    `"bodies`": [$(Format-ArtList $bodyPaths)],")
        $lines.Add("    `"layer`": $Layer,")
    }
}
$lines.Add('    "approvals": {},')
$lines.Add('    "export": null')
$lines.Add("}")

New-Item -ItemType Directory -Path $directory | Out-Null
try {
    $manifestPath = Join-Path $directory "asset.json"
    [System.IO.File]::WriteAllText($manifestPath, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
    $manifest = Read-ArtAssetManifest $directory -AllowMissingSource

    switch ($Kind) {
        "static" {
            $blender = Resolve-ArtBlender $BlenderPath
            $output = Invoke-ArtNative $blender @(
                "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
                "--python", $script:ArtBlenderCli, "--", "new-static",
                "--file", $manifest.SourcePath, "--roots", ($Roots -join ","))
            if ($script:ArtNativeExitCode -ne 0 -or @($output | Where-Object { $_ -like "ART_NEW *" }).Count -eq 0) {
                throw "Blender could not write $($manifest.SourcePath):`n$(($output | Select-Object -Last 20) -join "`n")"
            }
        }
        "animations" {
            & (Join-Path $PSScriptRoot "Sync-AnimationLibrary.ps1") -Asset $directory -Generate $Generate -BlenderPath $BlenderPath
        }
        "paint" {
            & (Join-Path $PSScriptRoot "Sync-PaintAsset.ps1") -Asset $directory -BlenderPath $BlenderPath
        }
    }
}
catch {
    # A half-made asset would only stand in the way of the next attempt.
    Remove-Item -LiteralPath $directory -Recurse -Force
    throw
}

Write-Host "Created $relative with asset.json and $name.blend."
Write-Host "Open it in Blender: & `$env:BLENDER_PATH $relative/$name.blend"
switch ($Kind) {
    "static" {
        Write-Host "Model below the roots $($Roots -join ', ') in the Export collection, then review, approve, and export it as docs/LOCAL_DEVELOPMENT.md describes under Add A New Asset."
    }
    "animations" {
        Write-Host "Make the clips on $($manifest.RootObjects[0]) and add their names to clips in asset.json, then review, approve, and export it as docs/LOCAL_DEVELOPMENT.md describes under Make A Clip."
    }
    "paint" {
        Write-Host "Paint T_Paint_${name}_$((Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Bodies[0])).RootObjects[0]) on the first body, run tools/Sync-PaintAsset.ps1 again, then review, approve, and export it as docs/LOCAL_DEVELOPMENT.md describes under Make A Paint Layer."
    }
}
