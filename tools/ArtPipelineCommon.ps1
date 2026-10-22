# Shared helpers for the art pipeline scripts (Render-ArtPreview, Approve-ArtAsset,
# Export-ArtAsset, Verify-ArtAssets). The rules are described in docs/ART_PIPELINE.md.
# Dot-source this file; it works in Windows PowerShell 5.1 and PowerShell 7.

$script:ArtRepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script:ArtSourceRoot = Join-Path $script:ArtRepositoryRoot "art"
$script:ArtModelFolder = "src/Client/Assets/Art/Models"
# The images of painted materials, in the category folder of their model.
$script:ArtTextureFolder = "src/Client/Assets/Art/Textures"
$script:ArtSkeletonFolder = Join-Path $script:ArtSourceRoot "Skeletons"
$script:ArtBlenderVersion = "5.2"
$script:ArtBlenderCli = Join-Path $PSScriptRoot "blender/art_pipeline_cli.py"

# The review stages each asset kind passes in order. The last stage is the export gate.
# An animation library holds handmade clips of one skeleton, without meshes, so its only stage is
# the animation. A skinned asset without clips of its own, such as a body whose clips come from
# animation libraries, ends with its rig. A garment is reviewed on its bodies at rest, then in the
# test poses.
$script:ArtReviewStages = @{
    static = @("model")
    skinned = @("model", "rig", "animation")
    animations = @("animation")
    paint = @("paint")
    garment = @("model", "rig")
}

# The stages whose rules Blender can check, and so can be approved.
$script:ArtImplementedStages = @{
    static = @("model")
    skinned = @("model", "rig", "animation")
    animations = @("animation")
    paint = @("paint")
    garment = @("model", "rig")
}

# A paint layer exports only images, below this folder.
$script:ArtPaintTextureFolder = "$script:ArtTextureFolder/Paint"

# A garment exports one model per body below this folder, and its images below the other.
$script:ArtGarmentModelFolder = "$script:ArtModelFolder/Garments"
$script:ArtGarmentTextureFolder = "$script:ArtTextureFolder/Garments"
# The visual slots, bands, headwear hair forms, and cloth profiles of garments; see
# docs/EQUIPMENT_VISUALS_DESIGN.md.
$script:ArtGarmentSlots = @("head", "body", "legs", "hands", "feet", "back", "hair")
$script:ArtGarmentBands = @("tight", "mid", "outer", "loose")
$script:ArtGarmentHairForms = @("hide", "compress", "show")
$script:ArtGarmentCloth = @("robe", "cloak", "hood")

# Kinds with a skeleton and animation clips.
$script:ArtAnimatedKinds = @("skinned", "animations")

$script:ArtNamePattern = '^[A-Za-z][A-Za-z0-9_]*$'
$script:ArtShaPattern = '^[0-9a-f]{64}$'
$script:ArtDatePattern = '^\d{4}-\d{2}-\d{2}$'

function Get-ArtRelativePath([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $root = $script:ArtRepositoryRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "'$Path' is outside the repository."
    }

    return $full.Substring($root.Length).Replace('\', '/')
}

function Get-ArtAssetDirectories {
    if (-not (Test-Path -LiteralPath $script:ArtSourceRoot)) {
        return @()
    }

    return @(Get-ChildItem -LiteralPath $script:ArtSourceRoot -Recurse -Filter "asset.json" -File |
        Sort-Object FullName | ForEach-Object { $_.Directory.FullName })
}

function Resolve-ArtAssetDirectory([string]$Asset) {
    $candidates = @($Asset, (Join-Path $script:ArtRepositoryRoot $Asset), (Join-Path $script:ArtSourceRoot $Asset))
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $candidate = Split-Path -Parent $candidate
        }

        if (Test-Path -LiteralPath (Join-Path $candidate "asset.json") -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "No art asset found at '$Asset'. Pass the asset folder, for example art/WorldActors/Resource_IronVein."
}

function Get-ArtFileSha256([string]$Path) {
    # A Git LFS pointer names the SHA-256 of the real content, so CI checks hashes without
    # downloading the .blend files.
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -lt 1024) {
        $text = [System.IO.File]::ReadAllText($item.FullName)
        $match = [regex]::Match($text, '^version https://git-lfs\.github\.com/spec/v1\s+oid sha256:([0-9a-f]{64})\s')
        if ($match.Success) {
            return $match.Groups[1].Value
        }
    }

    return (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Format-ArtNumber([double]$Value) {
    return $Value.ToString("0.0#", [System.Globalization.CultureInfo]::InvariantCulture)
}

function Test-ArtNumber($Value) {
    return $Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal]
}

function ConvertTo-ArtMaterialRecords($Records) {
    # Returns the material records as one array, or $null when the list is empty or any entry
    # is missing, malformed, or a duplicate, so no invalid entry can drop out unnoticed. A
    # painted material names its texture in baseMap and has a white baseColor.
    $result = New-Object System.Collections.Generic.List[object]
    $names = New-Object System.Collections.Generic.HashSet[string]
    foreach ($record in @($Records)) {
        if ($null -eq $record -or
            [string]$record.name -cnotmatch '^M_[A-Za-z0-9_]+$' -or
            [string]$record.baseColor -cnotmatch '^#[0-9A-F]{6}$' -or
            -not $names.Add([string]$record.name)) {
            return $null
        }

        $baseMap = $null
        if ($null -ne $record.PSObject.Properties['baseMap']) {
            $baseMap = [string]$record.baseMap
            if ($baseMap -cnotmatch '^T_[A-Za-z0-9_]+$' -or [string]$record.baseColor -cne '#FFFFFF') {
                return $null
            }
        }

        $numbers = @()
        foreach ($field in @("metallic", "smoothness")) {
            $value = $record.$field
            if (-not (Test-ArtNumber $value) -or $value -lt 0 -or $value -gt 1) {
                return $null
            }

            $numbers += [double]$value
        }

        $material = [ordered]@{
            name = [string]$record.name
            baseColor = [string]$record.baseColor
        }
        if ($null -ne $baseMap) {
            $material.baseMap = $baseMap
        }

        $material.metallic = $numbers[0]
        $material.smoothness = $numbers[1]
        $result.Add($material)
    }

    if ($result.Count -eq 0) {
        return $null
    }

    return , $result.ToArray()
}

function Get-ArtTextureFolder([string]$Target) {
    # The textures of src/Client/Assets/Art/Models/<Category>/<Name>.fbx go to
    # src/Client/Assets/Art/Textures/<Category>.
    $category = Split-Path -Leaf (Split-Path -Parent $Target)
    return "$script:ArtTextureFolder/$category"
}

function ConvertTo-ArtTextureRecords($Records, [string]$Folder) {
    # Returns the texture records as one array, empty when there are none, or $null when any
    # entry is malformed or a duplicate. Each texture is the PNG <Folder>/<name>.png.
    $result = New-Object System.Collections.Generic.List[object]
    $names = New-Object System.Collections.Generic.HashSet[string]
    foreach ($record in @($Records | Where-Object { $null -ne $_ })) {
        $name = [string]$record.name
        if ($name -cnotmatch '^T_[A-Za-z0-9_]+$' -or [string]$record.target -cne "$Folder/$name.png" -or
            [string]$record.sha256 -cnotmatch $script:ArtShaPattern -or -not $names.Add($name)) {
            return $null
        }

        $result.Add([ordered]@{ name = $name; target = [string]$record.target; sha256 = [string]$record.sha256 })
    }

    return , $result.ToArray()
}

function ConvertTo-ArtClipRecords($Records, [string[]]$Clips) {
    # Returns the clip records as one array, or $null unless there is exactly one well-formed
    # record for each clip of the manifest.
    $result = New-Object System.Collections.Generic.List[object]
    $names = New-Object System.Collections.Generic.HashSet[string]
    foreach ($record in @($Records | Where-Object { $null -ne $_ })) {
        $name = [string]$record.name
        if ($Clips -cnotcontains $name -or -not ($record.frames -is [int] -or $record.frames -is [long]) -or
            $record.frames -le 0 -or -not ($record.loop -is [bool]) -or -not $names.Add($name)) {
            return $null
        }

        $result.Add([ordered]@{ name = $name; frames = [int]$record.frames; loop = [bool]$record.loop })
    }

    if ($result.Count -ne $Clips.Count) {
        return $null
    }

    return , $result.ToArray()
}

function ConvertTo-ArtVariantRecords($Records) {
    # Returns the variants of a skinned asset as one array, or $null when any entry is malformed
    # or names a material or image twice. Each names a painted material M_<Part>_<Label> and the
    # other images T_<Part>_<Label>_<Option> it may take in the game instead of its own.
    $result = New-Object System.Collections.Generic.List[object]
    $materials = New-Object System.Collections.Generic.HashSet[string]
    $images = New-Object System.Collections.Generic.HashSet[string]
    foreach ($record in @($Records | Where-Object { $null -ne $_ })) {
        $material = [string]$record.material
        $names = @($record.images | ForEach-Object { [string]$_ })
        if ($material -cnotmatch '^M_([A-Za-z0-9]+)_([A-Za-z0-9]+)$' -or -not $materials.Add($material) -or $names.Count -eq 0) {
            return $null
        }

        $prefix = "T_$($Matches[1])_$($Matches[2])_"
        foreach ($name in $names) {
            if (-not $name.StartsWith($prefix, [System.StringComparison]::Ordinal) -or
                $name.Substring($prefix.Length) -cnotmatch '^[A-Za-z0-9]+$' -or -not $images.Add($name)) {
                return $null
            }
        }

        $result.Add([ordered]@{ material = $material; images = [string[]]$names })
    }

    return , $result.ToArray()
}

function Read-ArtSkeleton([string]$Name) {
    # Returns the path of art/Skeletons/<Name>.json after checking its bones: unique names, one
    # root, every parent before its children, a deform flag on every bone, and cloth chain bones
    # that deform, of a known chain, outside the Humanoid avatar and the sockets.
    $path = Join-Path $script:ArtSkeletonFolder "$Name.json"
    $where = Get-ArtRelativePath $path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "${where}: the skeleton definition is missing."
    }

    try {
        $json = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    }
    catch {
        throw "${where}: invalid JSON. $($_.Exception.Message)"
    }

    $names = New-Object System.Collections.Generic.HashSet[string]
    $roots = 0
    foreach ($bone in @($json.bones)) {
        $boneName = [string]$bone.name
        if ($boneName -cnotmatch $script:ArtNamePattern -or -not ($bone.deform -is [bool])) {
            throw "${where}: every bone needs a name of letters, digits, and underscores and a deform flag."
        }

        if ($null -ne $bone.chain -and (@("skirt", "cloak", "hood") -cnotcontains [string]$bone.chain -or
                -not $bone.deform -or $null -ne $bone.human -or $null -ne $bone.socket)) {
            throw "${where}: bone '$boneName' must be a deforming bone of the chain skirt, cloak, or hood, outside the Humanoid avatar and the sockets."
        }

        if ($null -eq $bone.parent) {
            $roots++
        }
        elseif (-not $names.Contains([string]$bone.parent)) {
            throw "${where}: bone '$boneName' must follow its parent '$($bone.parent)'."
        }

        if (-not $names.Add($boneName)) {
            throw "${where}: bone '$boneName' is listed twice."
        }
    }

    if ($json.schemaVersion -ne 1 -or [string]$json.name -cne $Name -or $roots -ne 1) {
        throw "${where}: needs schemaVersion 1, the name '$Name', and exactly one root bone."
    }

    return $path
}

