# Creates a new art asset: its folder below art/, its asset.json with the fields of its kind, and
# its first .blend file, so the next step is the work in Blender. A static model starts as an
# empty scene with the Export collection and its root objects, an animation library from the rig
# it is keyed on, optionally with clips of the gait model, a paint layer from its bodies, the
# garment cage of a skeleton from the first of its bodies, fitted to all of them, with the standard
# lines, a garment template on a cage from its recipe, and a garment texture for a template. The
# asset then goes through review, approval, and export like any other; the cage has no export. See
# docs/ART_PIPELINE.md.
param(
    # static for a model without a skeleton, animations for an animation library, paint for a paint
    # layer, cage for the garment cage of a skeleton, garment for a garment template, texture for the
    # look of a template. A new body type starts from a human base body and comes with body types.
    [Parameter(Mandatory = $true)]
    [ValidateSet("static", "animations", "paint", "garment", "cage", "texture")]
    [string]$Kind,
    # The new asset folder art/<Category>/<Name>, such as art/WorldActors/Resource_CopperVein,
    # art/Animations/Human_Combat, art/Paint/ChainShirt, art/Cages/Humanoid, art/Garments/Shirt, or
    # art/GarmentTextures/LinenShirt.
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
    # the drawing order from 0 to 1000, the lowest first. garment: its layer from the inside out, by
    # default the slot's typical layer. cage: the bodies it is fitted to, the one it is made from
    # first.
    [string]$Part = "Body",
    [string[]]$Bodies = @("art/Characters/Human_Male", "art/Characters/Human_Female"),
    [int]$Layer = 20,
    # garment: its visual slot, its band from the cage, the slots it hides, what happens to the hair
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
    # garment: the cage it is made on and its recipe: the cage regions it covers, its cuts as
    # <Line>:head or <Line>:tail, the side of each line it keeps, its ease at the armpits, and the
    # depth of its turned edges, in meters. -Modeled makes a template whose first version a person
    # models by hand instead.
    [string]$Cage = "art/Cages/Humanoid",
    [string[]]$Regions = @(),
    [string[]]$Cuts = @(),
    [double]$Ease = 0.0,
    [double]$Hem = 0.004,
    [switch]$Modeled,
    # texture: the template it belongs to, its size in pixels, and optionally a mesh of another
    # .blend file, <file.blend>=<Object>, whose look it starts from by projection.
    [string]$Garment,
    [ValidateSet(512, 1024, 2048)]
    [int]$Size = 1024,
    [string]$ProjectFrom,
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
$expectedCategory = @{ animations = "Animations"; paint = "Paint"; garment = "Garments"; cage = "Cages"; texture = "GarmentTextures" }[$Kind]
if ($null -ne $expectedCategory -and $category -cne $expectedCategory) {
    throw "A new $Kind asset goes below art/$expectedCategory, such as art/$expectedCategory/$name."
}

$directory = Join-Path $script:ArtRepositoryRoot $relative
if (Test-Path -LiteralPath $directory) {
    throw "$relative exists already; a new asset needs a folder of its own."
}

# A garment template and a garment texture both export the material M_Garment_<Name> and data
# Models/Garments/<Name>_<Label>.json, so no texture may share a template's name, nor the reverse.
$other = @{ garment = "GarmentTextures"; texture = "Garments" }[$Kind]
if ($null -ne $other -and (Test-Path -LiteralPath (Join-Path (Join-Path $script:ArtSourceRoot $other) $name))) {
    throw "art/$other/$name exists; a garment template and a garment texture need different names, such as Shirt and LinenShirt."
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
        $layers = @{ head = 400; body = 200; legs = 100; hands = 350; feet = 120; back = 500 }
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

        $cageManifest = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Cage)
        if ($cageManifest.Kind -ne "cage") {
            throw "$Cage is a $($cageManifest.Kind) asset; a garment template is made on a garment cage."
        }

        $lines[1] = '    "schemaVersion": 2,'
        $lines.Add("    `"cage`": `"$($cageManifest.RelativeDirectory)`",")
        $lines.Add("    `"slot`": `"$Slot`",")
        $lines.Add("    `"layer`": $Layer,")
        $lines.Add("    `"band`": `"$Band`",")
        if ($Modeled) {
            $recipe = "modeled"
        }
        else {
            $recipe = [ordered]@{
                regions = [string[]]$Regions
                cuts = @($Cuts | ForEach-Object {
                        $parts = $_.Split(":")
                        if ($parts.Count -ne 2) {
                            throw "Pass each cut as <Line>:head or <Line>:tail, such as HemHip:tail."
                        }

                        [ordered]@{ line = $parts[0]; keep = $parts[1] }
                    })
                ease = $Ease
                hem = $Hem
            }
        }

        if ($null -eq (ConvertTo-ArtGarmentRecipe ([pscustomobject]$recipe) $cageManifest) -and -not $Modeled) {
            throw "The recipe needs -Regions of the cage ($($script:ArtCageRegions -join ', ')), -Cuts at its lines in those regions, each <Line>:head or <Line>:tail, -Ease from 0 to $script:ArtGarmentEaseLimit, and -Hem from 0 to $script:ArtGarmentHemLimit."
        }

        Add-ArtRecipeLines $lines $recipe
        $lines.Add("    `"hides`": [$(Format-ArtList $Hides)],")
        $lines.Add("    `"hair`": $(if ($Hair) { '"' + $Hair + '"' } else { 'null' }),")
        $lines.Add("    `"cloth`": $(if ($Cloth) { '"' + $Cloth + '"' } else { 'null' }),")
        $lines.Add('    "dyes": 0,')
        $lines.Add("    `"triangleBudget`": $TriangleBudget,")
    }
    "texture" {
        $template = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Garment)
        if ($template.Kind -ne "garment") {
            throw "-Garment must name the garment template the texture belongs to, such as art/Garments/Jerkin."
        }

        $lines.Add("    `"garment`": `"$($template.RelativeDirectory)`",")
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
            @("SleeveShort", "arms", "UpperArm", 0.6),
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
                "--file", $manifest.SourcePath, "--garment", $manifest.Name, "--manifest", $manifest.ManifestPath,
                "--cage-manifest", $manifest.Cage.ManifestPath)
            $arguments += @($manifest.Bodies | ForEach-Object { @("--body", "$($_.SourcePath)=$($_.Root)") })
            $blender = Resolve-ArtBlender $BlenderPath
            $output = Invoke-ArtNative $blender $arguments
            if ($script:ArtNativeExitCode -ne 0 -or @($output | Where-Object { $_ -like "ART_GARMENT created *" }).Count -eq 0) {
                throw "Blender could not write $($manifest.SourcePath):`n$(($output | Select-Object -Last 20) -join "`n")"
            }

            foreach ($line in @($output | Where-Object { $_ -like "ART_REPORT *" })) {
                Write-Host $line.Substring("ART_REPORT ".Length)
            }
        }
        "texture" {
            $arguments = @(
                "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
                "--python", $script:ArtBlenderCli, "--", "texture-create",
                "--file", $manifest.SourcePath, "--texture", $manifest.Name, "--garment-file", $manifest.Garment.SourcePath,
                "--garment", $manifest.Garment.Name, "--label", $manifest.RootObjects[0], "--size", [string]$Size)
            if ($ProjectFrom) {
                $source = $ProjectFrom.Split("=")
                if ($source.Count -ne 2 -or -not (Test-Path -LiteralPath $source[0] -PathType Leaf)) {
                    throw "Pass -ProjectFrom as <file.blend>=<Object>."
                }

                $arguments += @("--source-file", (Resolve-Path -LiteralPath $source[0]).Path, "--source-object", $source[1])
            }

            $blender = Resolve-ArtBlender $BlenderPath
            $output = Invoke-ArtNative $blender $arguments
            if ($script:ArtNativeExitCode -ne 0 -or @($output | Where-Object { $_ -like "ART_TEXTURE_FILE created *" }).Count -eq 0) {
                throw "Blender could not write $($manifest.SourcePath):`n$(($output | Select-Object -Last 20) -join "`n")"
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
        Write-Host "Shape ${name}_$($manifest.RootObjects[0]) on the first body where it needs more than its recipe, run tools/Sync-GarmentAsset.ps1, then review, approve, and export it as docs/LOCAL_DEVELOPMENT.md describes under Make A Garment."
    }
    "texture" {
        Write-Host "Paint T_Garment_$name on Preview in Texture Paint mode, then review, approve, and export it as docs/LOCAL_DEVELOPMENT.md describes under Make A Garment."
    }
    "cage" {
        Write-Host "Run tools/Sync-CageAsset.ps1 for its review sheet and checks, then review and approve it as docs/LOCAL_DEVELOPMENT.md describes under Fit The Garment Cage."
    }
}
