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
# test poses. The garment cage is reviewed once, on its bodies, and has no export. A garment
# texture is reviewed on its template.
$script:ArtReviewStages = @{
    static = @("model")
    skinned = @("model", "rig", "animation")
    animations = @("animation")
    paint = @("paint")
    garment = @("model", "rig")
    cage = @("model")
    texture = @("texture")
}

# The stages whose rules Blender can check, and so can be approved.
$script:ArtImplementedStages = @{
    static = @("model")
    skinned = @("model", "rig", "animation")
    animations = @("animation")
    paint = @("paint")
    garment = @("model", "rig")
    cage = @("model")
    texture = @("texture")
}

# The regions of the garment cage and the range of its figure scale in meters; see
# docs/EQUIPMENT_VISUALS_DESIGN.md.
$script:ArtCageRegions = @("neck", "trunk", "arms", "legs", "hands", "feet", "head")
$script:ArtCageSidedRegions = @("arms", "legs", "hands", "feet")
$script:ArtCageFigureRange = @(0.01, 0.2)

# A paint layer exports only images, below this folder.
$script:ArtPaintTextureFolder = "$script:ArtTextureFolder/Paint"
# A paint image is cropped to start at whole blocks of this many texels, as
# tools/blender/art_pipeline/paint.py does.
$script:ArtPaintCropBlock = 4