function Read-ArtRigReference([string]$Asset, [string]$Skeleton, [string]$Field) {
    # A skinned asset of the given skeleton that an animation library names: its repository path,
    # its .blend file, its armature, and its manifest path. Its approvals are checked on its own.
    $directory = Join-Path $script:ArtRepositoryRoot $Asset
    $manifestPath = Join-Path $directory "asset.json"
    if ($Asset -cnotmatch '^art/[A-Za-z][A-Za-z0-9_]*/[A-Za-z][A-Za-z0-9_]*$' -or
        -not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "$Field must name skinned assets as art/<Category>/<Name>; '$Asset' is none"
    }

    $json = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    $roots = @($json.rootObjects)
    if ([string]$json.kind -ne "skinned" -or [string]$json.skeleton -cne $Skeleton -or $roots.Count -ne 1) {
        throw "$Field '$Asset' must be a skinned asset of the $Skeleton skeleton with its armature as its one root object"
    }

    $name = Split-Path -Leaf $Asset
    return [pscustomobject]@{
        Asset = $Asset
        ManifestPath = $manifestPath
        SourcePath = Join-Path $directory "$name.blend"
        Root = [string]$roots[0]
    }
}

function Read-ArtPaintManifest([string]$AssetDirectory, $Json, [string]$Where, [switch]$AllowMissingSource) {
    # A paint layer: transparent paint over one painted material of its bodies, painted on the
    # first body and moved to the others, exported as one image per body.
    $name = Split-Path -Leaf $AssetDirectory
    $problems = New-Object System.Collections.Generic.List[string]
    if ($name -notmatch '^[A-Za-z][A-Za-z0-9]*$') {
        $problems.Add("the folder name '$name' of a paint layer must use only letters and digits")
    }

    if ($Json.schemaVersion -ne 1) {
        $problems.Add("schemaVersion must be 1")
    }

    foreach ($field in @("target", "rootObjects", "triangleBudget", "skeleton", "clips", "rig", "previews", "variants")) {
        if ($null -ne $Json.PSObject.Properties[$field]) {
            $problems.Add("a paint layer has no $field")
        }
    }

    $part = [string]$Json.part
    if ($part -cnotmatch '^[A-Za-z][A-Za-z0-9]*$') {
        $problems.Add("part must name the painted material M_<Part>_<Label>, such as Body")
    }

    if (-not ($Json.layer -is [int] -or $Json.layer -is [long]) -or $Json.layer -lt 0 -or $Json.layer -gt 1000) {
        $problems.Add("layer must be a whole number from 0 to 1000, the order paint layers are drawn in")
    }

    $bodies = @()
    try {
        $first = @($Json.bodies)[0]
        $skeleton = if ($null -ne $first) { [string](Get-Content -Raw -LiteralPath (Join-Path (Join-Path $script:ArtRepositoryRoot ([string]$first)) "asset.json") | ConvertFrom-Json).skeleton } else { "" }
        $bodies = @(@($Json.bodies | Where-Object { $null -ne $_ }) | ForEach-Object { Read-ArtRigReference ([string]$_) $skeleton "bodies" })
    }
    catch {
        $problems.Add($_.Exception.Message)
    }

    if ($bodies.Count -eq 0 -or @($bodies | ForEach-Object { $_.Asset } | Sort-Object -Unique).Count -ne $bodies.Count -or
        @($bodies | ForEach-Object { $_.Root } | Sort-Object -Unique).Count -ne $bodies.Count) {
        $problems.Add("bodies must name one or more skinned assets of one skeleton, each once, the painted body first")
    }

    $approvals = [ordered]@{}
    if ($null -ne $Json.approvals) {
        foreach ($property in $Json.approvals.PSObject.Properties) {
            $value = $property.Value
            if ($property.Name -ne "paint" -or [string]$value.date -notmatch $script:ArtDatePattern -or
                [string]$value.sourceSha256 -cnotmatch $script:ArtShaPattern) {
                $problems.Add("approvals of a paint layer hold the 'paint' stage with a date (yyyy-MM-dd) and a sourceSha256")
                continue
            }

            $approvals["paint"] = [ordered]@{ date = [string]$value.date; sourceSha256 = [string]$value.sourceSha256 }
        }
    }

    $export = $null
    if ($null -ne $Json.export) {
        $textures = ConvertTo-ArtTextureRecords $Json.export.textures $script:ArtPaintTextureFolder
        $expected = (@($bodies | ForEach-Object { "T_Paint_${name}_$($_.Root)" }) | Sort-Object) -join ","
        $smoothness = $Json.export.smoothness
        $metallic = $Json.export.metallic
        if ([string]$Json.export.sourceSha256 -cnotmatch $script:ArtShaPattern) {
            $problems.Add("export needs a sourceSha256")
        }
        elseif ($null -eq $textures -or ((@($textures | ForEach-Object { $_.name }) | Sort-Object) -join ",") -cne $expected) {
            $problems.Add("export.textures must list T_Paint_${name}_<Label> of every body once with its target ($script:ArtPaintTextureFolder/<name>.png) and sha256")
        }
        elseif (-not (Test-ArtNumber $smoothness) -or $smoothness -lt 0 -or $smoothness -gt 1 -or
            -not (Test-ArtNumber $metallic) -or $metallic -lt 0 -or $metallic -gt 1) {
            $problems.Add("export needs the smoothness and metallic of the paint, from 0 to 1")
        }
        else {
            $export = [ordered]@{
                sourceSha256 = [string]$Json.export.sourceSha256
                textures = $textures
                smoothness = [double]$smoothness
                metallic = [double]$metallic
            }
        }
    }

    $sourcePath = Join-Path $AssetDirectory "$name.blend"
    if (-not $AllowMissingSource -and -not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        $problems.Add("the source file $name.blend is missing next to asset.json")
    }

    if ($problems.Count -gt 0) {
        throw "${Where}: $($problems -join '; ')."
    }

    return [pscustomobject]@{
        Name = $name
        Directory = $AssetDirectory
        ManifestPath = Join-Path $AssetDirectory "asset.json"
        RelativeDirectory = Get-ArtRelativePath $AssetDirectory
        SourcePath = $sourcePath
        Kind = "paint"
        Stages = $script:ArtReviewStages["paint"]
        ImplementedStages = $script:ArtImplementedStages["paint"]
        Target = $null
        TargetPath = $null
        RootObjects = [string[]]@()
        TriangleBudget = $null
        Skeleton = $null
        SkeletonPath = $null
        Rig = $null
        Previews = @()
        Clips = [string[]]@()
        Variants = @()
        Part = $part
        Layer = [int]$Json.layer
        Bodies = $bodies
        Approvals = $approvals
        Export = $export
        ExportProblem = $null
    }
}

