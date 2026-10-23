# Creates a new art asset: its folder below art/, its asset.json with the fields of its kind, and
# its first .blend file, so the next step is the work in Blender. A static model starts as an
# empty scene with the Export collection and its root objects, an animation library from the rig
# it is keyed on, optionally with clips of the gait model, a paint layer from its bodies, a
# garment from its bodies with a starting shell on the first body, and the garment cage of a
# skeleton from the first of its bodies, fitted to all of them, with the standard lines. The asset
# then goes through review, approval, and export like any other; the cage has no export. See
# docs/ART_PIPELINE.md.
param(
    # static for a model without a skeleton, animations for an animation library, paint for a paint
    # layer, garment for clothes or armor, cage for the garment cage of a skeleton. A new body type
    # starts from a human base body and comes with body types.
    [Parameter(Mandatory = $true)]
    [ValidateSet("static", "animations", "paint", "garment", "cage")]
    [string]$Kind,
    # The new asset folder art/<Category>/<Name>, such as art/WorldActors/Resource_CopperVein,
    # art/Animations/Human_Combat, art/Paint/ChainShirt, art/Garments/LinenShirt, or
    # art/Cages/Humanoid.
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
    # the drawing order from 0 to 1000, the lowest first. garment: the bodies it fits with the one
    # it is made on first, and its layer from the inside out, by default the slot's typical layer.
    # cage: the bodies it is fitted to, the one it is made from first.
    [string]$Part = "Body",
    [string[]]$Bodies = @("art/Characters/Human_Male", "art/Characters/Human_Female"),
    [int]$Layer = 20,
    # garment: its visual slot, its band from the skin, the slots it hides, what happens to the hair
    # under headwear, and its cloth profile. Its -TriangleBudget is by default the slot's budget.
    [ValidateSet("head", "body", "legs", "hands", "feet", "back", "hair")]
    [string]$Slot,
    [ValidateSet("tight", "mid", "outer", "loose")]
    [string]$Band,
    [string[]]$Hides = @(),
    [ValidateSet("hide", "compress", "show")]
    [string]$Hair,
    [ValidateSet("robe", "cloak", "hood")]
    [string]$Cloth,
    # garment: a paint layer art/Paint/<Name> whose paint on the first body shows where the starting
    # shell goes; without it, the shell covers the body parts of the slot.
    [string]$FromPaint,
    # cage: the figure scale in meters, the size of a detail the cage still spans.
    [double]$Figure = 0.06,
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
$expectedCategory = @{ animations = "Animations"; paint = "Paint"; garment = "Garments"; cage = "Cages" }[$Kind]
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
    "garment" {
        if ([string]::IsNullOrEmpty($Slot) -or [string]::IsNullOrEmpty($Band)) {
            throw "A garment needs -Slot, its visual slot, and -Band, how far from the skin it lies."
        }

        # The typical layers and triangle budgets of docs/EQUIPMENT_VISUALS_DESIGN.md.
        $layers = @{ head = 400; body = 200; legs = 100; hands = 150; feet = 120; back = 500 }
        $budgets = @{ head = 1500; body = 3000; legs = 2000; hands = 800; feet = 1000; back = 1500; hair = 2500 }
        if (-not $PSBoundParameters.ContainsKey("Layer")) {
            if (-not $layers.ContainsKey($Slot)) {
                throw "Pass -Layer for a $Slot garment."
            }

            $Layer = $layers[$Slot]
        }

        if ($TriangleBudget -le 0) {
            $TriangleBudget = $budgets[$Slot]
        }

        if ($Slot -eq "head" -and [string]::IsNullOrEmpty($Hair)) {
            throw "Headwear needs -Hair: hide, compress, or show."
        }

        $bodyPaths = @($Bodies | ForEach-Object { (Read-ArtAssetManifest (Resolve-ArtAssetDirectory $_)).RelativeDirectory })
        $lines.Add("    `"slot`": `"$Slot`",")
        $lines.Add("    `"layer`": $Layer,")
        $lines.Add("    `"band`": `"$Band`",")
        $lines.Add("    `"bodies`": [$(Format-ArtList $bodyPaths)],")
        $lines.Add("    `"hides`": [$(Format-ArtList $Hides)],")
        $lines.Add("    `"hair`": $(if ($Hair) { '"' + $Hair + '"' } else { 'null' }),")
        $lines.Add("    `"cloth`": $(if ($Cloth) { '"' + $Cloth + '"' } else { 'null' }),")
        $lines.Add('    "dyes": 0,')
        $lines.Add("    `"triangleBudget`": $TriangleBudget,")
    }
    "cage" {
        $bodyPaths = @($Bodies | ForEach-Object { (Read-ArtAssetManifest (Resolve-ArtAssetDirectory $_)).RelativeDirectory })
        $lines.Add("    `"bodies`": [$(Format-ArtList $bodyPaths)],")
        $lines.Add("    `"figure`": $(Format-ArtShare $Figure),")
        # The standard lines of docs/EQUIPMENT_VISUALS_DESIGN.md, from the neck down.
        $standard = @(
            @("CollarStand", "neck", "Neck", 0.3),
            @("HemCrop", "trunk", "Chest", 0.5),
            @("Waist", "trunk", "Spine", 0.1),
            @("HemHip", "trunk", "Hips", 0.72),
            @("SleeveShort", "arms", "UpperArm", 0.45),
            @("SleeveElbow", "arms", "LowerArm", 0.05),
            @("GloveCuff", "arms", "LowerArm", 0.65),
            @("SleeveLong", "arms", "LowerArm", 0.92),
            @("LegShorts", "legs", "UpperLeg", 0.5),
            @("LegKnee", "legs", "LowerLeg", 0.1),
            @("BootCalf", "legs", "LowerLeg", 0.55),
            @("LegAnkle", "legs", "LowerLeg", 0.9))
        $lines.Add('    "lines": [')
        for ($i = 0; $i -lt $standard.Count; $i++) {
            $comma = if ($i -lt $standard.Count - 1) { "," } else { "" }
            $line = $standard[$i]
            $lines.Add("        { `"name`": `"$($line[0])`", `"region`": `"$($line[1])`", `"bone`": `"$($line[2])`", `"share`": $(Format-ArtShare $line[3]) }$comma")
        }
        $lines.Add('    ],')
    }
}
if ($Kind -eq "cage") {
    $lines.Add('    "approvals": {}')
}
else {
    $lines.Add('    "approvals": {},')
    $lines.Add('    "export": null')
}
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
        "garment" {
            $arguments = @(
                "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
                "--python", $script:ArtBlenderCli, "--", "garment-create",
                "--file", $manifest.SourcePath, "--garment", $manifest.Name, "--slot", $Slot, "--band", $Band)
            $arguments += @($manifest.Bodies | ForEach-Object { @("--body", "$($_.SourcePath)=$($_.Root)") })
            if ($FromPaint) {
                $paint = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $FromPaint)
                if ($paint.Kind -ne "paint" -or $paint.Bodies[0].Root -cne $manifest.RootObjects[0]) {
                    throw "$FromPaint must be a paint layer painted on $($manifest.RootObjects[0])."
                }

                $arguments += @("--paint-file", $paint.SourcePath, "--paint-name", $paint.Name, "--part", $paint.Part)
            }

            $blender = Resolve-ArtBlender $BlenderPath
            $output = Invoke-ArtNative $blender $arguments
            if ($script:ArtNativeExitCode -ne 0 -or @($output | Where-Object { $_ -like "ART_GARMENT created *" }).Count -eq 0) {
                throw "Blender could not write $($manifest.SourcePath):`n$(($output | Select-Object -Last 20) -join "`n")"
            }

            foreach ($line in @($output | Where-Object { $_ -like "ART_REPORT *" })) {
                Write-Host $line.Substring("ART_REPORT ".Length)
            }
        }
        "cage" {
            $arguments = @(
                "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
                "--python", $script:ArtBlenderCli, "--", "cage-create",
                "--file", $manifest.SourcePath, "--manifest", $manifest.ManifestPath)
            $arguments += @($manifest.Bodies | ForEach-Object { @("--body", "$($_.SourcePath)=$($_.Root)") })
            $blender = Resolve-ArtBlender $BlenderPath
            $output = Invoke-ArtNative $blender $arguments
            if ($script:ArtNativeExitCode -ne 0 -or @($output | Where-Object { $_ -like "ART_CAGE created *" }).Count -eq 0) {
                throw "Blender could not write $($manifest.SourcePath):`n$(($output | Select-Object -Last 20) -join "`n")"
            }

            foreach ($line in @($output | Where-Object { $_ -like "ART_REPORT *" })) {
                Write-Host $line.Substring("ART_REPORT ".Length)
            }
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
    "garment" {
        Write-Host "Shape ${name}_$($manifest.RootObjects[0]) on the first body, run tools/Sync-GarmentAsset.ps1, then review, approve, and export it as docs/LOCAL_DEVELOPMENT.md describes under Make A Garment."
    }
    "cage" {
        Write-Host "Run tools/Sync-CageAsset.ps1 for its review sheet and checks, then review and approve it as docs/LOCAL_DEVELOPMENT.md describes under Fit The Garment Cage."
    }
}