# A garment exports one model per body below this folder, and its images below the other.
$script:ArtGarmentModelFolder = "$script:ArtModelFolder/Garments"
$script:ArtGarmentTextureFolder = "$script:ArtTextureFolder/Garments"
# The visual slots, bands, headwear hair forms, and cloth profiles of garments; see
# docs/EQUIPMENT_VISUALS_DESIGN.md.
$script:ArtGarmentSlots = @("head", "body", "legs", "hands", "feet", "back", "hair")
# The categories of paint layers with the layers each is drawn at, so a scar lies under a tattoo
# and both under the underwear, and the visual slots underwear is worn in.
$script:ArtPaintCategories = [ordered]@{ scar = @(100, 199); tattoo = @(200, 299); underwear = @(900, 999) }
$script:ArtUnderwearSlots = @("body", "legs")
$script:ArtGarmentBands = @("tight", "mid", "outer", "loose", "hair")
$script:ArtGarmentHairForms = @("hide", "compress", "show")
$script:ArtGarmentCloth = @("robe", "cloak", "hood")
# A loose part's side of the edge it hangs from, the chains it hangs on with the cloth profile they
# take, its most flare in meters, and the most top and width of a part across the back, as shares
# of how far the shoulder joints lie from the middle.
$script:ArtGarmentHangSides = @("all", "back")
$script:ArtGarmentHangChains = @("skirt", "cloak")
$script:ArtGarmentHangCloth = @{ skirt = "robe"; cloak = "cloak" }
$script:ArtGarmentFlareLimit = 0.1
$script:ArtGarmentHangShareLimit = 1.5
# A template's recipe: the side of a line it keeps, and the most ease and edge depth in meters.
$script:ArtGarmentKeeps = @("head", "tail")
$script:ArtGarmentEaseLimit = 0.03
$script:ArtGarmentHemLimit = 0.01
$script:ArtTextureSizes = @(512, 1024, 2048)

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
    # painted material names its texture in baseMap and has a white baseColor. A dyeable garment
    # texture's material names its dye mask in dyeMap and the natural color of each of its one to
    # three channels in dyeColors.
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

        $dyeMap = $null
        $dyeColors = @()
        if ($null -ne $record.PSObject.Properties['dyeMap']) {
            $dyeMap = [string]$record.dyeMap
            $dyeColors = @($record.dyeColors | ForEach-Object { [string]$_ })
            if ($null -eq $baseMap -or $dyeMap -cne "${baseMap}_Dye" -or $dyeColors.Count -lt 1 -or $dyeColors.Count -gt 3 -or
                @($dyeColors | Where-Object { $_ -cnotmatch '^#[0-9A-F]{6}$' }).Count -gt 0) {
                return $null
            }
        }
        elseif ($null -ne $record.PSObject.Properties['dyeColors']) {
            return $null
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

        if ($null -ne $dyeMap) {
            $material.dyeMap = $dyeMap
            $material.dyeColors = [string[]]$dyeColors
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

function ConvertTo-ArtPaintTextureRecords($Records, [string]$Folder) {
    # Returns the image records of a paint layer as one array, or $null when any entry is malformed
    # or a duplicate. Each is a texture record with the image's full paint layout of its body, width
    # and height, its crop, x, y, width, and height inside that layout, starting at whole blocks, how
    # it was moved, through the shared "topology" or through the "cage", and the bodyPaintSha256 of
    # the body's export it was moved to.
    $plain = ConvertTo-ArtTextureRecords $Records $Folder
    if ($null -eq $plain) {
        return $null
    }

    $result = New-Object System.Collections.Generic.List[object]
    $records = @($Records | Where-Object { $null -ne $_ })
    for ($i = 0; $i -lt $records.Count; $i++) {
        $record = $records[$i]
        $layout = @($record.layout | ForEach-Object { $_ })
        $rect = @($record.rect | ForEach-Object { $_ })
        $whole = @(@($layout + $rect) | Where-Object { -not ($_ -is [int] -or $_ -is [long]) }).Count -eq 0
        if (-not $whole -or $layout.Count -ne 2 -or $rect.Count -ne 4 -or $layout[0] -le 0 -or $layout[1] -le 0 -or
            $rect[0] -lt 0 -or $rect[1] -lt 0 -or $rect[2] -le 0 -or $rect[3] -le 0 -or
            $rect[0] + $rect[2] -gt $layout[0] -or $rect[1] + $rect[3] -gt $layout[1] -or
            $rect[0] % $script:ArtPaintCropBlock -ne 0 -or $rect[1] % $script:ArtPaintCropBlock -ne 0 -or
            @("topology", "cage") -cnotcontains [string]$record.move -or
            [string]$record.bodyPaintSha256 -cnotmatch $script:ArtShaPattern) {
            return $null
        }

        $entry = $plain[$i]
        $entry.layout = [int[]]$layout
        $entry.rect = [int[]]$rect
        $entry.move = [string]$record.move
        $entry.bodyPaintSha256 = [string]$record.bodyPaintSha256
        $result.Add($entry)
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

function Get-ArtSocket([string]$Socket) {
    # The skeleton and bone of a socket that its bone names, such as main_hand on Socket_MainHand of
    # the Humanoid skeleton, or $null when no skeleton definition has it.
    foreach ($file in @(Get-ChildItem -LiteralPath $script:ArtSkeletonFolder -Filter "*.json" -File | Sort-Object Name)) {
        $json = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json
        foreach ($bone in @($json.bones)) {
            if ($null -ne $bone.socket -and [string]$bone.socket -ceq $Socket -and -not $bone.deform) {
                return [pscustomobject]@{ Skeleton = [string]$json.name; Bone = [string]$bone.name }
            }
        }
    }

    return $null
}

function Get-ArtHeldLibrary([string]$Skeleton) {
    # The animation library of a skeleton whose bodies hold an attachment in its review: the first
    # with the clip Idle, with its armature, or $null without one.
    foreach ($library in @(Get-ArtClipLibraries $Skeleton | Sort-Object Asset)) {
        if ($library.Clips -ccontains "Idle") {
            $json = Get-Content -Raw -LiteralPath (Join-Path $script:ArtRepositoryRoot "$($library.Asset)/asset.json") | ConvertFrom-Json
            return [pscustomobject]@{ SourcePath = $library.SourcePath; Root = [string]@($json.rootObjects)[0] }
        }
    }

    return $null
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

    $category = [string]$Json.category
    $range = $script:ArtPaintCategories[$category]
    if (@($script:ArtPaintCategories.Keys) -cnotcontains $category) {
        $problems.Add("category must be one of: $(@($script:ArtPaintCategories.Keys) -join ', ')")
    }
    elseif (-not ($Json.layer -is [int] -or $Json.layer -is [long]) -or $Json.layer -lt $range[0] -or $Json.layer -gt $range[1]) {
        $problems.Add("layer must be a whole number from $($range[0]) to $($range[1]) for a $category, the order paint layers are drawn in")
    }

    # Underwear is worn in a visual slot, which a garment of that slot can hide; other paint is not.
    $slot = $Json.slot
    if ($category -ceq "underwear" -and $script:ArtUnderwearSlots -cnotcontains [string]$slot) {
        $problems.Add("underwear needs the slot it is worn in: $($script:ArtUnderwearSlots -join ' or ')")
    }
    elseif ($category -cne "underwear" -and $null -ne $Json.PSObject.Properties["slot"]) {
        $problems.Add("only underwear has a slot")
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
    $exportProblem = $null
    if ($null -ne $Json.export) {
        $textures = ConvertTo-ArtPaintTextureRecords $Json.export.textures $script:ArtPaintTextureFolder
        # An export from before paint layers kept their place in the bodies' paint UVs has plain
        # texture records; it is stale, and the next export replaces it.
        if ($null -eq $textures -and @($Json.export.textures | Where-Object { $null -ne $_ -and @($_.rect | Where-Object { $null -ne $_ }).Count -eq 0 }).Count -gt 0) {
            $textures = ConvertTo-ArtTextureRecords $Json.export.textures $script:ArtPaintTextureFolder
            $exportProblem = "export.textures were written before paint layers were cropped in the bodies' paint UVs; export the asset again"
        }
        $expected = (@($bodies | ForEach-Object { "T_Paint_${name}_$($_.Root)" }) | Sort-Object) -join ","
        $smoothness = $Json.export.smoothness
        $metallic = $Json.export.metallic
        $cageSha = $Json.export.cageSourceSha256
        $throughCage = @($textures | Where-Object { $_.move -ceq "cage" }).Count -gt 0
        if ([string]$Json.export.sourceSha256 -cnotmatch $script:ArtShaPattern) {
            $problems.Add("export needs a sourceSha256")
        }
        elseif ($null -eq $textures -or ((@($textures | ForEach-Object { $_.name }) | Sort-Object) -join ",") -cne $expected) {
            $problems.Add("export.textures must list T_Paint_${name}_<Label> of every body once with its target ($script:ArtPaintTextureFolder/<name>.png), sha256, layout, rect starting at whole blocks of $script:ArtPaintCropBlock texels, move (topology or cage), and bodyPaintSha256")
        }
        elseif (-not (Test-ArtNumber $smoothness) -or $smoothness -lt 0 -or $smoothness -gt 1 -or
            -not (Test-ArtNumber $metallic) -or $metallic -lt 0 -or $metallic -gt 1) {
            $problems.Add("export needs the smoothness and metallic of the paint, from 0 to 1")
        }
        elseif (($throughCage -and [string]$cageSha -cnotmatch $script:ArtShaPattern) -or (-not $throughCage -and $null -ne $cageSha)) {
            $problems.Add("export has the cageSourceSha256 of the cage file exactly when a body was moved through the cage")
        }
        else {
            $export = [ordered]@{
                sourceSha256 = [string]$Json.export.sourceSha256
                cageSourceSha256 = if ($throughCage) { [string]$cageSha } else { $null }
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
        Category = $category
        Slot = if ($category -ceq "underwear") { [string]$slot } else { $null }
        Part = $part
        Layer = [int]$Json.layer
        Bodies = $bodies
        Approvals = $approvals
        Export = $export
        ExportProblem = $exportProblem
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

        if ($material.Contains("dyeMap")) {
            $dyeColors = @($material.dyeColors | ForEach-Object { '"' + $_ + '"' }) -join ", "
            $Lines.Add("                `"dyeMap`": `"$($material.dyeMap)`",")
            $Lines.Add("                `"dyeColors`": [$dyeColors],")
        }

        $Lines.Add("                `"metallic`": $(Format-ArtNumber $material.metallic),")
        $Lines.Add("                `"smoothness`": $(Format-ArtNumber $material.smoothness)")
        $Lines.Add("            }$separator")
    }
    $Lines.Add("        ]$Comma")
}

function Add-ArtTextureLines($Lines, $Textures, [string]$Comma) {
    if ($Textures.Count -eq 0) {
        $Lines.Add("        `"textures`": []$Comma")
        return
    }

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

function Add-ArtPaintTextureLines($Lines, $Textures, [string]$Comma) {
    $Lines.Add('        "textures": [')
    for ($i = 0; $i -lt $Textures.Count; $i++) {
        $texture = $Textures[$i]
        $separator = if ($i -lt $Textures.Count - 1) { "," } else { "" }
        $Lines.Add("            {")
        $Lines.Add("                `"name`": `"$($texture.name)`",")
        $Lines.Add("                `"target`": `"$($texture.target)`",")
        $Lines.Add("                `"sha256`": `"$($texture.sha256)`",")
        $Lines.Add("                `"layout`": [$(@($texture.layout) -join ', ')],")
        $Lines.Add("                `"rect`": [$(@($texture.rect) -join ', ')],")
        $Lines.Add("                `"move`": `"$($texture.move)`",")
        $Lines.Add("                `"bodyPaintSha256`": `"$($texture.bodyPaintSha256)`"")
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
        if ($approval.Contains("garmentSourceSha256") -and -not [string]::IsNullOrEmpty($approval.garmentSourceSha256)) {
            $Lines.Add("            `"sourceSha256`": `"$($approval.sourceSha256)`",")
            $Lines.Add("            `"garmentSourceSha256`": `"$($approval.garmentSourceSha256)`"")
        }
        else {
            $Lines.Add("            `"sourceSha256`": `"$($approval.sourceSha256)`"")
        }

        $Lines.Add("        }$comma")
    }
    $Lines.Add("    },")
}

function Write-ArtPaintManifest($Manifest) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("{")
    $lines.Add('    "schemaVersion": 1,')
    $lines.Add('    "kind": "paint",')
    $lines.Add("    `"category`": `"$($Manifest.Category)`",")
    if ($null -ne $Manifest.Slot) {
        $lines.Add("    `"slot`": `"$($Manifest.Slot)`",")
    }

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
        if (-not [string]::IsNullOrEmpty($Manifest.Export.cageSourceSha256)) {
            $lines.Add("        `"cageSourceSha256`": `"$($Manifest.Export.cageSourceSha256)`",")
        }
        # A stale export from before the crops keeps its plain records until the next export.
        if (@($Manifest.Export.textures | Where-Object { -not $_.Contains("rect") }).Count -gt 0) {
            Add-ArtTextureLines $lines @($Manifest.Export.textures) ","
        }
        else {
            Add-ArtPaintTextureLines $lines @($Manifest.Export.textures) ","
        }
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

function Get-ArtDataTarget([string]$Target) {
    # The data a model exports next to it, such as a body's topology.
    return $Target -replace '\.fbx$', '.json'
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

function Get-ArtClipSourcesProblem($Manifest, [string]$Sync) {
    # Returns $null when the export of a garment template, or a texture's masks, was checked in the
    # current exports of the animation libraries of its skeleton, otherwise the reason it was not.
    $libraries = @(Get-ArtClipLibraries $Manifest.Skeleton | ForEach-Object {
            [string]$_.Asset + "=" + $(if ($null -ne $_.Export) { [string]$_.Export.sourceSha256 } else { "" })
        } | Sort-Object)
    $recorded = @($Manifest.Export.clipSources | Where-Object { $null -ne $_ } | ForEach-Object { "$($_.library)=$($_.sourceSha256)" } | Sort-Object)
    if (($libraries -join ",") -cne ($recorded -join ",")) {
        return "was checked in other clips than the current exports of the $($Manifest.Skeleton) animation libraries; run $Sync, review and approve it, then run tools/Export-ArtAsset.ps1"
    }

    return $null
}

function Get-ArtPaintDependencyProblems($Manifest) {
    # The reasons the export of a paint layer no longer matches what it was moved to: the exports of
    # its bodies, and the file of the cage when a body was moved through it. None when it matches.
    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($body in @($Manifest.Bodies)) {
        $texture = @($Manifest.Export.textures | Where-Object { $_.name -ceq "T_Paint_$($Manifest.Name)_$($body.Root)" }) | Select-Object -First 1
        $bodyExport = $null
        try {
            $bodyExport = (Read-ArtAssetManifest (Join-Path $script:ArtRepositoryRoot $body.Asset)).Export
        }
        catch {
            # The body's own problems are reported with the body.
        }

        if ($null -eq $texture -or $null -eq $bodyExport -or [string]::IsNullOrEmpty($bodyExport.paintSha256) -or
            $bodyExport.paintSha256 -ne $texture.bodyPaintSha256) {
            $problems.Add("was moved to another export of its body $($body.Asset); run tools/Sync-PaintAsset.ps1, review and approve it, then run tools/Export-ArtAsset.ps1")
        }
    }

    if (-not [string]::IsNullOrEmpty($Manifest.Export.cageSourceSha256)) {
        $cage = @(Get-ArtPaintCage $Manifest) | Select-Object -First 1
        if ($null -eq $cage -or (Get-ArtFileSha256 $cage.SourcePath) -ne $Manifest.Export.cageSourceSha256) {
            $problems.Add("was moved through another file of the garment cage of its bodies; run tools/Sync-PaintAsset.ps1, review and approve it, then run tools/Export-ArtAsset.ps1")
        }
    }

    return $problems.ToArray()
}

function Get-ArtPaintCage($Manifest) {
    # The garment cages whose bodies hold every body of a paint layer.
    $assets = @($Manifest.Bodies | ForEach-Object { $_.Asset })
    foreach ($directory in @(Get-ArtAssetDirectories)) {
        $json = Get-Content -Raw -LiteralPath (Join-Path $directory "asset.json") | ConvertFrom-Json
        if ([string]$json.kind -ne "cage") {
            continue
        }

        $bodies = @($json.bodies | ForEach-Object { [string]$_ })
        if (@($assets | Where-Object { $bodies -cnotcontains $_ }).Count -eq 0) {
            [pscustomobject]@{
                RelativeDirectory = Get-ArtRelativePath $directory
                SourcePath = Join-Path $directory "$(Split-Path -Leaf $directory).blend"
            }
        }
    }
}

function Get-ArtGarmentDependencyProblems($Manifest) {
    # The reasons the export of a garment template no longer matches what it was made from: the
    # approved file of its cage, the bodies of the cage and their exports, and the exports of the
    # animation libraries it was checked in. None when it matches.
    $problems = New-Object System.Collections.Generic.List[string]
    if ((Get-ArtFileSha256 $Manifest.Cage.SourcePath) -ne $Manifest.Export.cageSourceSha256) {
        $problems.Add("was fitted to another file of its cage $($Manifest.Cage.RelativeDirectory); run tools/Sync-GarmentAsset.ps1, review and approve it, then run tools/Export-ArtAsset.ps1")
    }

    $exported = @($Manifest.Export.models | ForEach-Object { [string]$_.body } | Sort-Object)
    $current = @($Manifest.Models | ForEach-Object { $_.Body } | Sort-Object)
    if (($exported -join ",") -cne ($current -join ",")) {
        $problems.Add("was exported for other bodies ($($exported -join ', ')) than its cage has ($($current -join ', ')); run tools/Sync-GarmentAsset.ps1, review and approve it, then run tools/Export-ArtAsset.ps1")
    }

    foreach ($model in @($Manifest.Export.models)) {
        # A body that left the cage is reported above.
        $body = @($Manifest.Models | Where-Object { $_.Body -ceq $model.body }) | Select-Object -First 1
        if ($null -eq $body) {
            continue
        }

        $bodyExport = $null
        try {
            $bodyExport = (Read-ArtAssetManifest (Join-Path $script:ArtRepositoryRoot $body.BodyAsset)).Export
        }
        catch {
            # The body's own problems are reported with the body.
        }

        if ($null -eq $bodyExport -or [string]::IsNullOrEmpty($bodyExport.fitSha256) -or $bodyExport.fitSha256 -ne $model.bodyFitSha256) {
            $problems.Add("was fitted to another rig, surface, or weights of its body $($body.BodyAsset); run tools/Sync-GarmentAsset.ps1, review and approve it if it changed, then run tools/Export-ArtAsset.ps1")
        }
    }

    $clipProblem = Get-ArtClipSourcesProblem $Manifest "tools/Sync-GarmentAsset.ps1"
    if ($null -ne $clipProblem) {
        $problems.Add($clipProblem)
    }

    $pairProblem = Get-ArtPairSourcesProblem $Manifest
    if ($null -ne $pairProblem) {
        $problems.Add($pairProblem)
    }

    return $problems.ToArray()
}

function Get-ArtGarmentPairs($Manifest) {
    # The exported templates of the same cage, another visual slot, and a lower layer, which a
    # template is checked over: worn under it, they must not come through it at its openings. A
    # body wears one garment per visual slot, so templates of the same slot are never worn together,
    # and a template that hides a slot, such as a robe the legs, is worn over none of that slot.
    # Headwear that hides the hair is worn over no hairstyle; one that compresses it is checked over
    # the hairstyle's form UnderHeadwear.
    return @(Get-ArtAssetDirectories | Where-Object { (Split-Path -Leaf (Split-Path -Parent $_)) -ceq "Garments" } | ForEach-Object {
            $other = $null
            try {
                $other = Read-ArtAssetManifest $_
            }
            catch {
                # Its own problems are reported with it.
            }

            if ($null -ne $other -and $other.Kind -eq "garment" -and $other.Name -cne $Manifest.Name -and $null -ne $other.Export -and
                $null -ne $other.Cage -and $other.Cage.RelativeDirectory -ceq $Manifest.Cage.RelativeDirectory -and
                $other.Slot -cne $Manifest.Slot -and $other.Layer -lt $Manifest.Layer -and @($Manifest.Hides) -cnotcontains $other.Slot -and
                -not ($Manifest.Hair -ceq "hide" -and $other.Slot -ceq "hair")) {
                $other
            }
        } | Sort-Object Name)
}

function Get-ArtGarmentPairArguments($Manifest) {
    return @(Get-ArtGarmentPairs $Manifest | ForEach-Object { @("--pair", "$($_.SourcePath)=$($_.Name):$($_.Band)") })
}

function Get-ArtPairSourcesProblem($Manifest) {
    # Returns $null when the template's export was checked over the current exports of every template
    # of another visual slot and a lower layer of its cage, otherwise the reason it was not.
    $current = @(Get-ArtGarmentPairs $Manifest | ForEach-Object { "$($_.RelativeDirectory)=$($_.Export.sourceSha256)" } | Sort-Object)
    $recorded = @($Manifest.Export.pairSources | Where-Object { $null -ne $_ } | ForEach-Object { "$($_.garment)=$($_.sourceSha256)" } | Sort-Object)
    if (($current -join ",") -cne ($recorded -join ",")) {
        return "was checked over other templates of a lower layer than the current exports of its cage's templates; run tools/Sync-GarmentAsset.ps1, review it, then run tools/Export-ArtAsset.ps1"
    }

    return $null
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

function Get-ArtGarmentCoverArguments($Garment) {
    # A template's band, which its masks are computed in, and whether it covers nothing at all.
    $arguments = @("--band", $Garment.Band)
    if (-not $Garment.Covers) {
        $arguments += "--covers-nothing"
    }

    return $arguments
}

function ConvertTo-ArtGarmentRecipe($Recipe, $Cage) {
    # Returns a template's recipe, "modeled" or its regions, cuts, what it lies over, ease, and hem,
    # or $null when it names a region or a line its cage does not have, a line outside its regions,
    # a band that is not one, or a value out of range.
    if ($Recipe -is [string]) {
        if ($Recipe -ceq "modeled") {
            return "modeled"
        }

        return $null
    }

    if ($null -eq $Recipe -or $null -eq $Cage) {
        return $null
    }

    $regions = @($Recipe.regions | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
    if ($regions.Count -eq 0 -or @($regions | Sort-Object -Unique).Count -ne $regions.Count -or
        @($regions | Where-Object { $script:ArtCageRegions -cnotcontains $_ }).Count -gt 0) {
        return $null
    }

    $cuts = New-Object System.Collections.Generic.List[object]
    $seen = New-Object System.Collections.Generic.HashSet[string]
    foreach ($cut in @($Recipe.cuts | Where-Object { $null -ne $_ })) {
        $line = @($Cage.Lines | Where-Object { $_.name -ceq [string]$cut.line }) | Select-Object -First 1
        if ($null -eq $line -or $regions -cnotcontains $line.region -or $script:ArtGarmentKeeps -cnotcontains [string]$cut.keep -or
            -not $seen.Add([string]$cut.line)) {
            return $null
        }

        $cuts.Add([ordered]@{ line = [string]$cut.line; keep = [string]$cut.keep })
    }

    # Beyond a standard line, on the side an inner garment keeps, the template lies over a garment of
    # the given band, such as a top over the trousers below Waist.
    $over = New-Object System.Collections.Generic.List[object]
    $overLines = New-Object System.Collections.Generic.HashSet[string]
    foreach ($entry in @($Recipe.over | Where-Object { $null -ne $_ })) {
        $line = @($Cage.Lines | Where-Object { $_.name -ceq [string]$entry.line }) | Select-Object -First 1
        if ($null -eq $line -or $regions -cnotcontains $line.region -or $script:ArtGarmentKeeps -cnotcontains [string]$entry.keep -or
            $script:ArtGarmentBands -cnotcontains [string]$entry.band -or -not $overLines.Add([string]$entry.line)) {
            return $null
        }

        $over.Add([ordered]@{ line = [string]$entry.line; keep = [string]$entry.keep; band = [string]$entry.band })
    }

    # Loose parts that hang on the cloth chains from the template's open edge at a line of the trunk,
    # all around for a skirt or across the back for a cloak, widening by their flare toward the hem.
    # A part across the back may hang from a narrow top, which its yoke keeps to, and fan out to a
    # width, both shares of how far the shoulder joints lie from the middle.
    $hang = New-Object System.Collections.Generic.List[object]
    $hangLines = New-Object System.Collections.Generic.HashSet[string]
    foreach ($entry in @($Recipe.hang | Where-Object { $null -ne $_ })) {
        $line = @($Cage.Lines | Where-Object { $_.name -ceq [string]$entry.line }) | Select-Object -First 1
        $flare = $entry.flare
        if ($null -eq $line -or $line.region -cne "trunk" -or $regions -cnotcontains "trunk" -or
            $script:ArtGarmentHangSides -cnotcontains [string]$entry.side -or
            $script:ArtGarmentHangChains -cnotcontains [string]$entry.chains -or -not (Test-ArtNumber $flare) -or
            $flare -lt 0 -or $flare -gt $script:ArtGarmentFlareLimit -or -not $hangLines.Add([string]$entry.line)) {
            return $null
        }

        $part = [ordered]@{ line = [string]$entry.line; side = [string]$entry.side; chains = [string]$entry.chains; flare = [double]$flare }
        $names = if ($entry -is [System.Collections.IDictionary]) { @($entry.Keys) } else { @($entry.PSObject.Properties.Name) }
        foreach ($key in @("top", "width")) {
            if ($names -cnotcontains $key) {
                continue
            }

            $share = $entry.$key
            if ([string]$entry.side -cne "back" -or -not (Test-ArtNumber $share) -or $share -le 0 -or
                $share -gt $script:ArtGarmentHangShareLimit) {
                return $null
            }

            $part[$key] = [double]$share
        }

        if ($part.Contains("top") -and $part.Contains("width") -and $part.width -lt $part.top) {
            return $null
        }

        $hang.Add($part)
    }

    $ease = $Recipe.ease
    $hem = $Recipe.hem
    if (-not (Test-ArtNumber $ease) -or $ease -lt 0 -or $ease -gt $script:ArtGarmentEaseLimit -or
        -not (Test-ArtNumber $hem) -or $hem -lt 0 -or $hem -gt $script:ArtGarmentHemLimit) {
        return $null
    }

    $result = [ordered]@{ regions = [string[]]$regions; cuts = $cuts.ToArray() }
    if ($over.Count -gt 0) {
        $result.over = $over.ToArray()
    }

    if ($hang.Count -gt 0) {
        $result.hang = $hang.ToArray()
    }

    $result.ease = [double]$ease
    $result.hem = [double]$hem
    return $result
}

function Read-ArtGarmentManifest([string]$AssetDirectory, $Json, [string]$Where, [switch]$AllowMissingSource) {
    # A garment template: the form of a garment kind, made on the garment cage of its skeleton from a
    # recipe or by hand, fitted to every body of the cage, and exported as one model per body,
    # skinned to that body's rig. See docs/EQUIPMENT_VISUALS_DESIGN.md.
    $name = Split-Path -Leaf $AssetDirectory
    $problems = New-Object System.Collections.Generic.List[string]
    if ($name -notmatch '^[A-Za-z][A-Za-z0-9]*$') {
        $problems.Add("the folder name '$name' of a garment must use only letters and digits")
    }

    if ($Json.schemaVersion -ne 2) {
        $problems.Add("schemaVersion must be 2")
    }

    foreach ($field in @("target", "rootObjects", "skeleton", "clips", "rig", "previews", "variants", "part", "bodies")) {
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

    # A garment that bares the shoulders or has a deep neckline hides the underwear of its slot.
    $hidesUnderwear = $Json.hidesUnderwear
    if ($null -ne $Json.PSObject.Properties["hidesUnderwear"] -and $hidesUnderwear -isnot [bool]) {
        $problems.Add("hidesUnderwear must be true or false")
    }

    # A garment that lies on top and may swing open, such as a cloak, covers nothing under it.
    $covers = $Json.covers
    if ($null -ne $Json.PSObject.Properties["covers"] -and $covers -isnot [bool]) {
        $problems.Add("covers must be true or false")
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

    if (-not ($Json.dyes -is [int] -or $Json.dyes -is [long]) -or $Json.dyes -lt 0 -or $Json.dyes -gt 3) {
        $problems.Add("dyes must be the number of dye channels its textures may use, 0 to 3")
    }

    $triangleBudget = $null
    if (-not ($Json.triangleBudget -is [int] -or $Json.triangleBudget -is [long]) -or $Json.triangleBudget -le 0) {
        $problems.Add("triangleBudget must be a positive whole number, the most triangles of one body's version")
    }
    else {
        $triangleBudget = [int]$Json.triangleBudget
    }

    # The bodies are those of the cage.
    $cage = $null
    $bodies = @()
    $skeleton = $null
    $skeletonPath = $null
    $cagePath = [string]$Json.cage
    if ($cagePath -cnotmatch '^art/Cages/[A-Za-z][A-Za-z0-9]*$' -or
        -not (Test-Path -LiteralPath (Join-Path (Join-Path $script:ArtRepositoryRoot $cagePath) "asset.json") -PathType Leaf)) {
        $problems.Add("cage must name the garment cage the template is made on, art/Cages/<Skeleton>")
    }
    else {
        try {
            $cage = Read-ArtAssetManifest (Join-Path $script:ArtRepositoryRoot $cagePath)
            if ($cage.Kind -ne "cage") {
                throw "$cagePath is a $($cage.Kind) asset, not a garment cage"
            }

            $bodies = @($cage.Bodies)
            $skeleton = $cage.Skeleton
            $skeletonPath = $cage.SkeletonPath
        }
        catch {
            $problems.Add($_.Exception.Message)
            $cage = $null
        }
    }

    $recipe = ConvertTo-ArtGarmentRecipe $Json.recipe $cage
    if ($null -ne $cage -and $null -eq $recipe) {
        $problems.Add("recipe must be ""modeled"" or name the regions of the cage the template covers ($($script:ArtCageRegions -join ', ')), its cuts, each a line of the cage in those regions with the side it keeps (head or tail), optionally what it lies over, each a line of those regions with the side the inner garment keeps and the inner garment's band, optionally its loose parts, each a line of the trunk it hangs from, its side ($($script:ArtGarmentHangSides -join ', ')), its chains ($($script:ArtGarmentHangChains -join ', ')), its flare from 0 to $script:ArtGarmentFlareLimit m, and across the back optionally its top and its width, no smaller than its top, both above 0 and up to $script:ArtGarmentHangShareLimit of the shoulders' half width, its ease from 0 to $script:ArtGarmentEaseLimit m, and its hem from 0 to $script:ArtGarmentHemLimit m")
    }

    # The chains a loose part hangs on move only with the cloth profile that activates them.
    foreach ($part in @($(if ($recipe -is [System.Collections.IDictionary]) { $recipe["hang"] }) | Where-Object { $null -ne $_ })) {
        $clothOfChains = $script:ArtGarmentHangCloth[[string]$part.chains]
        if ([string]$cloth -cne $clothOfChains) {
            $problems.Add("cloth must be ""$clothOfChains"" for a template whose loose parts hang on the $($part.chains) chains")
        }
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
        $cageSourceSha256 = [string]$Json.export.cageSourceSha256
        # The export may name other bodies than the cage has now, after a body joined or left the
        # cage: it is then stale, which Verify-ArtAssets.ps1 reports, and a new export replaces it.
        $modelsMatch = $records.Count -gt 0 -and
            @($records | ForEach-Object { [string]$_.body } | Sort-Object -Unique).Count -eq $records.Count
        foreach ($record in $records) {
            $label = [string]$record.body
            $modelsMatch = $modelsMatch -and $label -cmatch $script:ArtNamePattern -and
                [string]$record.target -ceq (Get-ArtGarmentModelTarget $name $label) -and
                [string]$record.sha256 -cmatch $script:ArtShaPattern -and
                [string]$record.data -ceq (Get-ArtGarmentDataTarget $name $label) -and
                [string]$record.dataSha256 -cmatch $script:ArtShaPattern -and
                ([string]$record.bodyFitSha256 -cmatch $script:ArtShaPattern -or [string]$record.bodySourceSha256 -cmatch $script:ArtShaPattern)
        }

        $clipSources = @($Json.export.clipSources | Where-Object { $null -ne $_ })
        $clipSourcesMatch = @($clipSources | Where-Object {
                [string]$_.library -cnotmatch '^art/[A-Za-z][A-Za-z0-9_]*/[A-Za-z][A-Za-z0-9_]*$' -or
                [string]$_.sourceSha256 -cnotmatch $script:ArtShaPattern }).Count -eq 0 -and
            @($clipSources | ForEach-Object { [string]$_.library } | Sort-Object -Unique).Count -eq $clipSources.Count
        # The templates of a lower layer it was checked over, with the sourceSha256 of their exports.
        $pairSources = @($Json.export.pairSources | Where-Object { $null -ne $_ })
        $pairSourcesMatch = @($pairSources | Where-Object {
                [string]$_.garment -cnotmatch '^art/Garments/[A-Za-z][A-Za-z0-9]*$' -or
                [string]$_.sourceSha256 -cnotmatch $script:ArtShaPattern }).Count -eq 0 -and
            @($pairSources | ForEach-Object { [string]$_.garment } | Sort-Object -Unique).Count -eq $pairSources.Count

        $baseMaps = (@($materials | Where-Object { $null -ne $_ -and $_.Contains("baseMap") } | ForEach-Object { $_.baseMap }) | Sort-Object) -join ","
        if ([string]$Json.export.sourceSha256 -cnotmatch $script:ArtShaPattern -or $cageSourceSha256 -cnotmatch $script:ArtShaPattern) {
            $problems.Add("export needs a sourceSha256 and the cageSourceSha256 of the approved cage file it was fitted to")
        }
        elseif (-not $modelsMatch) {
            $problems.Add("export.models must list the model of each body once with its body, the bodyFitSha256 of the body's export, its target ($script:ArtGarmentModelFolder/${name}_<Label>.fbx) and sha256, and its data ($script:ArtGarmentModelFolder/${name}_<Label>.json) and dataSha256")
        }
        elseif (-not $clipSourcesMatch) {
            $problems.Add("export.clipSources must list every animation library the garment was checked in once, with its library (art/<Category>/<Name>) and the sourceSha256 of its export")
        }
        elseif (-not $pairSourcesMatch) {
            $problems.Add("export.pairSources must list every template of a lower layer the garment was checked over once, with its garment (art/Garments/<Name>) and the sourceSha256 of its export")
        }
        elseif ($null -eq $materials -or @($materials | Where-Object { $_.name -cne "M_Garment_$name" }).Count -gt 0) {
            $problems.Add("export.materials must hold the one material M_Garment_$name with its baseColor (#RRGGBB), metallic, and smoothness")
        }
        elseif ($baseMaps -ne "" -or $null -eq $textures -or $textures.Count -gt 0) {
            $problems.Add("export.textures must be empty; a template's look comes from its garment textures")
        }
        else {
            $export = [ordered]@{
                sourceSha256 = [string]$Json.export.sourceSha256
                cageSourceSha256 = $cageSourceSha256
                models = @($records | ForEach-Object {
                        # A record from before the fingerprint names the body's export sourceSha256
                        # instead; it is stale, which Get-ArtGarmentDependencyProblems reports.
                        [ordered]@{
                            body = [string]$_.body
                            bodyFitSha256 = if ($null -ne $_.bodyFitSha256) { [string]$_.bodyFitSha256 } else { $null }
                            bodySourceSha256 = if ($null -ne $_.bodySourceSha256) { [string]$_.bodySourceSha256 } else { $null }
                            target = [string]$_.target
                            sha256 = [string]$_.sha256
                            data = [string]$_.data
                            dataSha256 = [string]$_.dataSha256
                        }
                    })
                clipSources = @($clipSources | ForEach-Object { [ordered]@{ library = [string]$_.library; sourceSha256 = [string]$_.sourceSha256 } })
                pairSources = @($pairSources | ForEach-Object { [ordered]@{ garment = [string]$_.garment; sourceSha256 = [string]$_.sourceSha256 } })
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
        Cage = $cage
        Recipe = $recipe
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
        HidesUnderwear = $hidesUnderwear -eq $true
        Covers = $covers -ne $false
        Hair = if ($null -ne $hair) { [string]$hair } else { $null }
        Cloth = if ($null -ne $cloth) { [string]$cloth } else { $null }
        Dyes = [int]$Json.dyes
        Models = $models
        Approvals = $approvals
        Export = $export
        ExportProblem = $null
    }
}

function Add-ArtRecipeLines($Lines, $Recipe) {
    if ($Recipe -is [string]) {
        $Lines.Add("    `"recipe`": `"$Recipe`",")
        return
    }

    $regions = (@($Recipe.regions) | ForEach-Object { '"' + $_ + '"' }) -join ", "
    $Lines.Add('    "recipe": {')
    $Lines.Add("        `"regions`": [$regions],")
    $cuts = @($Recipe.cuts)
    if ($cuts.Count -eq 0) {
        $Lines.Add('        "cuts": [],')
    }
    else {
        $Lines.Add('        "cuts": [')
        for ($i = 0; $i -lt $cuts.Count; $i++) {
            $comma = if ($i -lt $cuts.Count - 1) { "," } else { "" }
            $Lines.Add("            { `"line`": `"$($cuts[$i].line)`", `"keep`": `"$($cuts[$i].keep)`" }$comma")
        }
        $Lines.Add('        ],')
    }

    $over = @($Recipe.over | Where-Object { $null -ne $_ })
    if ($over.Count -gt 0) {
        $Lines.Add('        "over": [')
        for ($i = 0; $i -lt $over.Count; $i++) {
            $comma = if ($i -lt $over.Count - 1) { "," } else { "" }
            $Lines.Add("            { `"line`": `"$($over[$i].line)`", `"keep`": `"$($over[$i].keep)`", `"band`": `"$($over[$i].band)`" }$comma")
        }
        $Lines.Add('        ],')
    }

    $hang = @($Recipe.hang | Where-Object { $null -ne $_ })
    if ($hang.Count -gt 0) {
        $Lines.Add('        "hang": [')
        for ($i = 0; $i -lt $hang.Count; $i++) {
            $comma = if ($i -lt $hang.Count - 1) { "," } else { "" }
            $shares = @("top", "width" | Where-Object { $null -ne $hang[$i].$_ } |
                    ForEach-Object { ", `"$_`": $(Format-ArtShare $hang[$i].$_)" })
            $Lines.Add("            { `"line`": `"$($hang[$i].line)`", `"side`": `"$($hang[$i].side)`", `"chains`": `"$($hang[$i].chains)`", `"flare`": $(Format-ArtShare $hang[$i].flare)$($shares -join '') }$comma")
        }
        $Lines.Add('        ],')
    }

    $Lines.Add("        `"ease`": $(Format-ArtShare $Recipe.ease),")
    $Lines.Add("        `"hem`": $(Format-ArtShare $Recipe.hem)")
    $Lines.Add('    },')
}

function Write-ArtGarmentManifest($Manifest) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("{")
    $lines.Add('    "schemaVersion": 2,')
    $lines.Add('    "kind": "garment",')
    $lines.Add("    `"cage`": `"$($Manifest.Cage.RelativeDirectory)`",")
    $lines.Add("    `"slot`": `"$($Manifest.Slot)`",")
    $lines.Add("    `"layer`": $($Manifest.Layer),")
    $lines.Add("    `"band`": `"$($Manifest.Band)`",")
    Add-ArtRecipeLines $lines $Manifest.Recipe
    $hides = (@($Manifest.Hides) | ForEach-Object { '"' + $_ + '"' }) -join ", "
    $lines.Add("    `"hides`": [$hides],")
    $lines.Add("    `"hidesUnderwear`": $(if ($Manifest.HidesUnderwear) { 'true' } else { 'false' }),")
    $lines.Add("    `"covers`": $(if ($Manifest.Covers) { 'true' } else { 'false' }),")
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
        $lines.Add("        `"cageSourceSha256`": `"$($Manifest.Export.cageSourceSha256)`",")
        $lines.Add('        "models": [')
        $models = @($Manifest.Export.models)
        for ($i = 0; $i -lt $models.Count; $i++) {
            $comma = if ($i -lt $models.Count - 1) { "," } else { "" }
            $lines.Add("            {")
            $lines.Add("                `"body`": `"$($models[$i].body)`",")
            if (-not [string]::IsNullOrEmpty($models[$i].bodyFitSha256)) {
                $lines.Add("                `"bodyFitSha256`": `"$($models[$i].bodyFitSha256)`",")
            }
            else {
                $lines.Add("                `"bodySourceSha256`": `"$($models[$i].bodySourceSha256)`",")
            }
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
        $pairs = @($Manifest.Export.pairSources | Where-Object { $null -ne $_ })
        if ($pairs.Count -gt 0) {
            $lines.Add('        "pairSources": [')
            for ($i = 0; $i -lt $pairs.Count; $i++) {
                $comma = if ($i -lt $pairs.Count - 1) { "," } else { "" }
                $lines.Add("            { `"garment`": `"$($pairs[$i].garment)`", `"sourceSha256`": `"$($pairs[$i].sourceSha256)`" }$comma")
            }
            $lines.Add('        ],')
        }

        Add-ArtMaterialLines $lines @($Manifest.Export.materials) ","
        Add-ArtTextureLines $lines @($Manifest.Export.textures) ""
        $lines.Add("    }")
    }

    $lines.Add("}")
    $text = ($lines -join "`n") + "`n"
    [System.IO.File]::WriteAllText($Manifest.ManifestPath, $text, (New-Object System.Text.UTF8Encoding($false)))
}

function Read-ArtCageManifest([string]$AssetDirectory, $Json, [string]$Where, [switch]$AllowMissingSource) {
    # The garment cage of a skeleton: a net of fixed topology fitted to every body of the skeleton,
    # on which garments are made and fitted. It has one review stage and no export; garments record
    # its approved file. See docs/EQUIPMENT_VISUALS_DESIGN.md.
    $name = Split-Path -Leaf $AssetDirectory
    $problems = New-Object System.Collections.Generic.List[string]
    if ($Json.schemaVersion -ne 1) {
        $problems.Add("schemaVersion must be 1")
    }

    foreach ($field in @("target", "rootObjects", "triangleBudget", "skeleton", "clips", "rig", "previews", "variants", "part", "export")) {
        if ($null -ne $Json.PSObject.Properties[$field]) {
            $problems.Add("a cage has no $field")
        }
    }

    $bodies = @()
    $skeleton = $null
    $skeletonPath = $null
    $boneNames = @()
    try {
        $first = @($Json.bodies)[0]
        $skeleton = if ($null -ne $first) { [string](Get-Content -Raw -LiteralPath (Join-Path (Join-Path $script:ArtRepositoryRoot ([string]$first)) "asset.json") | ConvertFrom-Json).skeleton } else { "" }
        $bodies = @(@($Json.bodies | Where-Object { $null -ne $_ }) | ForEach-Object { Read-ArtRigReference ([string]$_) $skeleton "bodies" })
        $skeletonPath = Read-ArtSkeleton $skeleton
        $boneNames = @((Get-Content -Raw -LiteralPath $skeletonPath | ConvertFrom-Json).bones | ForEach-Object { [string]$_.name })
    }
    catch {
        $problems.Add($_.Exception.Message)
    }

    if ($bodies.Count -eq 0 -or @($bodies | ForEach-Object { $_.Asset } | Sort-Object -Unique).Count -ne $bodies.Count -or
        @($bodies | ForEach-Object { $_.Root } | Sort-Object -Unique).Count -ne $bodies.Count) {
        $problems.Add("bodies must name one or more skinned assets of one skeleton, each once, the body the cage is made from first")
    }
    elseif ($name -cne $skeleton) {
        $problems.Add("a cage lies in the folder of its skeleton, art/Cages/$skeleton")
    }

    $figure = $Json.figure
    if (-not (Test-ArtNumber $figure) -or $figure -lt $script:ArtCageFigureRange[0] -or $figure -gt $script:ArtCageFigureRange[1]) {
        $problems.Add("figure must be the figure scale in meters, from $($script:ArtCageFigureRange[0]) to $($script:ArtCageFigureRange[1])")
    }

    $lines = New-Object System.Collections.Generic.List[object]
    $lineNames = New-Object System.Collections.Generic.HashSet[string]
    foreach ($line in @($Json.lines | Where-Object { $null -ne $_ })) {
        $lineName = [string]$line.name
        $region = [string]$line.region
        $bone = [string]$line.bone
        $share = $line.share
        $sided = $script:ArtCageSidedRegions -ccontains $region
        $bonesFound = if ($sided) { $boneNames -ccontains "${bone}_L" -and $boneNames -ccontains "${bone}_R" } else { $boneNames -ccontains $bone }
        if ($lineName -cnotmatch '^[A-Z][A-Za-z0-9]*$' -or -not $lineNames.Add($lineName) -or
            $script:ArtCageRegions -cnotcontains $region -or $bone -cnotmatch '^[A-Za-z][A-Za-z0-9]*$' -or -not $bonesFound -or
            -not (Test-ArtNumber $share) -or $share -lt 0 -or $share -gt 1) {
            $problems.Add("every line needs a unique name, a region ($($script:ArtCageRegions -join ', ')), a bone of the skeleton, without its side _L or _R for the arms, legs, hands, and feet, and a share of the bone from 0 to 1; '$lineName' does not")
            continue
        }

        # A line of the neck may rise toward its sides, by the rise times the square of the distance
        # to the side, a neckline that hugs the neck.
        $rise = $line.rise
        if ($null -ne $line.PSObject.Properties["rise"] -and
            (-not (Test-ArtNumber $rise) -or $rise -le 0 -or $rise -gt 20 -or $region -cne "neck")) {
            $problems.Add("rise is how a line of the neck climbs toward its sides, per meter, above 0 and at most 20; '$lineName' does not have one")
            continue
        }

        $entry = [ordered]@{ name = $lineName; region = $region; bone = $bone; share = [double]$share }
        if ($null -ne $line.PSObject.Properties["rise"]) {
            $entry.rise = [double]$rise
        }

        $lines.Add($entry)
    }

    if ($lines.Count -eq 0) {
        $problems.Add("lines must list the standard lines of the cage")
    }

    # The fit of every body the approved cage was fitted to, the fitSha256 of the body's export, so
    # a body whose rig, surface, or weights change, or that joins or leaves, makes the cage stale
    # until it is synced and approved again; a change of only its images does not. A record of a
    # body that left is kept for that, and Get-ArtCageBodyProblem reports it, as it reports a record
    # from before the fingerprint, which names the body's export sourceSha256 instead.
    $fittedTo = New-Object System.Collections.Generic.List[object]
    foreach ($record in @($Json.fittedTo | Where-Object { $null -ne $_ })) {
        $key = if ($null -ne $record.fitSha256) { "fitSha256" } else { "sourceSha256" }
        if ([string]$record.body -cnotmatch '^art/[A-Za-z][A-Za-z0-9_]*/[A-Za-z][A-Za-z0-9_]*$' -or
            [string]$record.$key -cnotmatch $script:ArtShaPattern -or
            @($fittedTo | Where-Object { $_.body -ceq [string]$record.body }).Count -gt 0) {
            $problems.Add("fittedTo must list bodies once each with the fitSha256 of the body's export the cage was approved on")
            break
        }

        $fittedTo.Add([ordered]@{ body = [string]$record.body; $key = [string]$record.$key })
    }

    $stages = $script:ArtReviewStages["cage"]
    $approvals = [ordered]@{}
    if ($null -ne $Json.approvals) {
        foreach ($property in $Json.approvals.PSObject.Properties) {
            $value = $property.Value
            if ($stages -notcontains $property.Name -or [string]$value.date -notmatch $script:ArtDatePattern -or
                [string]$value.sourceSha256 -cnotmatch $script:ArtShaPattern) {
                $problems.Add("approvals of a cage hold its stage $($stages -join ', ') with a date (yyyy-MM-dd) and a sourceSha256")
                continue
            }

            $approvals[$property.Name] = [ordered]@{ date = [string]$value.date; sourceSha256 = [string]$value.sourceSha256 }
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
        Kind = "cage"
        Stages = $stages
        ImplementedStages = $script:ArtImplementedStages["cage"]
        Target = $null
        TargetPath = $null
        RootObjects = [string[]]@($bodies | ForEach-Object { $_.Root })
        TriangleBudget = $null
        Skeleton = $skeleton
        SkeletonPath = $skeletonPath
        Rig = $null
        Previews = @()
        Clips = [string[]]@()
        Variants = @()
        Bodies = $bodies
        Figure = [double]$figure
        Lines = $lines.ToArray()
        FittedTo = $fittedTo.ToArray()
        Approvals = $approvals
        Export = $null
        ExportProblem = $null
    }
}

function Format-ArtShare([double]$Value) {
    # The shortest text that reads back as the same number, so no value is rounded on the way.
    return $Value.ToString("R", [System.Globalization.CultureInfo]::InvariantCulture)
}

function Write-ArtCageManifest($Manifest) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("{")
    $lines.Add('    "schemaVersion": 1,')
    $lines.Add('    "kind": "cage",')
    $bodies = (@($Manifest.Bodies) | ForEach-Object { '"' + $_.Asset + '"' }) -join ", "
    $lines.Add("    `"bodies`": [$bodies],")
    $lines.Add("    `"figure`": $(Format-ArtShare $Manifest.Figure),")
    $lines.Add('    "lines": [')
    $entries = @($Manifest.Lines)
    for ($i = 0; $i -lt $entries.Count; $i++) {
        $comma = if ($i -lt $entries.Count - 1) { "," } else { "" }
        $rise = if ($entries[$i].Contains("rise")) { ", `"rise`": $(Format-ArtShare $entries[$i].rise)" } else { "" }
        $lines.Add("        { `"name`": `"$($entries[$i].name)`", `"region`": `"$($entries[$i].region)`", `"bone`": `"$($entries[$i].bone)`", `"share`": $(Format-ArtShare $entries[$i].share)$rise }$comma")
    }
    $lines.Add('    ],')
    $fitted = @($Manifest.FittedTo)
    if ($fitted.Count -gt 0) {
        $lines.Add('    "fittedTo": [')
        for ($i = 0; $i -lt $fitted.Count; $i++) {
            $comma = if ($i -lt $fitted.Count - 1) { "," } else { "" }
            $key = if ($fitted[$i].Contains("fitSha256")) { "fitSha256" } else { "sourceSha256" }
            $lines.Add("        { `"body`": `"$($fitted[$i].body)`", `"$key`": `"$($fitted[$i].$key)`" }$comma")
        }
        $lines.Add('    ],')
    }

    Add-ArtApprovalLines $lines $Manifest
    # A cage has no export, so its approvals are the last field.
    $lines[$lines.Count - 1] = $lines[$lines.Count - 1].TrimEnd(',')
    $lines.Add("}")
    $text = ($lines -join "`n") + "`n"
    [System.IO.File]::WriteAllText($Manifest.ManifestPath, $text, (New-Object System.Text.UTF8Encoding($false)))
}

function Read-ArtTextureManifest([string]$AssetDirectory, $Json, [string]$Where, [switch]$AllowMissingSource) {
    # A garment texture: the look of one template, its image in the template's UVs, with one review
    # stage, exported as its image and its material, and with transparent parts also as its own
    # masks per body of the template. See docs/EQUIPMENT_VISUALS_DESIGN.md.
    $name = Split-Path -Leaf $AssetDirectory
    $problems = New-Object System.Collections.Generic.List[string]
    if ($name -notmatch '^[A-Za-z][A-Za-z0-9]*$') {
        $problems.Add("the folder name '$name' of a garment texture must use only letters and digits")
    }

    if ($Json.schemaVersion -ne 1) {
        $problems.Add("schemaVersion must be 1")
    }

    foreach ($field in @("target", "rootObjects", "triangleBudget", "skeleton", "clips", "rig", "previews", "variants", "part", "bodies", "cage")) {
        if ($null -ne $Json.PSObject.Properties[$field]) {
            $problems.Add("a garment texture has no $field")
        }
    }

    $template = $null
    $garmentPath = [string]$Json.garment
    if ($garmentPath -cnotmatch '^art/Garments/[A-Za-z][A-Za-z0-9]*$' -or
        -not (Test-Path -LiteralPath (Join-Path (Join-Path $script:ArtRepositoryRoot $garmentPath) "asset.json") -PathType Leaf)) {
        $problems.Add("garment must name the template the texture belongs to, art/Garments/<Name>")
    }
    else {
        try {
            $template = Read-ArtAssetManifest (Join-Path $script:ArtRepositoryRoot $garmentPath)
            if ($template.Kind -ne "garment") {
                throw "$garmentPath is a $($template.Kind) asset, not a garment template"
            }
        }
        catch {
            $problems.Add($_.Exception.Message)
            $template = $null
        }
    }

    # A texture with transparent parts has masks of its own per body of its template, next to the
    # template's models.
    $roots = if ($null -ne $template) { @($template.RootObjects) } else { @() }
    $masks = @($roots | ForEach-Object {
            $data = Get-ArtGarmentDataTarget $name $_
            [pscustomobject]@{ Body = $_; Data = $data; DataPath = Join-Path $script:ArtRepositoryRoot $data }
        })

    $stages = $script:ArtReviewStages["texture"]
    $approvals = [ordered]@{}
    if ($null -ne $Json.approvals) {
        foreach ($property in $Json.approvals.PSObject.Properties) {
            $value = $property.Value
            # A texture is reviewed on its template, so its approval records the template's file too;
            # an approval without it is stale, which Get-ArtTextureTemplateProblem reports.
            if ($stages -notcontains $property.Name -or [string]$value.date -notmatch $script:ArtDatePattern -or
                [string]$value.sourceSha256 -cnotmatch $script:ArtShaPattern -or
                ($null -ne $value.garmentSourceSha256 -and [string]$value.garmentSourceSha256 -cnotmatch $script:ArtShaPattern)) {
                $problems.Add("approvals of a garment texture hold its stage texture with a date (yyyy-MM-dd), a sourceSha256, and the garmentSourceSha256 of the template file it was reviewed on")
                continue
            }

            $approvals[$property.Name] = [ordered]@{
                date = [string]$value.date
                sourceSha256 = [string]$value.sourceSha256
                garmentSourceSha256 = [string]$value.garmentSourceSha256
            }
        }
    }

    $export = $null
    if ($null -ne $Json.export) {
        $materials = ConvertTo-ArtMaterialRecords $Json.export.materials
        $textures = ConvertTo-ArtTextureRecords $Json.export.textures $script:ArtGarmentTextureFolder
        # The masks may name other bodies than the template has now, after a body joined or left its
        # cage: they are then stale, which Verify-ArtAssets.ps1 reports, and a new export replaces them.
        $maskRecords = @($Json.export.masks | Where-Object { $null -ne $_ })
        $masksMatch = @($maskRecords | ForEach-Object { [string]$_.body } | Sort-Object -Unique).Count -eq $maskRecords.Count
        foreach ($record in $maskRecords) {
            $label = [string]$record.body
            $masksMatch = $masksMatch -and $label -cmatch $script:ArtNamePattern -and
                [string]$record.data -ceq (Get-ArtGarmentDataTarget $name $label) -and
                [string]$record.dataSha256 -cmatch $script:ArtShaPattern
        }

        # Masks are checked in the clips of every animation library of the skeleton, which may have
        # none; an opaque texture has neither masks nor clip sources.
        $clipSources = @($Json.export.clipSources | Where-Object { $null -ne $_ })
        $clipSourcesMatch = ($maskRecords.Count -gt 0 -or $clipSources.Count -eq 0) -and
            @($clipSources | Where-Object {
                    [string]$_.library -cnotmatch '^art/[A-Za-z][A-Za-z0-9_]*/[A-Za-z][A-Za-z0-9_]*$' -or
                    [string]$_.sourceSha256 -cnotmatch $script:ArtShaPattern }).Count -eq 0 -and
            @($clipSources | ForEach-Object { [string]$_.library } | Sort-Object -Unique).Count -eq $clipSources.Count
        if ([string]$Json.export.sourceSha256 -cnotmatch $script:ArtShaPattern -or [string]$Json.export.garmentSourceSha256 -cnotmatch $script:ArtShaPattern) {
            $problems.Add("export needs a sourceSha256 and the garmentSourceSha256 of its template's export")
        }
        elseif (-not $masksMatch) {
            $problems.Add("export.masks must be empty for an opaque texture, or list the masks of each body once with its body and its data ($script:ArtGarmentModelFolder/${name}_<Label>.json) and dataSha256")
        }
        elseif (-not $clipSourcesMatch) {
            $problems.Add("export.clipSources must list, for a texture with masks, every animation library its masks were checked in once, with its library (art/<Category>/<Name>) and the sourceSha256 of its export, and be empty without masks")
        }
        elseif ($null -eq $materials -or $materials.Count -ne 1 -or $materials[0].name -cne "M_Garment_$name" -or $materials[0].baseMap -cne "T_Garment_$name") {
            $problems.Add("export.materials must hold the one material M_Garment_$name with its baseMap T_Garment_$name, metallic, and smoothness, and for a dyeable texture its dyeMap T_Garment_${name}_Dye and dyeColors")
        }
        elseif ($null -eq $textures -or
            ((@($textures | ForEach-Object { $_.name }) -join ",") -cne (@("T_Garment_$name") + @($materials[0].dyeMap | Where-Object { $null -ne $_ }) -join ","))) {
            $problems.Add("export.textures must hold the image T_Garment_$name, and the dye mask T_Garment_${name}_Dye of a dyeable texture, each with its target ($script:ArtGarmentTextureFolder/<name>.png) and sha256")
        }
        else {
            $export = [ordered]@{
                sourceSha256 = [string]$Json.export.sourceSha256
                garmentSourceSha256 = [string]$Json.export.garmentSourceSha256
                masks = @($maskRecords | ForEach-Object { [ordered]@{ body = [string]$_.body; data = [string]$_.data; dataSha256 = [string]$_.dataSha256 } })
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
        Kind = "texture"
        Stages = $stages
        ImplementedStages = $script:ArtImplementedStages["texture"]
        Target = $null
        TargetPath = $null
        RootObjects = [string[]]@($template.RootObjects)
        TriangleBudget = $null
        Skeleton = $template.Skeleton
        SkeletonPath = $template.SkeletonPath
        Rig = $null
        Previews = @()
        Clips = [string[]]@()
        Variants = @()
        Garment = $template
        Masks = $masks
        Approvals = $approvals
        Export = $export
        ExportProblem = $null
    }
}

function Write-ArtTextureManifest($Manifest) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("{")
    $lines.Add('    "schemaVersion": 1,')
    $lines.Add('    "kind": "texture",')
    $lines.Add("    `"garment`": `"$($Manifest.Garment.RelativeDirectory)`",")
    Add-ArtApprovalLines $lines $Manifest
    if ($null -eq $Manifest.Export) {
        $lines.Add('    "export": null')
    }
    else {
        $lines.Add('    "export": {')
        $lines.Add("        `"sourceSha256`": `"$($Manifest.Export.sourceSha256)`",")
        $lines.Add("        `"garmentSourceSha256`": `"$($Manifest.Export.garmentSourceSha256)`",")
        # An opaque texture uses its template's masks and has none of its own.
        $masks = @($Manifest.Export.masks | Where-Object { $null -ne $_ })
        if ($masks.Count -gt 0) {
            $lines.Add('        "masks": [')
            for ($i = 0; $i -lt $masks.Count; $i++) {
                $comma = if ($i -lt $masks.Count - 1) { "," } else { "" }
                $lines.Add("            { `"body`": `"$($masks[$i].body)`", `"data`": `"$($masks[$i].data)`", `"dataSha256`": `"$($masks[$i].dataSha256)`" }$comma")
            }

            $lines.Add('        ],')
            $lines.Add('        "clipSources": [')
            $sources = @($Manifest.Export.clipSources | Where-Object { $null -ne $_ })
            for ($i = 0; $i -lt $sources.Count; $i++) {
                $comma = if ($i -lt $sources.Count - 1) { "," } else { "" }
                $lines.Add("            { `"library`": `"$($sources[$i].library)`", `"sourceSha256`": `"$($sources[$i].sourceSha256)`" }$comma")
            }

            $lines.Add('        ],')
        }

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

    if ([string]$json.kind -eq "cage") {
        return Read-ArtCageManifest $AssetDirectory $json $where -AllowMissingSource:$AllowMissingSource
    }

    if ([string]$json.kind -eq "texture") {
        return Read-ArtTextureManifest $AssetDirectory $json $where -AllowMissingSource:$AllowMissingSource
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

    # An attachment, such as a weapon or a shield, is a static asset held on a socket of a skeleton,
    # named as the socket's bone names it, such as main_hand, with one root object Grip where it is
    # held. A root Grip makes an attachment.
    $socket = $null
    if ($null -ne $json.PSObject.Properties['socket']) {
        $socket = Get-ArtSocket ([string]$json.socket)
        if ($kind -ne "static") {
            $problems.Add("only a static asset has a socket")
        }
        elseif ($null -eq $socket) {
            $problems.Add("socket must name a socket of a skeleton in art/Skeletons, such as main_hand")
        }
        else {
            $socket | Add-Member -NotePropertyName Name -NotePropertyValue ([string]$json.socket)
        }
    }

    if (($null -ne $json.PSObject.Properties['socket']) -ne (@($roots).Count -eq 1 -and [string]@($roots)[0] -ceq "Grip")) {
        $problems.Add("an attachment has a socket and one root object, Grip, where it is held, and no other asset has either")
    }

    # A tool, such as a pickaxe, is an attachment held only while a body uses it: it names the clip of
    # an animation library of its socket's skeleton that the body plays meanwhile, and a tool held in
    # both hands names where on it the other hand holds it, in the socket's axes.
    $useClip = $null
    if ($null -ne $json.PSObject.Properties['useClip']) {
        $useClip = [string]$json.useClip
        if ($null -eq $socket) {
            $problems.Add("only an attachment has a useClip")
        }
        elseif ($useClip -cnotmatch '^[A-Za-z][A-Za-z0-9_]*$' -or
            @(Get-ArtClipLibraries $socket.Skeleton | Where-Object { $_.Clips -ccontains $useClip }).Count -eq 0) {
            $problems.Add("useClip must name a clip of an animation library of the $($socket.Skeleton) skeleton, such as Mine")
        }
    }

    $offHandGrip = $null
    if ($null -ne $json.PSObject.Properties['offHandGrip']) {
        $values = @($json.offHandGrip)
        $numbers = @($values | Where-Object { $_ -is [int] -or $_ -is [long] -or $_ -is [double] -or $_ -is [decimal] })
        if ($null -eq $socket -or [string]$json.socket -cne "main_hand" -or $null -eq $useClip) {
            $problems.Add("only a tool with a useClip held on main_hand has an offHandGrip")
        }
        elseif ($values.Count -ne 3 -or $numbers.Count -ne 3 -or
            @($numbers | Where-Object { [Math]::Abs([double]$_) -gt 2.0 }).Count -gt 0) {
            $problems.Add("offHandGrip must be three numbers in meters, at most 2 m from Grip each, such as [0.0, -0.33, 0.0]")
        }
        else {
            $offHandGrip = [double[]]@($numbers | ForEach-Object { [double]$_ })
        }
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
        else {
            # A record of other clips than the manifest's, such as before a clip that was added since,
            # is stale: the next export replaces it, and the verification reports it until then.
            if ($null -eq $clipRecords) {
                $clipRecords = @()
                $exportProblem = "export.clips must list every clip of the manifest once with its name, frames, and loop; export the library again"
            }

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
        elseif ($null -ne $json.export.PSObject.Properties["dataSha256"] -and
            ([string]$json.kind -ne "skinned" -or [string]$json.export.dataSha256 -cnotmatch $script:ArtShaPattern)) {
            $problems.Add("only the export of a skinned asset with an editable body has a dataSha256, the hash of its topology $(Get-ArtDataTarget $target)")
        }
        elseif ($null -ne $json.export.PSObject.Properties["fitSha256"] -and
            ($null -eq $json.export.dataSha256 -or [string]$json.export.fitSha256 -cnotmatch $script:ArtShaPattern)) {
            $problems.Add("only the export of a skinned asset with an editable body has a fitSha256, the fingerprint of its rig, surface, and weights")
        }
        elseif ($null -ne $json.export.PSObject.Properties["paintSha256"] -and
            ($null -eq $json.export.dataSha256 -or [string]$json.export.paintSha256 -cnotmatch $script:ArtShaPattern)) {
            $problems.Add("only the export of a skinned asset with an editable body has a paintSha256, the fingerprint of what paint layers read from it")
        }
        else {
            $export = [ordered]@{
                sourceSha256 = [string]$json.export.sourceSha256
                targetSha256 = [string]$json.export.targetSha256
                dataSha256 = if ($null -ne $json.export.dataSha256) { [string]$json.export.dataSha256 } else { $null }
                # An export from before the fingerprint has none; the garment cage and garments then
                # wait for the body's next export.
                fitSha256 = if ($null -ne $json.export.fitSha256) { [string]$json.export.fitSha256 } else { $null }
                # Likewise for paint layers, which wait for the body's next export.
                paintSha256 = if ($null -ne $json.export.paintSha256) { [string]$json.export.paintSha256 } else { $null }
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
        Socket = $socket
        UseClip = $useClip
        OffHandGrip = $offHandGrip
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

    if ($Manifest.Kind -eq "cage") {
        Write-ArtCageManifest $Manifest
        return
    }

    if ($Manifest.Kind -eq "texture") {
        Write-ArtTextureManifest $Manifest
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

    if ($null -ne $Manifest.Socket) {
        $lines.Add("    `"socket`": `"$($Manifest.Socket.Name)`",")
    }

    if ($null -ne $Manifest.UseClip) {
        $lines.Add("    `"useClip`": `"$($Manifest.UseClip)`",")
    }

    if ($null -ne $Manifest.OffHandGrip) {
        $grip = (@($Manifest.OffHandGrip) | ForEach-Object { ([double]$_).ToString("0.0###", [System.Globalization.CultureInfo]::InvariantCulture) }) -join ", "
        $lines.Add("    `"offHandGrip`": [$grip],")
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
        if ($Manifest.Export.Contains("dataSha256") -and $null -ne $Manifest.Export.dataSha256) {
            $lines.Add("        `"dataSha256`": `"$($Manifest.Export.dataSha256)`",")
        }
        if ($Manifest.Export.Contains("fitSha256") -and $null -ne $Manifest.Export.fitSha256) {
            $lines.Add("        `"fitSha256`": `"$($Manifest.Export.fitSha256)`",")
        }
        if ($Manifest.Export.Contains("paintSha256") -and $null -ne $Manifest.Export.paintSha256) {
            $lines.Add("        `"paintSha256`": `"$($Manifest.Export.paintSha256)`",")
        }
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

function Get-ArtCageBodyProblem($Manifest) {
    # Returns $null when the approved cage was fitted to exactly its bodies and every body still has
    # the export the cage was approved on, otherwise the reason it does not.
    foreach ($record in @($Manifest.FittedTo)) {
        if (@($Manifest.Bodies | Where-Object { $_.Asset -ceq $record.body }).Count -eq 0) {
            return "it was approved on the body $($record.body), which it no longer has; run tools/Sync-CageAsset.ps1, review and approve it again"
        }
    }

    foreach ($body in @($Manifest.Bodies)) {
        $record = @($Manifest.FittedTo | Where-Object { $_.body -ceq $body.Asset }) | Select-Object -First 1
        $export = $null
        try {
            $export = (Read-ArtAssetManifest (Split-Path -Parent $body.ManifestPath)).Export
        }
        catch {
            # The body's own problems are reported with the body.
        }

        if ($null -eq $record) {
            return "its body $($body.Asset) joined after the cage was approved; run tools/Sync-CageAsset.ps1, review and approve it again"
        }

        if ($null -eq $export -or [string]::IsNullOrEmpty($export.fitSha256) -or -not $record.Contains("fitSha256") -or
            $export.fitSha256 -ne $record.fitSha256) {
            return "its body $($body.Asset) changed after the cage was approved, or was approved before bodies recorded their fit; run tools/Sync-CageAsset.ps1, review and approve it again"
        }
    }

    return $null
}

function Get-ArtTextureTemplateProblem($Manifest) {
    # Returns $null when the approved texture was reviewed on its template's current file, otherwise
    # the reason it was not; a texture's fit and masks depend on the template's shape, not only on
    # its UVs.
    $approval = $Manifest.Approvals["texture"]
    if ($null -ne $approval -and $approval.garmentSourceSha256 -ne (Get-ArtFileSha256 $Manifest.Garment.SourcePath)) {
        return "it was approved on another file of its template $($Manifest.Garment.RelativeDirectory); run tools/Sync-TextureAsset.ps1, review and approve it again"
    }

    return $null
}

function Get-ArtCageGateProblem($Cage) {
    # Returns $null when garment templates may be approved and exported on the cage: its stage is
    # approved for its current file, and its bodies still have the exports it was approved on.
    $problem = Get-ArtExportGateProblem $Cage (Get-ArtFileSha256 $Cage.SourcePath)
    if ($null -eq $problem) {
        $problem = Get-ArtCageBodyProblem $Cage
    }

    return $problem
}

function Get-ArtCageBodies($Manifest) {
    # The fit of the cage's bodies as an approval records it.
    return @($Manifest.Bodies | ForEach-Object {
            $export = (Read-ArtAssetManifest (Split-Path -Parent $_.ManifestPath)).Export
            if ($null -eq $export -or [string]::IsNullOrEmpty($export.fitSha256)) {
                throw "The cage cannot be approved before its body $($_.Asset) is exported with the fingerprint of its fit; export the body again."
            }

            [ordered]@{ body = $_.Asset; fitSha256 = [string]$export.fitSha256 }
        })
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
            "--garment", $Manifest.Name, "--manifest", $Manifest.ManifestPath,
            "--cage-manifest", $Manifest.Cage.ManifestPath) + (Get-ArtGarmentCoverArguments $Manifest)
        foreach ($body in @($Manifest.Bodies)) {
            $arguments += @("--body", "$($body.SourcePath)=$($body.Root)")
        }

        if ($Stage -eq "rig") {
            $arguments += Get-ArtGarmentPairArguments $Manifest
        }

        return $arguments + (Get-ArtGarmentClipArguments $Manifest)
    }

    # A texture is checked against its template's current UVs, and one with transparent parts gets
    # its masks in its template's band, checked in the clips of the skeleton's libraries.
    if ($Manifest.Kind -eq "texture") {
        return @("--kind", "texture", "--roots", ($Manifest.RootObjects -join ","), "--stage", $Stage,
            "--texture", $Manifest.Name, "--garment", $Manifest.Garment.Name, "--garment-file", $Manifest.Garment.SourcePath,
            "--dyes", [string]$Manifest.Garment.Dyes) +
            (Get-ArtGarmentCoverArguments $Manifest.Garment) + (Get-ArtGarmentClipArguments $Manifest)
    }

    # A cage is checked against the current files of its bodies and against the figure scale and
    # the lines of its asset.json.
    if ($Manifest.Kind -eq "cage") {
        $arguments = @("--kind", "cage", "--roots", ($Manifest.RootObjects -join ","), "--stage", $Stage,
            "--manifest", $Manifest.ManifestPath)
        foreach ($body in @($Manifest.Bodies)) {
            $arguments += @("--body", "$($body.SourcePath)=$($body.Root)")
        }

        return $arguments
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