function Add-ArtMaterialLines($Lines, $Materials, [string]$Comma) {
    $Lines.Add('        "materials": [')
    for ($i = 0; $i -lt $Materials.Count; $i++) {
        $material = $Materials[$i]
        $separator = if ($i -lt $Materials.Count - 1) { "," } else { "" }
        $Lines.Add("            {")
        $Lines.Add("                `"name`": `"$($material.name)`",")
        $Lines.Add("                `"baseColor`": `"$($material.baseColor)`",")
        if ($material.Contains("baseMap")) {
            $Lines.Add("                `"baseMap`": `"$($material.baseMap)`",")
        }
        $Lines.Add("                `"metallic`": $(Format-ArtNumber $material.metallic),")
        $Lines.Add("                `"smoothness`": $(Format-ArtNumber $material.smoothness)")
        $Lines.Add("            }$separator")
    }
    $Lines.Add("        ]$Comma")
}

function Add-ArtTextureLines($Lines, $Textures, [string]$Comma) {
    $Lines.Add('        "textures": [')
    for ($i = 0; $i -lt $Textures.Count; $i++) {
        $texture = $Textures[$i]
        $separator = if ($i -lt $Textures.Count - 1) { "," } else { "" }
        $Lines.Add("            {")
        $Lines.Add("                `"name`": `"$($texture.name)`",")
        $Lines.Add("                `"target`": `"$($texture.target)`",")
        $Lines.Add("                `"sha256`": `"$($texture.sha256)`"")
        $Lines.Add("            }$separator")
    }
    $Lines.Add("        ]$Comma")
}

function Add-ArtApprovalLines($Lines, $Manifest) {
    $approvedStages = @($Manifest.Stages | Where-Object { $Manifest.Approvals.Contains($_) })
    if ($approvedStages.Count -eq 0) {
        $Lines.Add('    "approvals": {},')
        return
    }

    $Lines.Add('    "approvals": {')
    for ($i = 0; $i -lt $approvedStages.Count; $i++) {
        $approval = $Manifest.Approvals[$approvedStages[$i]]
        $comma = if ($i -lt $approvedStages.Count - 1) { "," } else { "" }
        $Lines.Add("        `"$($approvedStages[$i])`": {")
        $Lines.Add("            `"date`": `"$($approval.date)`",")
        $Lines.Add("            `"sourceSha256`": `"$($approval.sourceSha256)`"")
        $Lines.Add("        }$comma")
    }
    $Lines.Add("    },")
}

function Write-ArtPaintManifest($Manifest) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("{")
    $lines.Add('    "schemaVersion": 1,')
    $lines.Add('    "kind": "paint",')
    $lines.Add("    `"part`": `"$($Manifest.Part)`",")
    $bodies = (@($Manifest.Bodies) | ForEach-Object { '"' + $_.Asset + '"' }) -join ", "
    $lines.Add("    `"bodies`": [$bodies],")
    $lines.Add("    `"layer`": $($Manifest.Layer),")
    if ($Manifest.Approvals.Contains("paint")) {
        $lines.Add('    "approvals": {')
        $lines.Add('        "paint": {')
        $lines.Add("            `"date`": `"$($Manifest.Approvals.paint.date)`",")
        $lines.Add("            `"sourceSha256`": `"$($Manifest.Approvals.paint.sourceSha256)`"")
        $lines.Add('        }')
        $lines.Add('    },')
    }
    else {
        $lines.Add('    "approvals": {},')
    }

    if ($null -eq $Manifest.Export) {
        $lines.Add('    "export": null')
    }
    else {
        $lines.Add('    "export": {')
        $lines.Add("        `"sourceSha256`": `"$($Manifest.Export.sourceSha256)`",")
        Add-ArtTextureLines $lines @($Manifest.Export.textures) ","
        $lines.Add("        `"smoothness`": $(Format-ArtNumber $Manifest.Export.smoothness),")
        $lines.Add("        `"metallic`": $(Format-ArtNumber $Manifest.Export.metallic)")
        $lines.Add("    }")
    }

    $lines.Add("}")
    $text = ($lines -join "`n") + "`n"
    [System.IO.File]::WriteAllText($Manifest.ManifestPath, $text, (New-Object System.Text.UTF8Encoding($false)))
}

function Get-ArtGarmentModelTarget([string]$Name, [string]$Label) {
    return "$script:ArtGarmentModelFolder/${Name}_$Label.fbx"
}

function Get-ArtGarmentDataTarget([string]$Name, [string]$Label) {
    # The cover mask, underlay, and pull-in of a garment's model for one body.
    return "$script:ArtGarmentModelFolder/${Name}_$Label.json"
}

function Get-ArtClipLibraries([string]$Skeleton) {
    # The animation libraries of a skeleton, whose clips a garment is checked in: their repository
    # path, their .blend file, and their clips.
    return @(Get-ArtAssetDirectories | ForEach-Object {
            $json = Get-Content -Raw -LiteralPath (Join-Path $_ "asset.json") | ConvertFrom-Json
            if ([string]$json.kind -eq "animations" -and [string]$json.skeleton -ceq $Skeleton) {
                $name = Split-Path -Leaf $_
                [pscustomobject]@{
                    Asset = Get-ArtRelativePath $_
                    SourcePath = Join-Path $_ "$name.blend"
                    Clips = [string[]]@($json.clips | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
                    Export = $json.export
                }
            }
        })
}

function Get-ArtGarmentClipArguments($Manifest) {
    $arguments = @()
    foreach ($library in @(Get-ArtClipLibraries $Manifest.Skeleton)) {
        if ($library.Clips.Count -gt 0) {
            $arguments += @("--clip-library", "$($library.SourcePath)=$($library.Clips -join '+')")
        }
    }

    return $arguments
}

function Read-ArtGarmentManifest([string]$AssetDirectory, $Json, [string]$Where, [switch]$AllowMissingSource) {
    # A garment: a mesh made on the first body and fitted to the others, exported as one model per
    # body, skinned to that body's rig. See docs/EQUIPMENT_VISUALS_DESIGN.md.
    $name = Split-Path -Leaf $AssetDirectory
    $problems = New-Object System.Collections.Generic.List[string]
    if ($name -notmatch '^[A-Za-z][A-Za-z0-9]*$') {
        $problems.Add("the folder name '$name' of a garment must use only letters and digits")
    }

    if ($Json.schemaVersion -ne 1) {
        $problems.Add("schemaVersion must be 1")
    }

    foreach ($field in @("target", "rootObjects", "skeleton", "clips", "rig", "previews", "variants", "part")) {
        if ($null -ne $Json.PSObject.Properties[$field]) {
            $problems.Add("a garment has no $field")
        }
    }

    $slot = [string]$Json.slot
    if ($script:ArtGarmentSlots -cnotcontains $slot) {
        $problems.Add("slot must be one of: $($script:ArtGarmentSlots -join ', ')")
    }

    if (-not ($Json.layer -is [int] -or $Json.layer -is [long]) -or $Json.layer -lt 0 -or $Json.layer -gt 1000) {
        $problems.Add("layer must be a whole number from 0 to 1000, the order of garments from the inside out")
    }

    $band = [string]$Json.band
    if ($script:ArtGarmentBands -cnotcontains $band) {
        $problems.Add("band must be one of: $($script:ArtGarmentBands -join ', ')")
    }

    $hides = @($Json.hides | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
    if ($null -eq $Json.PSObject.Properties["hides"] -or
        @($hides | Where-Object { $script:ArtGarmentSlots -cnotcontains $_ -or $_ -ceq $slot }).Count -gt 0 -or
        @($hides | Sort-Object -Unique).Count -ne $hides.Count) {
        $problems.Add("hides must list other visual slots the garment hides, each once, or none")
    }

    # Only headwear says what happens to the hair under it.
    $hair = $Json.hair
    if ($slot -ceq "head" -and $script:ArtGarmentHairForms -cnotcontains [string]$hair) {
        $problems.Add("hair of headwear must be one of: $($script:ArtGarmentHairForms -join ', ')")
    }
    elseif ($slot -cne "head" -and $null -ne $hair) {
        $problems.Add("only headwear has hair; it is null for a $slot garment")
    }

    $cloth = $Json.cloth
    if ($null -ne $cloth -and $script:ArtGarmentCloth -cnotcontains [string]$cloth) {
        $problems.Add("cloth must be null or one of: $($script:ArtGarmentCloth -join ', ')")
    }

    if (-not ($Json.dyes -is [int] -or $Json.dyes -is [long]) -or $Json.dyes -ne 0) {
        $problems.Add("dyes must be 0; dye channels come with the garment shader")
    }

    $triangleBudget = $null
    if (-not ($Json.triangleBudget -is [int] -or $Json.triangleBudget -is [long]) -or $Json.triangleBudget -le 0) {
        $problems.Add("triangleBudget must be a positive whole number, the most triangles of one body's version")
    }
    else {
        $triangleBudget = [int]$Json.triangleBudget
    }

    $bodies = @()
    $skeleton = $null
    $skeletonPath = $null
    try {
        $first = @($Json.bodies)[0]
        $skeleton = if ($null -ne $first) { [string](Get-Content -Raw -LiteralPath (Join-Path (Join-Path $script:ArtRepositoryRoot ([string]$first)) "asset.json") | ConvertFrom-Json).skeleton } else { "" }
        $bodies = @(@($Json.bodies | Where-Object { $null -ne $_ }) | ForEach-Object { Read-ArtRigReference ([string]$_) $skeleton "bodies" })
        $skeletonPath = Read-ArtSkeleton $skeleton
    }
    catch {
        $problems.Add($_.Exception.Message)
    }

    if ($bodies.Count -eq 0 -or @($bodies | ForEach-Object { $_.Asset } | Sort-Object -Unique).Count -ne $bodies.Count -or
        @($bodies | ForEach-Object { $_.Root } | Sort-Object -Unique).Count -ne $bodies.Count) {
        $problems.Add("bodies must name one or more skinned assets of one skeleton, each once, the body the garment is made on first")
    }

    $stages = $script:ArtReviewStages["garment"]
    $approvals = [ordered]@{}
    if ($null -ne $Json.approvals) {
        foreach ($property in $Json.approvals.PSObject.Properties) {
            $value = $property.Value
            if ($stages -notcontains $property.Name -or [string]$value.date -notmatch $script:ArtDatePattern -or
                [string]$value.sourceSha256 -cnotmatch $script:ArtShaPattern) {
                $problems.Add("approvals of a garment hold its stages $($stages -join ' and ') with a date (yyyy-MM-dd) and a sourceSha256")
                continue
            }

            $approvals[$property.Name] = [ordered]@{ date = [string]$value.date; sourceSha256 = [string]$value.sourceSha256 }
        }
    }

    if (@($approvals.Keys | Where-Object { @($stages | Select-Object -First $approvals.Count) -notcontains $_ }).Count -gt 0) {
        $problems.Add("approvals must cover the first review stages in order: $($stages -join ', ')")
    }

    $models = @($bodies | ForEach-Object {
            $target = Get-ArtGarmentModelTarget $name $_.Root
            $data = Get-ArtGarmentDataTarget $name $_.Root
            [pscustomobject]@{
                Body = $_.Root
                BodyAsset = $_.Asset
                Target = $target
                TargetPath = Join-Path $script:ArtRepositoryRoot $target
                Data = $data
                DataPath = Join-Path $script:ArtRepositoryRoot $data
            }
        })

    $export = $null
    if ($null -ne $Json.export) {
        $materials = ConvertTo-ArtMaterialRecords $Json.export.materials
        $textures = ConvertTo-ArtTextureRecords $Json.export.textures $script:ArtGarmentTextureFolder
        $records = @($Json.export.models | Where-Object { $null -ne $_ })
        $modelsMatch = $records.Count -eq $models.Count
        for ($i = 0; $modelsMatch -and $i -lt $models.Count; $i++) {
            $modelsMatch = [string]$records[$i].body -ceq $models[$i].Body -and [string]$records[$i].target -ceq $models[$i].Target -and
                [string]$records[$i].sha256 -cmatch $script:ArtShaPattern -and [string]$records[$i].data -ceq $models[$i].Data -and
                [string]$records[$i].dataSha256 -cmatch $script:ArtShaPattern -and
                [string]$records[$i].bodySourceSha256 -cmatch $script:ArtShaPattern
        }

        $clipSources = @($Json.export.clipSources | Where-Object { $null -ne $_ })
        $clipSourcesMatch = @($clipSources | Where-Object {
                [string]$_.library -cnotmatch '^art/[A-Za-z][A-Za-z0-9_]*/[A-Za-z][A-Za-z0-9_]*$' -or
                [string]$_.sourceSha256 -cnotmatch $script:ArtShaPattern }).Count -eq 0 -and
            @($clipSources | ForEach-Object { [string]$_.library } | Sort-Object -Unique).Count -eq $clipSources.Count

        $baseMaps = (@($materials | Where-Object { $null -ne $_ -and $_.Contains("baseMap") } | ForEach-Object { $_.baseMap }) | Sort-Object) -join ","
        if ([string]$Json.export.sourceSha256 -cnotmatch $script:ArtShaPattern) {
            $problems.Add("export needs a sourceSha256")
        }
        elseif (-not $modelsMatch) {
            $problems.Add("export.models must list the model of every body in order with its body, the bodySourceSha256 of the body's export, its target ($script:ArtGarmentModelFolder/${name}_<Label>.fbx) and sha256, and its data ($script:ArtGarmentModelFolder/${name}_<Label>.json) and dataSha256")
        }
        elseif (-not $clipSourcesMatch) {
            $problems.Add("export.clipSources must list every animation library the garment was checked in once, with its library (art/<Category>/<Name>) and the sourceSha256 of its export")
        }
        elseif ($null -eq $materials -or @($materials | Where-Object { $_.name -cne "M_Garment_$name" }).Count -gt 0) {
            $problems.Add("export.materials must hold the one material M_Garment_$name with its baseColor (#RRGGBB), optional baseMap, metallic, and smoothness")
        }
        elseif ($null -eq $textures -or ((@($textures | ForEach-Object { $_.name }) | Sort-Object) -join ",") -cne $baseMaps) {
            $problems.Add("export.textures must hold the image its material names, with its target ($script:ArtGarmentTextureFolder/<name>.png) and sha256")
        }
        else {
            $export = [ordered]@{
                sourceSha256 = [string]$Json.export.sourceSha256
                models = @($records | ForEach-Object {
                        [ordered]@{
                            body = [string]$_.body
                            bodySourceSha256 = [string]$_.bodySourceSha256
                            target = [string]$_.target
                            sha256 = [string]$_.sha256
                            data = [string]$_.data
                            dataSha256 = [string]$_.dataSha256
                        }
                    })
                clipSources = @($clipSources | ForEach-Object { [ordered]@{ library = [string]$_.library; sourceSha256 = [string]$_.sourceSha256 } })
                materials = $materials
                textures = $textures
            }
        }
    }

    $sourcePath = Join-Path $AssetDirectory "$name.blend"
    if (-not $AllowMissingSource -and -not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        $problems.Add("the source file $name.blend is missing next to asset.json")
    }

    if ($problems.Count -gt 0) {
        throw "${Where}: $($problems -join '; ')."
    }

    return [pscustomobject]@{
        Name = $name
        Directory = $AssetDirectory
        ManifestPath = Join-Path $AssetDirectory "asset.json"
        RelativeDirectory = Get-ArtRelativePath $AssetDirectory
        SourcePath = $sourcePath
        Kind = "garment"
        Stages = $stages
        ImplementedStages = $script:ArtImplementedStages["garment"]
        Target = $null
        TargetPath = $null
        RootObjects = [string[]]@($bodies | ForEach-Object { $_.Root })
        TriangleBudget = $triangleBudget
        Skeleton = $skeleton
        SkeletonPath = $skeletonPath
        Rig = $null
        Previews = @()
        Clips = [string[]]@()
        Variants = @()
        Slot = $slot
        Layer = [int]$Json.layer
        Band = $band
        Bodies = $bodies
        Hides = [string[]]$hides
        Hair = if ($null -ne $hair) { [string]$hair } else { $null }
        Cloth = if ($null -ne $cloth) { [string]$cloth } else { $null }
        Dyes = [int]$Json.dyes
        Models = $models
        Approvals = $approvals
        Export = $export
        ExportProblem = $null
    }
}

function Write-ArtGarmentManifest($Manifest) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("{")
    $lines.Add('    "schemaVersion": 1,')
    $lines.Add('    "kind": "garment",')
    $lines.Add("    `"slot`": `"$($Manifest.Slot)`",")
    $lines.Add("    `"layer`": $($Manifest.Layer),")
    $lines.Add("    `"band`": `"$($Manifest.Band)`",")
    $bodies = (@($Manifest.Bodies) | ForEach-Object { '"' + $_.Asset + '"' }) -join ", "
    $lines.Add("    `"bodies`": [$bodies],")
    $hides = (@($Manifest.Hides) | ForEach-Object { '"' + $_ + '"' }) -join ", "
    $lines.Add("    `"hides`": [$hides],")
    $lines.Add("    `"hair`": $(if ($null -ne $Manifest.Hair) { '"' + $Manifest.Hair + '"' } else { 'null' }),")
    $lines.Add("    `"cloth`": $(if ($null -ne $Manifest.Cloth) { '"' + $Manifest.Cloth + '"' } else { 'null' }),")
    $lines.Add("    `"dyes`": $($Manifest.Dyes),")
    $lines.Add("    `"triangleBudget`": $($Manifest.TriangleBudget),")
    Add-ArtApprovalLines $lines $Manifest
    if ($null -eq $Manifest.Export) {
        $lines.Add('    "export": null')
    }
    else {
        $lines.Add('    "export": {')
        $lines.Add("        `"sourceSha256`": `"$($Manifest.Export.sourceSha256)`",")
        $lines.Add('        "models": [')
        $models = @($Manifest.Export.models)
        for ($i = 0; $i -lt $models.Count; $i++) {
            $comma = if ($i -lt $models.Count - 1) { "," } else { "" }
            $lines.Add("            {")
            $lines.Add("                `"body`": `"$($models[$i].body)`",")
            $lines.Add("                `"bodySourceSha256`": `"$($models[$i].bodySourceSha256)`",")
            $lines.Add("                `"target`": `"$($models[$i].target)`",")
            $lines.Add("                `"sha256`": `"$($models[$i].sha256)`",")
            $lines.Add("                `"data`": `"$($models[$i].data)`",")
            $lines.Add("                `"dataSha256`": `"$($models[$i].dataSha256)`"")
            $lines.Add("            }$comma")
        }
        $lines.Add('        ],')
        $lines.Add('        "clipSources": [')
        $sources = @($Manifest.Export.clipSources)
        for ($i = 0; $i -lt $sources.Count; $i++) {
            $comma = if ($i -lt $sources.Count - 1) { "," } else { "" }
            $lines.Add("            { `"library`": `"$($sources[$i].library)`", `"sourceSha256`": `"$($sources[$i].sourceSha256)`" }$comma")
        }
        $lines.Add('        ],')
        Add-ArtMaterialLines $lines @($Manifest.Export.materials) ","
        Add-ArtTextureLines $lines @($Manifest.Export.textures) ""
        $lines.Add("    }")
    }

    $lines.Add("}")
    $text = ($lines -join "`n") + "`n"
    [System.IO.File]::WriteAllText($Manifest.ManifestPath, $text, (New-Object System.Text.UTF8Encoding($false)))
}

function Read-ArtAssetManifest([string]$AssetDirectory, [switch]$AllowMissingSource) {
    # AllowMissingSource reads the manifest of a new animation library, paint layer, or garment
    # before its first file is written.
    $name = Split-Path -Leaf $AssetDirectory
    $manifestPath = Join-Path $AssetDirectory "asset.json"
    $where = Get-ArtRelativePath $manifestPath
    try {
        $json = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    }
    catch {
        throw "${where}: invalid JSON. $($_.Exception.Message)"
    }

    if ([string]$json.kind -eq "paint") {
        return Read-ArtPaintManifest $AssetDirectory $json $where -AllowMissingSource:$AllowMissingSource
    }

    if ([string]$json.kind -eq "garment") {
        return Read-ArtGarmentManifest $AssetDirectory $json $where -AllowMissingSource:$AllowMissingSource
    }

    $problems = New-Object System.Collections.Generic.List[string]
    if ($name -notmatch $script:ArtNamePattern) {
        $problems.Add("the folder name '$name' must use only letters, digits, and underscores")
    }

    if ($json.schemaVersion -ne 1) {
        $problems.Add("schemaVersion must be 1")
    }

    $stages = $script:ArtReviewStages[[string]$json.kind]
    $implementedStages = $script:ArtImplementedStages[[string]$json.kind]
    if ($null -eq $stages) {
        $problems.Add("kind must be one of: $(($script:ArtReviewStages.Keys | Sort-Object) -join ', ')")
    }

    $target = [string]$json.target
    if ($target -cnotmatch "^$([regex]::Escape($script:ArtModelFolder))/[A-Za-z][A-Za-z0-9_]*/$([regex]::Escape($name))\.fbx$") {
        $problems.Add("target must be $script:ArtModelFolder/<Category>/$name.fbx")
    }

    $roots = @($json.rootObjects)
    if ($roots.Count -eq 0 -or @($roots | Where-Object { [string]$_ -notmatch $script:ArtNamePattern }).Count -gt 0 -or
        @($roots | Sort-Object -Unique).Count -ne $roots.Count) {
        $problems.Add("rootObjects must list one or more unique object names")
    }

    $kind = [string]$json.kind
    $triangleBudget = $null
    if ($kind -eq "animations") {
        if ($null -ne $json.PSObject.Properties['triangleBudget']) {
            $problems.Add("an animation library exports no meshes and has no triangleBudget")
        }
    }
    elseif (-not ($json.triangleBudget -is [int] -or $json.triangleBudget -is [long]) -or $json.triangleBudget -le 0) {
        $problems.Add("triangleBudget must be a positive whole number")
    }
    else {
        $triangleBudget = [int]$json.triangleBudget
    }

    # A skinned asset and an animation library name their shared skeleton and the animation clips
    # the game plays on it.
    $skeleton = $null
    $skeletonPath = $null
    $clips = @()
    if ($script:ArtAnimatedKinds -contains $kind) {
        $skeleton = [string]$json.skeleton
        $clips = @($json.clips | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
        if ($skeleton -cnotmatch $script:ArtNamePattern) {
            $problems.Add("skeleton must name a skeleton definition in art/Skeletons")
        }
        else {
            try {
                $skeletonPath = Read-ArtSkeleton $skeleton
            }
            catch {
                $problems.Add($_.Exception.Message)
            }
        }

        if (@($clips | Where-Object { $_ -cnotmatch $script:ArtNamePattern }).Count -gt 0 -or
            @($clips | Sort-Object -Unique).Count -ne $clips.Count) {
            $problems.Add("clips must list unique animation clip names")
        }
        elseif ($kind -eq "skinned" -and $clips.Count -eq 0 -and $null -ne $json.PSObject.Properties['clips']) {
            $problems.Add("a skinned asset without clips leaves clips out")
        }
    }
    elseif ($null -ne $json.skeleton -or $null -ne $json.clips) {
        $problems.Add("only a skinned asset or an animation library has a skeleton and clips")
    }

    if ($kind -eq "skinned" -and $clips.Count -eq 0) {
        $stages = @($stages | Where-Object { $_ -ne "animation" })
        $implementedStages = @($implementedStages | Where-Object { $_ -ne "animation" })
    }

    # A skinned asset may name other images that its painted materials take in the game, such as
    # the face and eye variants of a character.
    $variants = @()
    if ($null -ne $json.variants) {
        $variants = ConvertTo-ArtVariantRecords $json.variants
        if ($kind -ne "skinned") {
            $problems.Add("only a skinned asset has variants")
        }
        elseif ($null -eq $variants) {
            $problems.Add("variants must name each painted material M_<Part>_<Label> once with its other images T_<Part>_<Label>_<Option>, each once")
            $variants = @()
        }
    }

    # An animation library is authored on the rig of one skinned asset of its skeleton and shows
    # its clips on the bodies of others.
    $rig = $null
    $previews = @()
    if ($kind -eq "animations") {
        try {
            $rig = Read-ArtRigReference ([string]$json.rig) $skeleton "rig"
            $previews = @(@($json.previews | Where-Object { $null -ne $_ }) |
                ForEach-Object { Read-ArtRigReference ([string]$_) $skeleton "previews" })
        }
        catch {
            $problems.Add($_.Exception.Message)
        }

        if (@($previews | ForEach-Object { $_.Asset } | Sort-Object -Unique).Count -ne $previews.Count -or
            @($previews | Where-Object { $null -ne $rig -and $_.Asset -eq $rig.Asset }).Count -gt 0) {
            $problems.Add("previews must name other skinned assets than the rig, each once")
        }
    }
    elseif ($null -ne $json.rig -or $null -ne $json.previews) {
        $problems.Add("only an animation library has a rig and previews")
    }

    $approvals = [ordered]@{}
    if ($null -ne $json.approvals) {
        foreach ($property in $json.approvals.PSObject.Properties) {
            if ($null -ne $stages -and $stages -notcontains $property.Name) {
                $problems.Add("approvals.$($property.Name) is not a review stage of a $($json.kind) asset")
                continue
            }

            $value = $property.Value
            if ([string]$value.date -notmatch $script:ArtDatePattern -or [string]$value.sourceSha256 -cnotmatch $script:ArtShaPattern) {
                $problems.Add("approvals.$($property.Name) needs a date (yyyy-MM-dd) and a sourceSha256")
                continue
            }

            $approvals[$property.Name] = [ordered]@{ date = [string]$value.date; sourceSha256 = [string]$value.sourceSha256 }
        }
    }

    # Stages are approved in order, so the approvals are always the first stages of the kind.
    if ($null -ne $stages) {
        $approvedPrefix = @($stages | Select-Object -First $approvals.Count)
        if (@($approvals.Keys | Where-Object { $approvedPrefix -notcontains $_ }).Count -gt 0) {
            $problems.Add("approvals must cover the first review stages in order: $($stages -join ', ')")
        }
    }

    $export = $null
    $exportProblem = $null
    if ($null -ne $json.export -and $kind -eq "animations") {
        $clipRecords = ConvertTo-ArtClipRecords $json.export.clips $clips
        if ([string]$json.export.sourceSha256 -cnotmatch $script:ArtShaPattern -or
            [string]$json.export.targetSha256 -cnotmatch $script:ArtShaPattern -or
            [string]$json.export.rigSourceSha256 -cnotmatch $script:ArtShaPattern) {
            $problems.Add("export needs a sourceSha256, a targetSha256, and the rigSourceSha256 of the rig's export")
        }
        elseif ($null -ne $json.export.materials -or $null -ne $json.export.textures) {
            $problems.Add("the export of an animation library has no materials or textures")
        }
        elseif ($null -eq $clipRecords) {
            $problems.Add("export.clips must list every clip of the manifest once with its name, frames, and loop")
        }
        else {
            $export = [ordered]@{
                sourceSha256 = [string]$json.export.sourceSha256
                targetSha256 = [string]$json.export.targetSha256
                rigSourceSha256 = [string]$json.export.rigSourceSha256
                materials = @()
                textures = @()
                clips = $clipRecords
            }
        }
    }
    elseif ($null -ne $json.export) {
        $materials = ConvertTo-ArtMaterialRecords $json.export.materials
        $textureFolder = Get-ArtTextureFolder $target
        $textures = ConvertTo-ArtTextureRecords $json.export.textures $textureFolder
        $clipRecords = $null
        if ([string]$json.kind -eq "skinned") {
            $clipRecords = ConvertTo-ArtClipRecords $json.export.clips $clips
        }

        $baseMaps = (@(@($materials | Where-Object { $_.Contains("baseMap") } | ForEach-Object { $_.baseMap }) +
                @($variants | ForEach-Object { $_.images })) | Sort-Object -Unique) -join ","
        $variantProblem = $null
        foreach ($variant in $variants) {
            $owner = @($materials | Where-Object { $_.name -ceq $variant.material -and $_.Contains("baseMap") })
            $prefix = $variant.material -replace '^M_', 'T_'
            if ($owner.Count -ne 1 -or -not ([string]$owner[0].baseMap).StartsWith("${prefix}_", [System.StringComparison]::Ordinal)) {
                $variantProblem = "the variants of $($variant.material) need an exported painted material whose image is named ${prefix}_<Option>"
            }
        }
        $textureNames = (@($textures | ForEach-Object { $_.name }) | Sort-Object) -join ","

        if ([string]$json.export.sourceSha256 -cnotmatch $script:ArtShaPattern -or
            [string]$json.export.targetSha256 -cnotmatch $script:ArtShaPattern) {
            $problems.Add("export needs a sourceSha256 and a targetSha256")
        }
        elseif ($null -eq $materials) {
            $problems.Add("export.materials must list every exported material once with its name, baseColor (#RRGGBB), optional baseMap, metallic, and smoothness")
        }
        elseif ($null -eq $textures) {
            $problems.Add("export.textures must list every texture once with its name, target ($textureFolder/<name>.png), and sha256")
        }
        elseif ([string]$json.kind -eq "skinned" -and $null -eq $clipRecords) {
            $problems.Add("export.clips must list every clip of the manifest once with its name, frames, and loop")
        }
        elseif ([string]$json.kind -ne "skinned" -and $null -ne $json.export.clips) {
            $problems.Add("only the export of a skinned asset has clips")
        }
        else {
            $export = [ordered]@{
                sourceSha256 = [string]$json.export.sourceSha256
                targetSha256 = [string]$json.export.targetSha256
                materials = $materials
                textures = $textures
                clips = $clipRecords
            }

            # A record that no longer fits the manifest, such as after a change of its variants,
            # is stale: the next export replaces it, and the verification reports it until then.
            if ($baseMaps -cne $textureNames) {
                $exportProblem = "export.textures must hold exactly the textures that the materials name in baseMap and the variants; export the asset again"
            }
            elseif ($null -ne $variantProblem) {
                $exportProblem = $variantProblem
            }
        }
    }

    $sourcePath = Join-Path $AssetDirectory "$name.blend"
    if (-not $AllowMissingSource -and -not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        $problems.Add("the source file $name.blend is missing next to asset.json")
    }

    if ($problems.Count -gt 0) {
        throw "${where}: $($problems -join '; ')."
    }

    return [pscustomobject]@{
        Name = $name
        Directory = $AssetDirectory
        ManifestPath = $manifestPath
        RelativeDirectory = Get-ArtRelativePath $AssetDirectory
        SourcePath = $sourcePath
        Kind = [string]$json.kind
        Stages = $stages
        ImplementedStages = $implementedStages
        Target = $target
        TargetPath = Join-Path $script:ArtRepositoryRoot $target
        RootObjects = [string[]]$roots
        TriangleBudget = $triangleBudget
        Skeleton = $skeleton
        SkeletonPath = $skeletonPath
        Rig = $rig
        Previews = $previews
        Clips = [string[]]$clips
        Variants = $variants
        Approvals = $approvals
        Export = $export
        ExportProblem = $exportProblem
    }
}

function Add-ArtClipLines($Lines, $ClipRecords) {
    $Lines.Add('        "clips": [')
    for ($i = 0; $i -lt $ClipRecords.Count; $i++) {
        $clip = $ClipRecords[$i]
        $comma = if ($i -lt $ClipRecords.Count - 1) { "," } else { "" }
        $loop = if ($clip.loop) { "true" } else { "false" }
        $Lines.Add("            { `"name`": `"$($clip.name)`", `"frames`": $($clip.frames), `"loop`": $loop }$comma")
    }
    $Lines.Add("        ]")
}

function Write-ArtAssetManifest($Manifest) {
    # Written by hand so Windows PowerShell 5.1 and PowerShell 7 produce identical files.
    if ($Manifest.Kind -eq "paint") {
        Write-ArtPaintManifest $Manifest
        return
    }

    if ($Manifest.Kind -eq "garment") {
        Write-ArtGarmentManifest $Manifest
        return
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("{")
    $lines.Add('    "schemaVersion": 1,')
    $lines.Add("    `"kind`": `"$($Manifest.Kind)`",")
    $lines.Add("    `"target`": `"$($Manifest.Target)`",")
    $lines.Add('    "rootObjects": [')
    for ($i = 0; $i -lt $Manifest.RootObjects.Count; $i++) {
        $comma = if ($i -lt $Manifest.RootObjects.Count - 1) { "," } else { "" }
        $lines.Add("        `"$($Manifest.RootObjects[$i])`"$comma")
    }
    $lines.Add("    ],")
    if ($null -ne $Manifest.TriangleBudget) {
        $lines.Add("    `"triangleBudget`": $($Manifest.TriangleBudget),")
    }

    if ($script:ArtAnimatedKinds -contains $Manifest.Kind) {
        $lines.Add("    `"skeleton`": `"$($Manifest.Skeleton)`",")
    }

    if ($Manifest.Kind -eq "animations") {
        $lines.Add("    `"rig`": `"$($Manifest.Rig.Asset)`",")
        $previews = (@($Manifest.Previews) | ForEach-Object { '"' + $_.Asset + '"' }) -join ", "
        $lines.Add("    `"previews`": [$previews],")
    }

    if ($script:ArtAnimatedKinds -contains $Manifest.Kind -and @($Manifest.Clips).Count -gt 0) {
        $clips = (@($Manifest.Clips) | ForEach-Object { '"' + $_ + '"' }) -join ", "
        $lines.Add("    `"clips`": [$clips],")
    }

    $variants = @($Manifest.Variants)
    if ($variants.Count -gt 0) {
        $lines.Add('    "variants": [')
        for ($i = 0; $i -lt $variants.Count; $i++) {
            $comma = if ($i -lt $variants.Count - 1) { "," } else { "" }
            $images = (@($variants[$i].images) | ForEach-Object { '"' + $_ + '"' }) -join ", "
            $lines.Add("        { `"material`": `"$($variants[$i].material)`", `"images`": [$images] }$comma")
        }
        $lines.Add("    ],")
    }
    Add-ArtApprovalLines $lines $Manifest

    if ($null -eq $Manifest.Export) {
        $lines.Add('    "export": null')
    }
    elseif ($Manifest.Kind -eq "animations") {
        $lines.Add('    "export": {')
        $lines.Add("        `"sourceSha256`": `"$($Manifest.Export.sourceSha256)`",")
        $lines.Add("        `"targetSha256`": `"$($Manifest.Export.targetSha256)`",")
        $lines.Add("        `"rigSourceSha256`": `"$($Manifest.Export.rigSourceSha256)`",")
        Add-ArtClipLines $lines @($Manifest.Export.clips)
        $lines.Add("    }")
    }
    else {
        $lines.Add('    "export": {')
        $lines.Add("        `"sourceSha256`": `"$($Manifest.Export.sourceSha256)`",")
        $lines.Add("        `"targetSha256`": `"$($Manifest.Export.targetSha256)`",")
        $textures = @($Manifest.Export.textures)
        $clipRecords = @($Manifest.Export.clips | Where-Object { $null -ne $_ })
        # Only painted materials have textures and only skinned assets have clips, so the
        # records of other assets stay as they are.
        $more = $textures.Count -gt 0 -or $clipRecords.Count -gt 0
        Add-ArtMaterialLines $lines @($Manifest.Export.materials) $(if ($more) { "," } else { "" })
        if ($textures.Count -gt 0) {
            Add-ArtTextureLines $lines $textures $(if ($clipRecords.Count -gt 0) { "," } else { "" })
        }

        if ($clipRecords.Count -gt 0) {
            Add-ArtClipLines $lines $clipRecords
        }
        $lines.Add("    }")
    }

    $lines.Add("}")
    $text = ($lines -join "`n") + "`n"
    [System.IO.File]::WriteAllText($Manifest.ManifestPath, $text, (New-Object System.Text.UTF8Encoding($false)))
}

function Get-ArtExportGateProblem($Manifest, [string]$SourceSha256) {
    # Returns $null when every review stage is approved and the last approval covers the
    # current source file, otherwise the reason the asset may not be exported.
    foreach ($stage in $Manifest.Stages) {
        if (-not $Manifest.Approvals.Contains($stage)) {
            return "the '$stage' review stage is not approved"
        }
    }

    $finalStage = $Manifest.Stages[-1]
    if ($Manifest.Approvals[$finalStage].sourceSha256 -ne $SourceSha256) {
        return "$($Manifest.Name).blend changed after the '$finalStage' approval"
    }

    return $null
}

function Test-ArtAssetInReview($Manifest) {
    # An asset is in review while a later stage still waits, such as a skinned model before
    # its rig. It has no export yet.
    return @($Manifest.Stages | Where-Object { -not $Manifest.Approvals.Contains($_) }).Count -gt 0
}

function Get-ArtReviewProblem($Manifest, [string]$SourceSha256) {
    # Returns $null when an asset in review has its latest approval on the current source file,
    # so the repository only holds approved states, otherwise the reason it does not.
    $approved = @($Manifest.Stages | Where-Object { $Manifest.Approvals.Contains($_) })
    if ($approved.Count -eq 0) {
        return "no review stage is approved; approve the '$($Manifest.Stages[0])' stage with tools/Approve-ArtAsset.ps1"
    }

    $latest = $approved[-1]
    if ($Manifest.Approvals[$latest].sourceSha256 -ne $SourceSha256) {
        return "$($Manifest.Name).blend changed after the '$latest' approval"
    }

    return $null
}

function Resolve-ArtBlender([string]$BlenderPath) {
    if ([string]::IsNullOrWhiteSpace($BlenderPath)) {
        $BlenderPath = $env:BLENDER_PATH
    }

    if ([string]::IsNullOrWhiteSpace($BlenderPath)) {
        $command = Get-Command blender -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            $BlenderPath = $command.Source
        }
        elseif ($IsWindows -or $env:OS -eq "Windows_NT") {
            $BlenderPath = "C:\Program Files\Blender Foundation\Blender $script:ArtBlenderVersion\blender.exe"
        }
    }

    if ([string]::IsNullOrWhiteSpace($BlenderPath) -or -not (Test-Path -LiteralPath $BlenderPath -PathType Leaf)) {
        throw "Blender $script:ArtBlenderVersion was not found. Set BLENDER_PATH to blender.exe."
    }

    $version = @(Invoke-ArtNative $BlenderPath @("--factory-startup", "--version")) |
        Where-Object { $_ -like "Blender *" } | Select-Object -First 1
    if ([string]$version -notmatch "^Blender $([regex]::Escape($script:ArtBlenderVersion))\.\d+") {
        throw "The art pipeline needs Blender $script:ArtBlenderVersion; '$BlenderPath' reports '$version'."
    }

    return $BlenderPath
}

function Invoke-ArtNative([string]$Executable, [string[]]$Arguments) {
    # Returns stdout and stderr as text lines. Windows PowerShell 5.1 turns a native stderr line
    # into a terminating error under $ErrorActionPreference = "Stop", and Blender logs to stderr.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& $Executable @Arguments 2>&1 | ForEach-Object { [string]$_ })
        $script:ArtNativeExitCode = $LASTEXITCODE
        return $output
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Invoke-ArtBlender([string]$Blender, $Manifest, [string[]]$Arguments) {
    # Runs one art_pipeline_cli.py command and returns its ART_ result lines. Blender's Python
    # sees one variable of this environment, PYTHONHASHSEED: fixed string hashes make the FBX
    # export byte for byte reproducible. Every other Python variable is cleared, so no Python
    # setup of the machine reaches the pipeline.
    $environment = [ordered]@{
        PYTHONHASHSEED = "0"
        PYTHONNOUSERSITE = "1"
        PYTHONPATH = $null
        PYTHONHOME = $null
        PYTHONSTARTUP = $null
        PYTHONUSERBASE = $null
    }
    $saved = @{}
    foreach ($name in $environment.Keys) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
        [Environment]::SetEnvironmentVariable($name, $environment[$name], "Process")
    }

    try {
        $output = Invoke-ArtNative $Blender (@(
                "--background", "--factory-startup", "--python-use-system-env", $Manifest.SourcePath,
                "--python-exit-code", "1", "--python", $script:ArtBlenderCli, "--") + $Arguments)
    }
    finally {
        foreach ($name in $environment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $saved[$name], "Process")
        }
    }

    $exitCode = $script:ArtNativeExitCode
    $results = @($output | Where-Object { $_ -like "ART_*" })
    $errors = @($results | Where-Object { $_ -like "ART_PIPELINE_ERROR *" } |
        ForEach-Object { $_.Substring("ART_PIPELINE_ERROR ".Length) })
    if ($errors.Count -gt 0) {
        throw "$($Manifest.Name) does not meet the art pipeline rules:`n - $($errors -join "`n - ")"
    }

    if ($exitCode -ne 0) {
        throw "Blender failed with exit code $exitCode for $($Manifest.Name):`n$(($output | Select-Object -Last 30) -join "`n")"
    }

    return $results
}

function Get-ArtValidationArguments($Manifest, [string]$Stage) {
    # The rules of the given review stage and of every earlier one apply.
    if ($Manifest.Kind -eq "paint") {
        return @("--kind", "paint", "--roots", (@($Manifest.Bodies | ForEach-Object { $_.Root }) -join ","),
            "--stage", $Stage, "--paint", $Manifest.Name, "--part", $Manifest.Part)
    }

    # A garment is checked against the current files of its bodies.
    if ($Manifest.Kind -eq "garment") {
        $arguments = @("--kind", "garment", "--roots", ($Manifest.RootObjects -join ","), "--stage", $Stage,
            "--triangle-budget", [string]$Manifest.TriangleBudget, "--skeleton", $Manifest.SkeletonPath,
            "--garment", $Manifest.Name, "--band", $Manifest.Band)
        foreach ($body in @($Manifest.Bodies)) {
            $arguments += @("--body", "$($body.SourcePath)=$($body.Root)")
        }

        return $arguments + (Get-ArtGarmentClipArguments $Manifest)
    }

    $arguments = @(
        "--kind", $Manifest.Kind,
        "--roots", ($Manifest.RootObjects -join ","),
        "--stage", $Stage)
    if ($null -ne $Manifest.TriangleBudget) {
        $arguments += @("--triangle-budget", [string]$Manifest.TriangleBudget)
    }

    if ($script:ArtAnimatedKinds -contains $Manifest.Kind) {
        $arguments += @("--skeleton", $Manifest.SkeletonPath)
        # Windows PowerShell drops an empty argument, so a body without clips passes none.
        if (@($Manifest.Clips).Count -gt 0) {
            $arguments += @("--clips", ($Manifest.Clips -join ","))
        }
    }

    if ($Manifest.Kind -eq "animations") {
        $arguments += @("--rig-source", $Manifest.Rig.SourcePath, "--rig-root", $Manifest.Rig.Root)
    }

    $variants = @($Manifest.Variants)
    if ($variants.Count -gt 0) {
        $arguments += @("--variants", ((@($variants | ForEach-Object { "$($_.material)=$(@($_.images) -join '+')" })) -join ";"))
    }

    return $arguments
}
