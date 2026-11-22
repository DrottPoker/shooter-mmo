# Shared helpers for the art pipeline scripts (Render-ArtPreview, Approve-ArtAsset,
# Export-ArtAsset, Verify-ArtAssets). The rules are described in docs/ART_PIPELINE.md.
# Dot-source this file; it works in Windows PowerShell 5.1 and PowerShell 7.

$script:ArtRepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
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

# An approval binds to the content of its stage: a fingerprint of the parts the stage holds, chained
# to the earlier stage's, which tools/art/blender/art_pipeline/content.py reads from the .blend file
# and records in asset.json as "content". The version of the fingerprints, which content_core.py keeps
# too, and the parts of every stage as <stage>/<part>; a part that ends in ":" stands for one part per
# item, a clip, or a body of a paint layer or the cage. See docs/ART_PIPELINE.md, Review Stages.
$script:ArtContentVersion = 1
$script:ArtContentParts = @{
    static = @("model/materials", "model/shape", "model/uv")
    skinned = @("model/materials", "model/shape", "model/uv", "rig/skeleton", "rig/weights", "animation/clip:")
    animations = @("animation/clip:", "animation/skeleton")
    paint = @("paint/body:", "paint/canvas", "paint/paint")
    garment = @("model/materials", "model/shape", "model/uv", "rig/weights")
    cage = @("model/fit:", "model/net")
    texture = @("texture/image", "texture/material", "texture/templateUv")
}

# The regions of the garment cage and the range of its figure scale in meters; see
# docs/EQUIPMENT_VISUALS_DESIGN.md.
$script:ArtCageRegions = @("neck", "trunk", "arms", "legs", "hands", "feet", "head")
$script:ArtCageSidedRegions = @("arms", "legs", "hands", "feet")
$script:ArtCageFigureRange = @(0.01, 0.2)

# A paint layer exports only images, below this folder.
$script:ArtPaintTextureFolder = "$script:ArtTextureFolder/Paint"
# A paint image is cropped to start at whole blocks of this many texels, as
# tools/art/blender/art_pipeline/paint.py does.
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
# The most a split of a template lies off its line, in meters.
$script:ArtGarmentSplitLimit = 0.05
$script:ArtTextureSizes = @(512, 1024, 2048)

# Kinds with a skeleton and animation clips.
$script:ArtAnimatedKinds = @("skinned", "animations")

$script:ArtNamePattern = '^[A-Za-z][A-Za-z0-9_]*$'
$script:ArtShaPattern = '^[0-9a-f]{64}$'
$script:ArtDatePattern = '^\d{4}-\d{2}-\d{2}$'
$script:ArtPartPattern = '^[A-Za-z][A-Za-z0-9]*(:[A-Za-z][A-Za-z0-9_]*)?$'

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

$script:ArtFileHashes = @{}

function Get-ArtFileSha256([string]$Path) {
    # A Git LFS pointer names the SHA-256 of the real content, so CI checks hashes without
    # downloading the .blend files. A file is hashed once until it changes.
    $item = Get-Item -LiteralPath $Path
    $key = "$($item.FullName)|$($item.Length)|$($item.LastWriteTimeUtc.Ticks)"
    if ($script:ArtFileHashes.ContainsKey($key)) {
        return $script:ArtFileHashes[$key]
    }

    $hash = $null
    if ($item.Length -lt 1024) {
        $text = [System.IO.File]::ReadAllText($item.FullName)
        $match = [regex]::Match($text, '^version https://git-lfs\.github\.com/spec/v1\s+oid sha256:([0-9a-f]{64})\s')
        if ($match.Success) {
            $hash = $match.Groups[1].Value
        }
    }

    if ($null -eq $hash) {
        $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    $script:ArtFileHashes[$key] = $hash
    return $hash
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
    # three channels in dyeColors. A glowing material names its emission color.
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

        $emission = $null
        if ($null -ne $record.PSObject.Properties['emission']) {
            $emission = [string]$record.emission
            if ($emission -cnotmatch '^#[0-9A-F]{6}$' -or $emission -ceq '#000000') {
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

        if ($null -ne $dyeMap) {
            $material.dyeMap = $dyeMap
            $material.dyeColors = [string[]]$dyeColors
        }

        $material.metallic = $numbers[0]
        $material.smoothness = $numbers[1]
        if ($null -ne $emission) {
            $material.emission = $emission
        }

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

function Get-ArtUpdateContentHint([string]$RelativeDirectory) {
    return "run tools/art/Update-ArtContent.ps1 -Asset $RelativeDirectory"
}

function ConvertTo-ArtContentRecord($Record) {
    # Returns the content record of asset.json, $null for a new asset whose file was never read, or
    # throws what is malformed: its version, the sourceSha256 of the file it was read from, and the
    # fingerprints of its stages and parts.
    if ($null -eq $Record) {
        return $null
    }

    $version = $Record.version
    if (-not ($version -is [int] -or $version -is [long]) -or $version -lt 1 -or
        [string]$Record.sourceSha256 -cnotmatch $script:ArtShaPattern -or $null -eq $Record.stages -or $null -eq $Record.parts) {
        throw "content must be null or hold its version, the sourceSha256 of the file it was read from, and the fingerprints of its stages and parts"
    }

    $stages = [ordered]@{}
    foreach ($property in $Record.stages.PSObject.Properties) {
        if ([string]$property.Value -cnotmatch $script:ArtShaPattern) {
            throw "content.stages.$($property.Name) must be a SHA-256"
        }

        $stages[$property.Name] = [string]$property.Value
    }

    $parts = [ordered]@{}
    foreach ($property in $Record.parts.PSObject.Properties) {
        if ($property.Name -cnotmatch $script:ArtPartPattern -or [string]$property.Value -cnotmatch $script:ArtShaPattern) {
            throw "content.parts must name each part, such as shape or clip:Idle, with a SHA-256; '$($property.Name)' does not"
        }

        $parts[$property.Name] = [string]$property.Value
    }

    return [ordered]@{ version = [int]$version; sourceSha256 = [string]$Record.sourceSha256; stages = $stages; parts = $parts }
}

function Read-ArtApprovals($Json, [string[]]$Stages, [string]$Kind, [string]$RelativeDirectory, $Problems) {
    # The approvals of an asset's review stages, each with its date and the contentSha256 of its
    # stage, the first stages in order. An approval from before approvals recorded the content of
    # their stage names the sourceSha256 of a file instead; the stage is approved again.
    $approvals = [ordered]@{}
    if ($null -eq $Json.approvals) {
        return $approvals
    }

    foreach ($property in $Json.approvals.PSObject.Properties) {
        $value = $property.Value
        if ($null -ne $value -and $value -isnot [string] -and
            ($null -ne $value.PSObject.Properties["sourceSha256"] -or $null -ne $value.PSObject.Properties["garmentSourceSha256"])) {
            $Problems.Add("approvals.$($property.Name) names the hash of a file (sourceSha256): this asset.json is from before approvals recorded the content of their stage; remove approvals.$($property.Name), review the asset, and approve the stage again with tools/art/Approve-ArtAsset.ps1 -Asset $RelativeDirectory -Stage $($property.Name)")
            continue
        }

        if ($null -ne $Stages -and $Stages -notcontains $property.Name) {
            $Problems.Add("approvals.$($property.Name) is not a review stage of a $Kind asset")
            continue
        }

        if ([string]$value.date -notmatch $script:ArtDatePattern -or [string]$value.contentSha256 -cnotmatch $script:ArtShaPattern) {
            $Problems.Add("approvals.$($property.Name) needs a date (yyyy-MM-dd) and the contentSha256 of its stage")
            continue
        }

        $approvals[$property.Name] = [ordered]@{ date = [string]$value.date; contentSha256 = [string]$value.contentSha256 }
    }

    # Stages are approved in order, so the approvals are always the first stages of the kind.
    if ($null -ne $Stages) {
        $approvedPrefix = @($Stages | Select-Object -First $approvals.Count)
        if (@($approvals.Keys | Where-Object { $approvedPrefix -notcontains $_ }).Count -gt 0) {
            $Problems.Add("approvals must cover the first review stages in order: $($Stages -join ', ')")
        }
    }

    return $approvals
}

function Get-ArtOldExportProblem($Export, [string]$RelativeDirectory) {
    # Returns $null unless an export record names the sourceSha256 of .blend files, as records did
    # before they named the content of the exported stages, otherwise the reason it is stale.
    $old = @("sourceSha256", "rigSourceSha256", "garmentSourceSha256", "cageSourceSha256" |
            Where-Object { $null -ne $Export.PSObject.Properties[$_] })
    $old += @(@($Export.clipSources) + @($Export.pairSources) | Where-Object { $null -ne $_ -and $null -ne $_.PSObject.Properties["sourceSha256"] } |
            Select-Object -First 1 | ForEach-Object { "sourceSha256" })
    if ($old.Count -gt 0) {
        return "export names the hash of files ($($old[0])): this asset.json is from before exports recorded the content of their sources; set export to null and export the asset again with tools/art/Export-ArtAsset.ps1 -Asset $RelativeDirectory"
    }

    return $null
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
    # the Humanoid skeleton, or $null when no skeleton definition has it. Where several skeletons
    # have the socket, it is the first by the ordinal order of the file names, as Unity's
    # SocketBones lists them; a culture's order differs between Linux and Windows.
    $names = [string[]]@(Get-ChildItem -LiteralPath $script:ArtSkeletonFolder -Filter "*.json" -File | ForEach-Object { $_.Name })
    [Array]::Sort($names, [StringComparer]::Ordinal)
    foreach ($name in $names) {
        $json = Get-Content -Raw -LiteralPath (Join-Path $script:ArtSkeletonFolder $name) | ConvertFrom-Json
        foreach ($bone in @($json.bones)) {
            if ($null -ne $bone.socket -and [string]$bone.socket -ceq $Socket -and -not $bone.deform) {
                return [pscustomobject]@{ Skeleton = [string]$json.name; Bone = [string]$bone.name }
            }
        }
    }

    return $null
}

function Get-ArtHeldLibrary([string]$Skeleton, [string]$HoldClip) {
    # The animation library of a skeleton whose bodies hold an attachment in its review: the first
    # with the clip Idle, and the attachment's hold clip when it names one, with its armature, or
    # $null without one.
    foreach ($library in @(Get-ArtClipLibraries $Skeleton | Sort-Object Asset)) {
        if ($library.Clips -ccontains "Idle" -and ([string]::IsNullOrEmpty($HoldClip) -or $library.Clips -ccontains $HoldClip)) {
            $json = Get-Content -Raw -LiteralPath (Join-Path $script:ArtRepositoryRoot "$($library.Asset)/asset.json") | ConvertFrom-Json
            return [pscustomobject]@{ SourcePath = $library.SourcePath; Root = [string]@($json.rootObjects)[0] }
        }
    }

    return $null
}

function Get-ArtHeldAttachmentArguments($Manifest) {
    # The --held-attachment arguments of an animation library: each tool held in the clip it is used
    # with, and each weapon in its hold and attack clips, when the library has the clip.
    $arguments = @()
    foreach ($directory in @(Get-ArtAssetDirectories)) {
        $json = Get-Content -Raw -LiteralPath (Join-Path $directory "asset.json") | ConvertFrom-Json
        $named = @("useClip", "holdClip", "attackClip" | Where-Object {
                $null -ne $json.PSObject.Properties[$_] -and $Manifest.Clips -ccontains [string]$json.$_ })
        if ($named.Count -eq 0) {
            continue
        }

        $held = Read-ArtAssetManifest $directory
        if ($held.Socket.Skeleton -ceq $Manifest.Skeleton) {
            foreach ($clip in @($held.UseClip, $held.HoldClip, $held.AttackClip | Where-Object { $null -ne $_ })) {
                $arguments += @("--held-attachment", "$($held.SourcePath)=${clip}:$($held.Socket.Bone)")
            }
        }
    }

    return $arguments
}

function Format-ArtMotion($Motion) {
    # The motion numbers of one clip, an ART_MOTION record, as one line for a person.
    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    $fastest = if ($null -ne $Motion.fastestBone) {
        "fastest $($Motion.fastestBone) at $(([double]$Motion.fastestSpeed).ToString('0.0', $culture)) m/s"
    }
    else {
        "no bone moves"
    }

    $line = "$($Motion.clip): $(([double]$Motion.seconds).ToString('0.00', $culture)) s; " +
        "root travels $(([double]$Motion.rootTravel).ToString('0.00', $culture)) m and sinks to $(([double]$Motion.rootLowest * 100).ToString('0', $culture)) %; " +
        "$fastest; $(([double]$Motion.movingShare * 100).ToString('0', $culture)) % of the joints turn; " +
        "lowest point $(([double]$Motion.lowestPoint).ToString('0.000', $culture)) m"
    if ([double]$Motion.loopGap -gt 0.0) {
        $line += "; the loop jumps $(([double]$Motion.loopGap).ToString('0.000', $culture)) m"
    }

    return $line
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

    $relativeDirectory = Get-ArtRelativePath $AssetDirectory
    $content = $null
    try {
        $content = ConvertTo-ArtContentRecord $Json.content
    }
    catch {
        $problems.Add($_.Exception.Message)
    }

    $approvals = Read-ArtApprovals $Json $script:ArtReviewStages["paint"] "paint" $relativeDirectory $problems
    $export = $null
    $exportProblem = $null
    $oldExport = if ($null -ne $Json.export) { Get-ArtOldExportProblem $Json.export $relativeDirectory } else { $null }
    if ($null -ne $oldExport) {
        $problems.Add($oldExport)
    }
    elseif ($null -ne $Json.export) {
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
        $cageContent = $Json.export.cageContentSha256
        $throughCage = @($textures | Where-Object { $_.move -ceq "cage" }).Count -gt 0
        if ([string]$Json.export.contentSha256 -cnotmatch $script:ArtShaPattern) {
            $problems.Add("export needs the contentSha256 of the exported stage")
        }
        elseif ($null -eq $textures -or ((@($textures | ForEach-Object { $_.name }) | Sort-Object) -join ",") -cne $expected) {
            $problems.Add("export.textures must list T_Paint_${name}_<Label> of every body once with its target ($script:ArtPaintTextureFolder/<name>.png), sha256, layout, rect starting at whole blocks of $script:ArtPaintCropBlock texels, move (topology or cage), and bodyPaintSha256")
        }
        elseif (-not (Test-ArtNumber $smoothness) -or $smoothness -lt 0 -or $smoothness -gt 1 -or
            -not (Test-ArtNumber $metallic) -or $metallic -lt 0 -or $metallic -gt 1) {
            $problems.Add("export needs the smoothness and metallic of the paint, from 0 to 1")
        }
        elseif (($throughCage -and [string]$cageContent -cnotmatch $script:ArtShaPattern) -or (-not $throughCage -and $null -ne $cageContent)) {
            $problems.Add("export has the cageContentSha256 of the garment cage's content exactly when a body was moved through the cage")
        }
        else {
            $export = [ordered]@{
                contentSha256 = [string]$Json.export.contentSha256
                cageContentSha256 = if ($throughCage) { [string]$cageContent } else { $null }
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
        Content = $content
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
        if ($material.Contains("emission")) {
            $Lines.Add("                `"smoothness`": $(Format-ArtNumber $material.smoothness),")
            $Lines.Add("                `"emission`": `"$($material.emission)`"")
        }
        else {
            $Lines.Add("                `"smoothness`": $(Format-ArtNumber $material.smoothness)")
        }

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

function Add-ArtContentLines($Lines, $Manifest) {
    # The content record, before the approvals that bind to it.
    $content = $Manifest.Content
    if ($null -eq $content) {
        $Lines.Add('    "content": null,')
        return
    }

    $Lines.Add('    "content": {')
    $Lines.Add("        `"version`": $($content.version),")
    $Lines.Add("        `"sourceSha256`": `"$($content.sourceSha256)`",")
    foreach ($field in @("stages", "parts")) {
        $names = @($content[$field].Keys)
        $Lines.Add("        `"$field`": {")
        for ($i = 0; $i -lt $names.Count; $i++) {
            $comma = if ($i -lt $names.Count - 1) { "," } else { "" }
            $Lines.Add("            `"$($names[$i])`": `"$($content[$field][$names[$i]])`"$comma")
        }

        $Lines.Add("        }$(if ($field -eq 'stages') { ',' } else { '' })")
    }

    $Lines.Add('    },')
}

function Add-ArtApprovalLines($Lines, $Manifest) {
    Add-ArtContentLines $Lines $Manifest
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
        $Lines.Add("            `"contentSha256`": `"$($approval.contentSha256)`"")
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
    Add-ArtApprovalLines $lines $Manifest
    if ($null -eq $Manifest.Export) {
        $lines.Add('    "export": null')
    }
    else {
        $lines.Add('    "export": {')
        $lines.Add("        `"contentSha256`": `"$($Manifest.Export.contentSha256)`",")
        if (-not [string]::IsNullOrEmpty($Manifest.Export.cageContentSha256)) {
            $lines.Add("        `"cageContentSha256`": `"$($Manifest.Export.cageContentSha256)`",")
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
            [string]$_.Asset + "=" + $(if ($null -ne $_.Export) { [string]$_.Export.contentSha256 } else { "" })
        } | Sort-Object)
    $recorded = @($Manifest.Export.clipSources | Where-Object { $null -ne $_ } | ForEach-Object { "$($_.library)=$($_.contentSha256)" } | Sort-Object)
    if (($libraries -join ",") -cne ($recorded -join ",")) {
        return "was checked in other clips than the current exports of the $($Manifest.Skeleton) animation libraries; run $Sync, review and approve it, then run tools/art/Export-ArtAsset.ps1"
    }

    return $null
}

function Get-ArtPaintDependencyProblems($Manifest) {
    # The reasons the export of a paint layer no longer matches what it was moved to: the exports of
    # its bodies, and the content of the cage when a body was moved through it. None when it matches.
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
            $problems.Add("was moved to another export of its body $($body.Asset); run tools/art/Sync-PaintAsset.ps1, review and approve it, then run tools/art/Export-ArtAsset.ps1")
        }
    }

    if (-not [string]::IsNullOrEmpty($Manifest.Export.cageContentSha256)) {
        $cage = $null
        try {
            $cage = @(Get-ArtPaintCage $Manifest) | Select-Object -First 1
        }
        catch {
            # The cage's own problems are reported with the cage.
        }

        $cageProblem = if ($null -ne $cage) { Get-ArtContentProblem $cage } else { $null }
        if ($null -ne $cageProblem) {
            $problems.Add("its garment cage $($cage.RelativeDirectory) cannot be compared: $cageProblem")
        }
        elseif ($null -eq $cage -or (Get-ArtCageContent $cage) -cne $Manifest.Export.cageContentSha256) {
            $problems.Add("was moved through other content of the garment cage; run tools/art/Sync-PaintAsset.ps1, review and approve it, then run tools/art/Export-ArtAsset.ps1")
        }
    }

    return $problems.ToArray()
}

function Get-ArtPaintCage($Manifest) {
    # The manifests of the garment cages whose bodies hold every body of a paint layer.
    $assets = @($Manifest.Bodies | ForEach-Object { $_.Asset })
    foreach ($directory in @(Get-ArtAssetDirectories)) {
        $json = Get-Content -Raw -LiteralPath (Join-Path $directory "asset.json") | ConvertFrom-Json
        if ([string]$json.kind -ne "cage") {
            continue
        }

        $bodies = @($json.bodies | ForEach-Object { [string]$_ })
        if (@($assets | Where-Object { $bodies -cnotcontains $_ }).Count -eq 0) {
            Read-ArtAssetManifest $directory
        }
    }
}

function Get-ArtCageContent($Cage) {
    # The content fingerprint of the garment cage, its one stage, which garment templates are fitted
    # to and paint layers moved through. Throws when the cage's content record is not current.
    $problem = Get-ArtContentProblem $Cage
    if ($null -ne $problem) {
        throw "The content of the garment cage $($Cage.RelativeDirectory) is not recorded for its file: $problem."
    }

    return [string]$Cage.Content.stages["model"]
}

function Get-ArtGarmentDependencyProblems($Manifest) {
    # The reasons the export of a garment template no longer matches what it was made from: the
    # content of its cage, the bodies of the cage and their exports, and the exports of the
    # animation libraries it was checked in. None when it matches.
    $problems = New-Object System.Collections.Generic.List[string]
    $cageProblem = Get-ArtContentProblem $Manifest.Cage
    if ($null -ne $cageProblem) {
        $problems.Add("its cage $($Manifest.Cage.RelativeDirectory) cannot be compared: $cageProblem")
    }
    elseif ((Get-ArtCageContent $Manifest.Cage) -cne $Manifest.Export.cageContentSha256) {
        $problems.Add("was fitted to other content of its cage $($Manifest.Cage.RelativeDirectory); run tools/art/Sync-GarmentAsset.ps1, review and approve it, then run tools/art/Export-ArtAsset.ps1")
    }

    $exported = @($Manifest.Export.models | ForEach-Object { [string]$_.body } | Sort-Object)
    $current = @($Manifest.Models | ForEach-Object { $_.Body } | Sort-Object)
    if (($exported -join ",") -cne ($current -join ",")) {
        $problems.Add("was exported for other bodies ($($exported -join ', ')) than its cage has ($($current -join ', ')); run tools/art/Sync-GarmentAsset.ps1, review and approve it, then run tools/art/Export-ArtAsset.ps1")
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
            $problems.Add("was fitted to another rig, surface, or weights of its body $($body.BodyAsset); run tools/art/Sync-GarmentAsset.ps1, review and approve it if it changed, then run tools/art/Export-ArtAsset.ps1")
        }
    }

    $clipProblem = Get-ArtClipSourcesProblem $Manifest "tools/art/Sync-GarmentAsset.ps1"
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

function Get-ArtPairSourcesProblem($Manifest, $Template, [string]$Sync) {
    # Returns $null when the export of a template, or of a texture's masks on its template, was
    # checked over the current exports of every template of another visual slot and a lower layer of
    # the template's cage, otherwise the reason it was not.
    if ($null -eq $Template) {
        $Template = $Manifest
    }

    if ([string]::IsNullOrEmpty($Sync)) {
        $Sync = "tools/art/Sync-GarmentAsset.ps1"
    }

    $current = @(Get-ArtGarmentPairs $Template | ForEach-Object { "$($_.RelativeDirectory)=$($_.Export.contentSha256)" } | Sort-Object)
    $recorded = @($Manifest.Export.pairSources | Where-Object { $null -ne $_ } | ForEach-Object { "$($_.garment)=$($_.contentSha256)" } | Sort-Object)
    if (($current -join ",") -cne ($recorded -join ",")) {
        return "was checked over other templates of a lower layer than the current exports of its cage's templates; run $Sync, review it, then run tools/art/Export-ArtAsset.ps1"
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

function Get-ArtGarmentSplitProblem($Recipe, $Cage) {
    # Returns $null when every split of a recipe is valid, otherwise what is wrong with the first
    # wrong one, naming the entry and the key: a line of the cage in the template's regions, once per
    # place, and "toward" with "offset" together when it lies off its line.
    $regions = @($Recipe.regions | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
    $places = New-Object System.Collections.Generic.HashSet[string]
    $splits = @($Recipe.splits | Where-Object { $null -ne $_ })
    for ($i = 0; $i -lt $splits.Count; $i++) {
        $split = $splits[$i]
        $where = "recipe.splits entry $($i + 1) (line $([string]$split.line))"
        $names = if ($split -is [System.Collections.IDictionary]) { @($split.Keys) } else { @($split.PSObject.Properties.Name) }
        $unknown = @($names | Where-Object { @("line", "toward", "offset") -cnotcontains $_ }) | Select-Object -First 1
        if ($null -ne $unknown) {
            return "$where has the key ""$unknown""; a split has ""line"", and ""toward"" and ""offset"" together when it lies off its line"
        }

        $line = @($Cage.Lines | Where-Object { $_.name -ceq [string]$split.line }) | Select-Object -First 1
        if ($null -eq $line -or $regions -cnotcontains $line.region) {
            return "$where has a ""line"" that is not a line of the cage in the template's regions ($($regions -join ', '))"
        }

        if (($names -ccontains "toward") -ne ($names -ccontains "offset")) {
            $has, $lacks = if ($names -ccontains "toward") { "toward", "offset" } else { "offset", "toward" }
            return "$where has ""$has"" without ""$lacks""; ""toward"" and ""offset"" come together: a split off its line names the side of the line's bone it lies toward (head or tail) and how far, above 0 and up to $script:ArtGarmentSplitLimit m"
        }

        if ($names -ccontains "toward" -and $script:ArtGarmentKeeps -cnotcontains [string]$split.toward) {
            return "$where has the key ""toward"" set to ""$([string]$split.toward)""; it is head or tail"
        }

        if ($names -ccontains "offset" -and (-not (Test-ArtNumber $split.offset) -or $split.offset -le 0 -or $split.offset -gt $script:ArtGarmentSplitLimit)) {
            return "$where has the key ""offset"" set to $($split.offset); it is above 0 and up to $script:ArtGarmentSplitLimit m"
        }

        $place = if ($names -ccontains "offset") { "$($split.line):$($split.toward):$([double]$split.offset)" } else { "$($split.line)" }
        if (-not $places.Add($place)) {
            return "$where repeats a split at the same place; each place gets one edge loop"
        }
    }

    return $null
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

    # Edge loops at standard lines where the template is not cut, each optionally moved by an offset
    # toward the head or the tail of the line's bone, such as just beyond the hide margin inside a
    # top's hem, so a template of a higher layer that ends there hides it face by face.
    if ($null -ne (Get-ArtGarmentSplitProblem $Recipe $Cage)) {
        return $null
    }

    $splits = New-Object System.Collections.Generic.List[object]
    foreach ($split in @($Recipe.splits | Where-Object { $null -ne $_ })) {
        $entry = [ordered]@{ line = [string]$split.line }
        if ($null -ne $split.offset) {
            $entry.toward = [string]$split.toward
            $entry.offset = [double]$split.offset
        }

        $splits.Add($entry)
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
    # width, both shares of how far the shoulder joints lie from the middle. A part may end at a line
    # of the legs, its hem, instead of at the tips of its chains.
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

        if ($names -ccontains "to") {
            $to = @($Cage.Lines | Where-Object { $_.name -ceq [string]$entry.to }) | Select-Object -First 1
            if ($null -eq $to -or $to.region -cne "legs") {
                return $null
            }

            $part["to"] = [string]$entry.to
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
    if ($splits.Count -gt 0) {
        $result.splits = $splits.ToArray()
    }

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
    $splitProblem = if ($null -ne $cage -and $null -eq $recipe -and $Json.recipe -isnot [string] -and $null -ne $Json.recipe) {
        Get-ArtGarmentSplitProblem $Json.recipe $cage
    }
    if ($null -ne $splitProblem) {
        $problems.Add($splitProblem)
    }
    elseif ($null -ne $cage -and $null -eq $recipe) {
        $problems.Add("recipe must be ""modeled"" or name the regions of the cage the template covers ($($script:ArtCageRegions -join ', ')), its cuts, each a line of the cage in those regions with the side it keeps (head or tail), optionally its splits, each a line of those regions at which it gets an edge loop, once per place, optionally moved by an offset above 0 and up to $script:ArtGarmentSplitLimit m toward the head or the tail of the line's bone, optionally what it lies over, each a line of those regions with the side the inner garment keeps and the inner garment's band, optionally its loose parts, each a line of the trunk it hangs from, its side ($($script:ArtGarmentHangSides -join ', ')), its chains ($($script:ArtGarmentHangChains -join ', ')), its flare from 0 to $script:ArtGarmentFlareLimit m, across the back optionally its top and its width, no smaller than its top, both above 0 and up to $script:ArtGarmentHangShareLimit of the shoulders' half width, and optionally the line of the legs it ends at (to), its ease from 0 to $script:ArtGarmentEaseLimit m, and its hem from 0 to $script:ArtGarmentHemLimit m")
    }

    # The chains a loose part hangs on move only with the cloth profile that activates them.
    foreach ($part in @($(if ($recipe -is [System.Collections.IDictionary]) { $recipe["hang"] }) | Where-Object { $null -ne $_ })) {
        $clothOfChains = $script:ArtGarmentHangCloth[[string]$part.chains]
        if ([string]$cloth -cne $clothOfChains) {
            $problems.Add("cloth must be ""$clothOfChains"" for a template whose loose parts hang on the $($part.chains) chains")
        }
    }

    $stages = $script:ArtReviewStages["garment"]
    $relativeDirectory = Get-ArtRelativePath $AssetDirectory
    $content = $null
    try {
        $content = ConvertTo-ArtContentRecord $Json.content
    }
    catch {
        $problems.Add($_.Exception.Message)
    }

    $approvals = Read-ArtApprovals $Json $stages "garment" $relativeDirectory $problems
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
    $oldExport = if ($null -ne $Json.export) { Get-ArtOldExportProblem $Json.export $relativeDirectory } else { $null }
    if ($null -ne $oldExport) {
        $problems.Add($oldExport)
    }
    elseif ($null -ne $Json.export) {
        $materials = ConvertTo-ArtMaterialRecords $Json.export.materials
        $textures = ConvertTo-ArtTextureRecords $Json.export.textures $script:ArtGarmentTextureFolder
        $records = @($Json.export.models | Where-Object { $null -ne $_ })
        $cageContentSha256 = [string]$Json.export.cageContentSha256
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
                [string]$_.contentSha256 -cnotmatch $script:ArtShaPattern }).Count -eq 0 -and
            @($clipSources | ForEach-Object { [string]$_.library } | Sort-Object -Unique).Count -eq $clipSources.Count
        # The templates of a lower layer it was checked over, with the contentSha256 of their exports.
        $pairSources = @($Json.export.pairSources | Where-Object { $null -ne $_ })
        $pairSourcesMatch = @($pairSources | Where-Object {
                [string]$_.garment -cnotmatch '^art/Garments/[A-Za-z][A-Za-z0-9]*$' -or
                [string]$_.contentSha256 -cnotmatch $script:ArtShaPattern }).Count -eq 0 -and
            @($pairSources | ForEach-Object { [string]$_.garment } | Sort-Object -Unique).Count -eq $pairSources.Count

        $baseMaps = (@($materials | Where-Object { $null -ne $_ -and $_.Contains("baseMap") } | ForEach-Object { $_.baseMap }) | Sort-Object) -join ","
        if ([string]$Json.export.contentSha256 -cnotmatch $script:ArtShaPattern -or $cageContentSha256 -cnotmatch $script:ArtShaPattern) {
            $problems.Add("export needs the contentSha256 of the exported stage and the cageContentSha256 of the cage content it was fitted to")
        }
        elseif (-not $modelsMatch) {
            $problems.Add("export.models must list the model of each body once with its body, the bodyFitSha256 of the body's export, its target ($script:ArtGarmentModelFolder/${name}_<Label>.fbx) and sha256, and its data ($script:ArtGarmentModelFolder/${name}_<Label>.json) and dataSha256")
        }
        elseif (-not $clipSourcesMatch) {
            $problems.Add("export.clipSources must list every animation library the garment was checked in once, with its library (art/<Category>/<Name>) and the contentSha256 of its export")
        }
        elseif (-not $pairSourcesMatch) {
            $problems.Add("export.pairSources must list every template of a lower layer the garment was checked over once, with its garment (art/Garments/<Name>) and the contentSha256 of its export")
        }
        elseif ($null -eq $materials -or @($materials | Where-Object { $_.name -cne "M_Garment_$name" }).Count -gt 0) {
            $problems.Add("export.materials must hold the one material M_Garment_$name with its baseColor (#RRGGBB), metallic, and smoothness")
        }
        elseif ($baseMaps -ne "" -or $null -eq $textures -or $textures.Count -gt 0) {
            $problems.Add("export.textures must be empty; a template's look comes from its garment textures")
        }
        else {
            $export = [ordered]@{
                contentSha256 = [string]$Json.export.contentSha256
                cageContentSha256 = $cageContentSha256
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
                clipSources = @($clipSources | ForEach-Object { [ordered]@{ library = [string]$_.library; contentSha256 = [string]$_.contentSha256 } })
                pairSources = @($pairSources | ForEach-Object { [ordered]@{ garment = [string]$_.garment; contentSha256 = [string]$_.contentSha256 } })
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
        RelativeDirectory = $relativeDirectory
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
        Content = $content
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

    $splits = @($Recipe.splits | Where-Object { $null -ne $_ })
    if ($splits.Count -gt 0) {
        $Lines.Add('        "splits": [')
        for ($i = 0; $i -lt $splits.Count; $i++) {
            $comma = if ($i -lt $splits.Count - 1) { "," } else { "" }
            $offset = if ($null -ne $splits[$i].offset) { ", `"toward`": `"$($splits[$i].toward)`", `"offset`": $(Format-ArtShare $splits[$i].offset)" } else { "" }
            $Lines.Add("            { `"line`": `"$($splits[$i].line)`"$offset }$comma")
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
            $to = if ($null -ne $hang[$i].to) { ", `"to`": `"$($hang[$i].to)`"" } else { "" }
            $Lines.Add("            { `"line`": `"$($hang[$i].line)`", `"side`": `"$($hang[$i].side)`", `"chains`": `"$($hang[$i].chains)`", `"flare`": $(Format-ArtShare $hang[$i].flare)$($shares -join '')$to }$comma")
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
        $lines.Add("        `"contentSha256`": `"$($Manifest.Export.contentSha256)`",")
        $lines.Add("        `"cageContentSha256`": `"$($Manifest.Export.cageContentSha256)`",")
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
            $lines.Add("            { `"library`": `"$($sources[$i].library)`", `"contentSha256`": `"$($sources[$i].contentSha256)`" }$comma")
        }
        $lines.Add('        ],')
        $pairs = @($Manifest.Export.pairSources | Where-Object { $null -ne $_ })
        if ($pairs.Count -gt 0) {
            $lines.Add('        "pairSources": [')
            for ($i = 0; $i -lt $pairs.Count; $i++) {
                $comma = if ($i -lt $pairs.Count - 1) { "," } else { "" }
                $lines.Add("            { `"garment`": `"$($pairs[$i].garment)`", `"contentSha256`": `"$($pairs[$i].contentSha256)`" }$comma")
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
    # its approved content. See docs/EQUIPMENT_VISUALS_DESIGN.md.
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
    $content = $null
    try {
        $content = ConvertTo-ArtContentRecord $Json.content
    }
    catch {
        $problems.Add($_.Exception.Message)
    }

    $approvals = Read-ArtApprovals $Json $stages "cage" (Get-ArtRelativePath $AssetDirectory) $problems
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
        Content = $content
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

    # A texture is reviewed on its template; its content records the template's UVs it is painted in,
    # which Get-ArtTextureTemplateProblem compares with the template's content.
    $stages = $script:ArtReviewStages["texture"]
    $relativeDirectory = Get-ArtRelativePath $AssetDirectory
    $content = $null
    try {
        $content = ConvertTo-ArtContentRecord $Json.content
    }
    catch {
        $problems.Add($_.Exception.Message)
    }

    $approvals = Read-ArtApprovals $Json $stages "texture" $relativeDirectory $problems
    $export = $null
    $oldExport = if ($null -ne $Json.export) { Get-ArtOldExportProblem $Json.export $relativeDirectory } else { $null }
    if ($null -ne $oldExport) {
        $problems.Add($oldExport)
    }
    elseif ($null -ne $Json.export) {
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
                    [string]$_.contentSha256 -cnotmatch $script:ArtShaPattern }).Count -eq 0 -and
            @($clipSources | ForEach-Object { [string]$_.library } | Sort-Object -Unique).Count -eq $clipSources.Count
        # Masks name the faces they hide of the templates of lower layers worn under the template,
        # with the contentSha256 of the exports they were made with.
        $pairSources = @($Json.export.pairSources | Where-Object { $null -ne $_ })
        $pairSourcesMatch = ($maskRecords.Count -gt 0 -or $pairSources.Count -eq 0) -and
            @($pairSources | Where-Object {
                    [string]$_.garment -cnotmatch '^art/Garments/[A-Za-z][A-Za-z0-9]*$' -or
                    [string]$_.contentSha256 -cnotmatch $script:ArtShaPattern }).Count -eq 0 -and
            @($pairSources | ForEach-Object { [string]$_.garment } | Sort-Object -Unique).Count -eq $pairSources.Count
        if ([string]$Json.export.contentSha256 -cnotmatch $script:ArtShaPattern -or [string]$Json.export.garmentContentSha256 -cnotmatch $script:ArtShaPattern) {
            $problems.Add("export needs the contentSha256 of the exported stage and the garmentContentSha256 of its template's export")
        }
        elseif (-not $masksMatch) {
            $problems.Add("export.masks must be empty for an opaque texture, or list the masks of each body once with its body and its data ($script:ArtGarmentModelFolder/${name}_<Label>.json) and dataSha256")
        }
        elseif (-not $clipSourcesMatch) {
            $problems.Add("export.clipSources must list, for a texture with masks, every animation library its masks were checked in once, with its library (art/<Category>/<Name>) and the contentSha256 of its export, and be empty without masks")
        }
        elseif (-not $pairSourcesMatch) {
            $problems.Add("export.pairSources must list, for a texture with masks, every template of a lower layer its masks hide faces of once, with its garment (art/Garments/<Name>) and the contentSha256 of its export, and be empty without masks")
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
                contentSha256 = [string]$Json.export.contentSha256
                garmentContentSha256 = [string]$Json.export.garmentContentSha256
                masks = @($maskRecords | ForEach-Object { [ordered]@{ body = [string]$_.body; data = [string]$_.data; dataSha256 = [string]$_.dataSha256 } })
                clipSources = @($clipSources | ForEach-Object { [ordered]@{ library = [string]$_.library; contentSha256 = [string]$_.contentSha256 } })
                pairSources = @($pairSources | ForEach-Object { [ordered]@{ garment = [string]$_.garment; contentSha256 = [string]$_.contentSha256 } })
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
        RelativeDirectory = $relativeDirectory
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
        Content = $content
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
        $lines.Add("        `"contentSha256`": `"$($Manifest.Export.contentSha256)`",")
        $lines.Add("        `"garmentContentSha256`": `"$($Manifest.Export.garmentContentSha256)`",")
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
                $lines.Add("            { `"library`": `"$($sources[$i].library)`", `"contentSha256`": `"$($sources[$i].contentSha256)`" }$comma")
            }

            $lines.Add('        ],')
            $pairs = @($Manifest.Export.pairSources | Where-Object { $null -ne $_ })
            if ($pairs.Count -gt 0) {
                $lines.Add('        "pairSources": [')
                for ($i = 0; $i -lt $pairs.Count; $i++) {
                    $comma = if ($i -lt $pairs.Count - 1) { "," } else { "" }
                    $lines.Add("            { `"garment`": `"$($pairs[$i].garment)`", `"contentSha256`": `"$($pairs[$i].contentSha256)`" }$comma")
                }

                $lines.Add('        ],')
            }
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
    # held. A root Grip makes an attachment. An attachment with moving parts of its own, such as a
    # bow's limbs and string, is a skinned asset whose rig makes Grip its armature.
    $socket = $null
    if ($null -ne $json.PSObject.Properties['socket']) {
        $socket = Get-ArtSocket ([string]$json.socket)
        if (@("static", "skinned") -cnotcontains $kind) {
            $problems.Add("only a static or skinned asset has a socket")
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
    # both hands names where on it the other hand holds it, in the socket's axes. So does a weapon that
    # its attack takes in both hands, such as a greataxe.
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
        if ($null -eq $socket -or [string]$json.socket -cne "main_hand" -or
            ($null -eq $useClip -and $null -eq $json.PSObject.Properties['attackClip'])) {
            $problems.Add("only a tool with a useClip or a weapon with an attackClip, held on main_hand, has an offHandGrip")
        }
        elseif ($values.Count -ne 3 -or $numbers.Count -ne 3 -or
            @($numbers | Where-Object { [Math]::Abs([double]$_) -gt 2.0 }).Count -gt 0) {
            $problems.Add("offHandGrip must be three numbers in meters, at most 2 m from Grip each, such as [0.0, -0.33, 0.0]")
        }
        else {
            $offHandGrip = [double[]]@($numbers | ForEach-Object { [double]$_ })
        }
    }

    # A weapon held in a hand, on main_hand or, like a bow in the left hand, on off_hand, may name the
    # pose its arm holds it in, such as HoldStaff, and the clip its body plays once whenever its
    # attack starts, such as Cast, both clips of an animation library of its socket's skeleton. A
    # weapon with an attack clip may name where on it the projectile of its attack leaves, in the
    # socket's axes.
    $weaponClips = @{}
    foreach ($field in @("holdClip", "attackClip")) {
        $weaponClips[$field] = $null
        if ($null -eq $json.PSObject.Properties[$field]) {
            continue
        }

        $clip = [string]$json.$field
        if ($null -eq $socket -or @("main_hand", "off_hand") -cnotcontains [string]$json.socket -or $null -ne $useClip) {
            $problems.Add("only an attachment held on main_hand or off_hand that is not a tool has a $field")
        }
        elseif ($clip -cnotmatch '^[A-Za-z][A-Za-z0-9_]*$' -or
            @(Get-ArtClipLibraries $socket.Skeleton | Where-Object { $_.Clips -ccontains $clip }).Count -eq 0) {
            $problems.Add("$field must name a clip of an animation library of the $($socket.Skeleton) skeleton, such as HoldStaff")
        }
        else {
            $weaponClips[$field] = $clip
        }
    }

    $launchPoint = $null
    if ($null -ne $json.PSObject.Properties['launchPoint']) {
        $values = @($json.launchPoint)
        $numbers = @($values | Where-Object { $_ -is [int] -or $_ -is [long] -or $_ -is [double] -or $_ -is [decimal] })
        if ($null -eq $json.PSObject.Properties['attackClip']) {
            $problems.Add("only an attachment with an attackClip has a launchPoint")
        }
        elseif ($values.Count -ne 3 -or $numbers.Count -ne 3 -or
            @($numbers | Where-Object { [Math]::Abs([double]$_) -gt 2.0 }).Count -gt 0) {
            $problems.Add("launchPoint must be three numbers in meters, at most 2 m from Grip each, such as [0.0, 0.67, 0.0]")
        }
        else {
            $launchPoint = [double[]]@($numbers | ForEach-Object { [double]$_ })
        }
    }

    # A weapon with an attack clip names the moment in the clip, in seconds, when its blow lands or
    # its arrow or bolt leaves, so a client plays the clip to strike or loose when the server's
    # windup of the weapon family releases the attack.
    $attackRelease = $null
    if ($null -ne $json.PSObject.Properties['attackRelease']) {
        $value = $json.attackRelease
        if ($null -eq $json.PSObject.Properties['attackClip']) {
            $problems.Add("only an attachment with an attackClip has an attackRelease")
        }
        elseif (-not ($value -is [int] -or $value -is [long] -or $value -is [double] -or $value -is [decimal]) -or
            [double]$value -le 0.0 -or [double]$value -gt 10.0) {
            $problems.Add("attackRelease must be the seconds into the attack clip when it strikes or looses, above 0 and at most 10, such as 0.35")
        }
        else {
            $attackRelease = [double]$value
        }
    }
    elseif ($null -ne $weaponClips["attackClip"]) {
        $problems.Add("an attachment with an attackClip names its attackRelease, the seconds into the clip when it strikes or looses")
    }

    # A skinned weapon that shoots, such as a bow, names how far behind its launch point the nock of
    # its string comes at the end of its clip Draw, which bends it from brace to full draw, so a client
    # bends it by how far the drawing hand pulls the string.
    $drawLength = $null
    if ($null -ne $json.PSObject.Properties['drawLength']) {
        $value = $json.drawLength
        if ($kind -cne "skinned" -or $null -eq $launchPoint) {
            $problems.Add("only a skinned attachment with a launchPoint has a drawLength")
        }
        elseif (-not ($value -is [int] -or $value -is [long] -or $value -is [double] -or $value -is [decimal]) -or
            [double]$value -le 0.0 -or [double]$value -gt 2.0) {
            $problems.Add("drawLength must be the meters behind the launchPoint that the nock comes at full draw, above 0 and at most 2, such as 0.62")
        }
        elseif (@($json.clips) -cnotcontains "Draw") {
            $problems.Add("a skinned attachment with a drawLength has a clip Draw that bends it from brace to full draw")
        }
        else {
            $drawLength = [double]$value
        }
    }
    elseif ($kind -ceq "skinned" -and $null -ne $launchPoint) {
        $problems.Add("a skinned attachment with a launchPoint names its drawLength, the meters behind it that the nock comes at full draw")
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

    $relativeDirectory = Get-ArtRelativePath $AssetDirectory
    $content = $null
    try {
        $content = ConvertTo-ArtContentRecord $json.content
    }
    catch {
        $problems.Add($_.Exception.Message)
    }

    $approvals = Read-ArtApprovals $json $stages ([string]$json.kind) $relativeDirectory $problems
    $export = $null
    $exportProblem = $null
    $oldExport = if ($null -ne $json.export) { Get-ArtOldExportProblem $json.export $relativeDirectory } else { $null }
    if ($null -ne $oldExport) {
        $problems.Add($oldExport)
    }
    elseif ($null -ne $json.export -and $kind -eq "animations") {
        $clipRecords = ConvertTo-ArtClipRecords $json.export.clips $clips
        if ([string]$json.export.contentSha256 -cnotmatch $script:ArtShaPattern -or
            [string]$json.export.targetSha256 -cnotmatch $script:ArtShaPattern -or
            [string]$json.export.rigContentSha256 -cnotmatch $script:ArtShaPattern) {
            $problems.Add("export needs the contentSha256 of the exported stage, a targetSha256, and the rigContentSha256 of the rig's export")
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
                contentSha256 = [string]$json.export.contentSha256
                targetSha256 = [string]$json.export.targetSha256
                rigContentSha256 = [string]$json.export.rigContentSha256
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

        if ([string]$json.export.contentSha256 -cnotmatch $script:ArtShaPattern -or
            [string]$json.export.targetSha256 -cnotmatch $script:ArtShaPattern) {
            $problems.Add("export needs the contentSha256 of the exported stage and a targetSha256")
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
                contentSha256 = [string]$json.export.contentSha256
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
        HoldClip = $weaponClips["holdClip"]
        AttackClip = $weaponClips["attackClip"]
        AttackRelease = $attackRelease
        LaunchPoint = $launchPoint
        DrawLength = $drawLength
        Skeleton = $skeleton
        SkeletonPath = $skeletonPath
        Rig = $rig
        Previews = $previews
        Clips = [string[]]$clips
        Variants = $variants
        Content = $content
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

    if ($null -ne $Manifest.HoldClip) {
        $lines.Add("    `"holdClip`": `"$($Manifest.HoldClip)`",")
    }

    if ($null -ne $Manifest.AttackClip) {
        $lines.Add("    `"attackClip`": `"$($Manifest.AttackClip)`",")
    }

    if ($null -ne $Manifest.AttackRelease) {
        $release = ([double]$Manifest.AttackRelease).ToString("0.0###", [System.Globalization.CultureInfo]::InvariantCulture)
        $lines.Add("    `"attackRelease`": $release,")
    }

    if ($null -ne $Manifest.LaunchPoint) {
        $point = (@($Manifest.LaunchPoint) | ForEach-Object { ([double]$_).ToString("0.0###", [System.Globalization.CultureInfo]::InvariantCulture) }) -join ", "
        $lines.Add("    `"launchPoint`": [$point],")
    }

    if ($null -ne $Manifest.DrawLength) {
        $length = ([double]$Manifest.DrawLength).ToString("0.0###", [System.Globalization.CultureInfo]::InvariantCulture)
        $lines.Add("    `"drawLength`": $length,")
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
        $lines.Add("        `"contentSha256`": `"$($Manifest.Export.contentSha256)`",")
        $lines.Add("        `"targetSha256`": `"$($Manifest.Export.targetSha256)`",")
        $lines.Add("        `"rigContentSha256`": `"$($Manifest.Export.rigContentSha256)`",")
        Add-ArtClipLines $lines @($Manifest.Export.clips)
        $lines.Add("    }")
    }
    else {
        $lines.Add('    "export": {')
        $lines.Add("        `"contentSha256`": `"$($Manifest.Export.contentSha256)`",")
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
            return "it was approved on the body $($record.body), which it no longer has; run tools/art/Sync-CageAsset.ps1, review and approve it again"
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
            return "its body $($body.Asset) joined after the cage was approved; run tools/art/Sync-CageAsset.ps1, review and approve it again"
        }

        if ($null -eq $export -or [string]::IsNullOrEmpty($export.fitSha256) -or -not $record.Contains("fitSha256") -or
            $export.fitSha256 -ne $record.fitSha256) {
            return "its body $($body.Asset) changed after the cage was approved, or was approved before bodies recorded their fit; run tools/art/Sync-CageAsset.ps1, review and approve it again"
        }
    }

    return $null
}

function Get-ArtContentItems($Manifest) {
    # The items that have a part each in the content record: the clips of a skinned asset or an
    # animation library, the bodies of a paint layer or the cage.
    if ($script:ArtAnimatedKinds -contains $Manifest.Kind) {
        return [string[]]@($Manifest.Clips)
    }

    if (@("paint", "cage") -contains $Manifest.Kind) {
        return [string[]]@($Manifest.Bodies | ForEach-Object { $_.Root })
    }

    return [string[]]@()
}

function Get-ArtContentPartNames($Manifest) {
    # The parts the content record of the asset holds, sorted by name, as content.py writes them.
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $script:ArtContentParts[$Manifest.Kind]) {
        $stage, $part = $entry.Split("/", 2)
        if ($Manifest.Stages -notcontains $stage) {
            continue
        }

        if ($part.EndsWith(":")) {
            foreach ($item in (Get-ArtContentItems $Manifest)) {
                $names.Add("$part$item")
            }
        }
        else {
            $names.Add($part)
        }
    }

    $sorted = [string[]]$names.ToArray()
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    return , $sorted
}

function Get-ArtContentProblem($Manifest) {
    # Returns $null when the content record was read from the .blend file as it is, with the current
    # version of the fingerprints, for the stages and items of asset.json, otherwise the reason it is
    # not current. A Git LFS pointer names the hash of its file, so this works without the files.
    $hint = Get-ArtUpdateContentHint $Manifest.RelativeDirectory
    $content = $Manifest.Content
    if ($null -eq $content) {
        return "the content of $($Manifest.Name).blend was never read; $hint"
    }

    if ($content.version -ne $script:ArtContentVersion) {
        return "the content of $($Manifest.Name).blend was read with version $($content.version) of the fingerprints, not $script:ArtContentVersion; $hint"
    }

    if (-not (Test-Path -LiteralPath $Manifest.SourcePath -PathType Leaf) -or
        (Get-ArtFileSha256 $Manifest.SourcePath) -cne $content.sourceSha256) {
        return "$($Manifest.Name).blend was saved after its content was read; $hint"
    }

    $expected = (Get-ArtContentPartNames $Manifest) -join ","
    if ((@($content.stages.Keys) -join ",") -cne (@($Manifest.Stages) -join ",") -or (@($content.parts.Keys) -join ",") -cne $expected) {
        return "the content of $($Manifest.Name).blend was read for other review stages, clips, or bodies than asset.json names; $hint"
    }

    return $null
}

function Test-ArtStageHolds($Manifest, [string]$Stage) {
    # Whether the stage is approved and its approval holds: the content record is current and the
    # stage's content is the approved one.
    return $Manifest.Approvals.Contains($Stage) -and $null -eq (Get-ArtContentProblem $Manifest) -and
        $Manifest.Approvals[$Stage].contentSha256 -ceq $Manifest.Content.stages[$Stage]
}

function Get-ArtStageReport($Manifest) {
    # One line per review stage: whether it is approved and whether its approval holds.
    $contentProblem = Get-ArtContentProblem $Manifest
    return @($Manifest.Stages | ForEach-Object {
            if (-not $Manifest.Approvals.Contains($_)) {
                "$($Manifest.Name): the '$_' stage is not approved yet."
            }
            elseif ($null -ne $contentProblem) {
                "$($Manifest.Name): the '$_' approval cannot be checked: $contentProblem."
            }
            elseif (Test-ArtStageHolds $Manifest $_) {
                "$($Manifest.Name): the '$_' approval holds."
            }
            else {
                "$($Manifest.Name): the '$_' content changed after its approval; review it and approve it again with tools/art/Approve-ArtAsset.ps1 -Asset $($Manifest.RelativeDirectory) -Stage $_."
            }
        })
}

function Get-ArtExportedContent($Manifest) {
    # The content of the last review stage, which an export records.
    return [string]$Manifest.Content.stages[$Manifest.Stages[-1]]
}

function Get-ArtExportChangeProblem($Manifest) {
    # Returns $null when another asset whose export an export records, such as an animation library,
    # is exported from its current content, otherwise the reason it is not.
    $contentProblem = Get-ArtContentProblem $Manifest
    if ($null -ne $contentProblem) {
        return $contentProblem
    }

    if ($null -eq $Manifest.Export) {
        return "$($Manifest.RelativeDirectory) is not exported"
    }

    if ($Manifest.Export.contentSha256 -cne (Get-ArtExportedContent $Manifest)) {
        return "$($Manifest.RelativeDirectory) has changed since its export; review, approve, and export it first"
    }

    return $null
}

function Read-ArtContentLine([string[]]$Results) {
    # The content record of an ART_CONTENT line from Blender, or $null without one.
    $line = @($Results | Where-Object { $_ -like "ART_CONTENT *" }) | Select-Object -First 1
    if ($null -eq $line) {
        return $null
    }

    return $line.Substring("ART_CONTENT ".Length) | ConvertFrom-Json
}

function Assert-ArtFileUnchanged($Manifest, [string]$SourceSha256Before, [string]$Problem, [string]$Again = "run the command again") {
    # Throws when the asset's file has another hash than before a Blender run read it, so no record
    # pairs the content of one version of the file with the hash of another.
    if ([string]::IsNullOrEmpty($SourceSha256Before)) {
        throw "The hash of $($Manifest.Name).blend before Blender read it is missing."
    }

    if ((Get-ArtFileSha256 $Manifest.SourcePath) -cne $SourceSha256Before) {
        throw "$($Manifest.Name).blend $Problem; $Again."
    }
}

function Test-ArtSameStages($Manifest, $Read) {
    # Whether the stages Blender reported are those of the content record.
    return $null -ne $Read -and $null -ne $Manifest.Content -and $Read.version -eq $Manifest.Content.version -and
        @($Manifest.Stages | Where-Object { [string]$Read.stages.$_ -cne $Manifest.Content.stages[$_] }).Count -eq 0
}

function Assert-ArtExportedContent($Manifest, [string[]]$Results, [string]$SourceSha256Before) {
    # Throws, before the export record is written, when the file changed while it was exported: the
    # content the export run reported must be the record the gate accepted, and the file's hash before
    # and after the run that record's. The files the run wrote then match no record, so the
    # verification fails until the export runs again.
    $problem = "changed while it was exported"
    if ($SourceSha256Before -cne $Manifest.Content.sourceSha256 -or -not (Test-ArtSameStages $Manifest (Read-ArtContentLine $Results))) {
        throw "$($Manifest.Name).blend $problem; run the export again."
    }

    Assert-ArtFileUnchanged $Manifest $SourceSha256Before $problem "run the export again"
}

function Update-ArtContentRecord([string]$Blender, $Manifest, [string[]]$Results, [string]$SourceSha256Before) {
    # Makes the content record of an asset current and returns whether it wrote asset.json: nothing
    # to do when the record was read from the file as it is now, otherwise the ART_CONTENT line of
    # Results, which a Blender run on the file printed, such as a validation, with
    # SourceSha256Before the file's hash before that run, or else of a content run. A file saved
    # while Blender read it is refused.
    $read = $null
    if (@($Results).Count -gt 0) {
        Assert-ArtFileUnchanged $Manifest $SourceSha256Before "was saved while it was read"
        $read = Read-ArtContentLine $Results
    }

    if ($null -eq (Get-ArtContentProblem $Manifest)) {
        return $false
    }

    $sourceSha256 = Get-ArtFileSha256 $Manifest.SourcePath
    if ($null -eq $read) {
        # A garment's run takes the content of its cage, so the cage's record is made current first.
        if ($Manifest.Kind -eq "garment") {
            $null = Update-ArtContentRecord $Blender $Manifest.Cage
        }

        $read = Read-ArtContentLine (Invoke-ArtBlender $Blender $Manifest (@("content") + (Get-ArtValidationArguments $Manifest $Manifest.Stages[-1])))
        Assert-ArtFileUnchanged $Manifest $sourceSha256 "was saved while it was read"
    }

    if ($null -eq $read -or [int]$read.version -ne $script:ArtContentVersion) {
        throw "Blender did not report the content of $($Manifest.Name) with version $script:ArtContentVersion of the fingerprints."
    }

    $stages = [ordered]@{}
    foreach ($stage in $Manifest.Stages) {
        $stages[$stage] = [string]$read.stages.$stage
    }

    $parts = [ordered]@{}
    foreach ($property in $read.parts.PSObject.Properties) {
        $parts[$property.Name] = [string]$property.Value
    }

    $Manifest.Content = [ordered]@{ version = [int]$read.version; sourceSha256 = $sourceSha256; stages = $stages; parts = $parts }
    $problem = Get-ArtContentProblem $Manifest
    if ($null -ne $problem) {
        throw "The content Blender reported for $($Manifest.Name) does not fit its asset.json: $problem."
    }

    Write-ArtAssetManifest $Manifest
    return $true
}

function Get-ArtTextureTemplateProblem($Manifest) {
    # Returns $null when the texture is painted in its template's current UVs: the UVs its content
    # records are the template's, otherwise the reason it is not. A texture whose own content record
    # is not current is reported by its gate.
    $template = $Manifest.Garment
    if ($null -ne (Get-ArtContentProblem $Manifest)) {
        return $null
    }

    $templateProblem = Get-ArtContentProblem $template
    if ($null -ne $templateProblem) {
        return "its template $($template.RelativeDirectory) cannot be compared: $templateProblem"
    }

    if ($Manifest.Content.parts["templateUv"] -cne $template.Content.parts["uv"]) {
        return "its template's UVs changed; run tools/art/Sync-TextureAsset.ps1, and review and approve it again if the sync changes it"
    }

    return $null
}

function Get-ArtCageGateProblem($Cage) {
    # Returns $null when garment templates may be approved and exported on the cage: its stage is
    # approved for its current content, and its bodies still have the exports it was approved on.
    $problem = Get-ArtExportGateProblem $Cage
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

function Get-ArtExportGateProblem($Manifest) {
    # Returns $null when every review stage is approved and every approval holds for the content of
    # the current source file, otherwise the reason the asset may not be exported.
    foreach ($stage in $Manifest.Stages) {
        if (-not $Manifest.Approvals.Contains($stage)) {
            return "the '$stage' review stage is not approved"
        }
    }

    $contentProblem = Get-ArtContentProblem $Manifest
    if ($null -ne $contentProblem) {
        return $contentProblem
    }

    foreach ($stage in $Manifest.Stages) {
        if (-not (Test-ArtStageHolds $Manifest $stage)) {
            return "the '$stage' content of $($Manifest.Name).blend changed after its approval"
        }
    }

    return $null
}

function Test-ArtAssetInReview($Manifest) {
    # An asset is in review while a later stage still waits, such as a skinned model before
    # its rig. It has no export yet.
    return @($Manifest.Stages | Where-Object { -not $Manifest.Approvals.Contains($_) }).Count -gt 0
}

function Get-ArtReviewProblem($Manifest) {
    # Returns $null when every approval of an asset in review holds for the content of the current
    # source file, so the repository only holds approved states, otherwise the reason it does not.
    $approved = @($Manifest.Stages | Where-Object { $Manifest.Approvals.Contains($_) })
    if ($approved.Count -eq 0) {
        return "no review stage is approved; approve the '$($Manifest.Stages[0])' stage with tools/art/Approve-ArtAsset.ps1"
    }

    $contentProblem = Get-ArtContentProblem $Manifest
    if ($null -ne $contentProblem) {
        return $contentProblem
    }

    foreach ($stage in $approved) {
        if (-not (Test-ArtStageHolds $Manifest $stage)) {
            return "the '$stage' content of $($Manifest.Name).blend changed after its approval"
        }
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

    # A garment is checked against the current files of its bodies and the content of its cage.
    if ($Manifest.Kind -eq "garment") {
        $arguments = @("--kind", "garment", "--roots", ($Manifest.RootObjects -join ","), "--stage", $Stage,
            "--triangle-budget", [string]$Manifest.TriangleBudget, "--skeleton", $Manifest.SkeletonPath,
            "--garment", $Manifest.Name, "--manifest", $Manifest.ManifestPath,
            "--cage-manifest", $Manifest.Cage.ManifestPath, "--cage-content", (Get-ArtCageContent $Manifest.Cage)) +
            (Get-ArtGarmentCoverArguments $Manifest)
        foreach ($body in @($Manifest.Bodies)) {
            $arguments += @("--body", "$($body.SourcePath)=$($body.Root)")
        }

        # The templates of lower layers it is worn over: its band clears their real surface where it
        # lies over one, and at its rig stage it hides what it covers of them and none comes through.
        return $arguments + (Get-ArtGarmentPairArguments $Manifest) + (Get-ArtGarmentClipArguments $Manifest)
    }

    # A texture is checked against its template's current UVs, and one with transparent parts gets
    # its masks in its template's band, with what it hides of the templates of lower layers worn
    # under it, checked in the clips of the skeleton's libraries.
    if ($Manifest.Kind -eq "texture") {
        return @("--kind", "texture", "--roots", ($Manifest.RootObjects -join ","), "--stage", $Stage,
            "--texture", $Manifest.Name, "--garment", $Manifest.Garment.Name, "--garment-file", $Manifest.Garment.SourcePath,
            "--dyes", [string]$Manifest.Garment.Dyes) +
            (Get-ArtGarmentCoverArguments $Manifest.Garment) + (Get-ArtGarmentPairArguments $Manifest.Garment) +
            (Get-ArtGarmentClipArguments $Manifest)
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

function Get-ArtCacheArguments([string]$Blender, $Manifest, [switch]$NoCache, [switch]$Preview, [object[]]$Pairs) {
    # The fingerprints by which the cache of the checks of a garment template or a garment texture keys
    # their results, from the content records of the other assets they read, each made current first:
    # the fit of every body from its export while that export is current, the content of every clip
    # library and of every template of a lower layer, and for a preview or a texture the cage's content,
    # which a template's validation already passes, and for a texture's preview its image, whose file
    # the preview does not open. Blender reads the content of the file it opens itself. A fingerprint
    # left out leaves the units that need it computed and not cached. -NoCache computes every unit and
    # neither reads nor writes the cache. -Pairs gives the templates of lower layers when the caller
    # has them, as Get-ArtGarmentPairs gives them. The cache never stops a command: an asset whose
    # fingerprint cannot be had is left out with a line that says why, and its own commands report its
    # problems. See docs/ART_PIPELINE.md, Cache Of The Garment Checks.
    if (@("garment", "texture") -notcontains $Manifest.Kind) {
        return @()
    }

    if ($NoCache) {
        return @("--no-cache")
    }

    $template = if ($Manifest.Kind -eq "texture") { $Manifest.Garment } else { $Manifest }
    $arguments = @()
    foreach ($body in @($template.Bodies)) {
        try {
            $bodyManifest = Read-ArtAssetManifest (Split-Path -Parent $body.ManifestPath)
            $null = Update-ArtContentRecord $Blender $bodyManifest
            $problem = Get-ArtExportChangeProblem $bodyManifest
            if ($null -eq $problem -and [string]::IsNullOrEmpty($bodyManifest.Export.fitSha256)) {
                $problem = "its export records no fit"
            }

            if ($null -ne $problem) {
                Write-ArtUncached $body.Asset $problem
                continue
            }

            $arguments += @("--body-fit", "$($body.Root)=$([string]$bodyManifest.Export.fitSha256)")
        }
        catch {
            Write-ArtUncached $body.Asset $_.Exception.Message
        }
    }

    $libraries = @()
    try {
        $libraries = @(Get-ArtClipLibraries $Manifest.Skeleton | Where-Object { $_.Clips.Count -gt 0 })
    }
    catch {
        Write-ArtUncached "the animation libraries of $($Manifest.Skeleton)" $_.Exception.Message
    }

    foreach ($library in $libraries) {
        try {
            $current = Read-ArtAssetManifest (Join-Path $script:ArtRepositoryRoot $library.Asset)
            $null = Update-ArtContentRecord $Blender $current
            $problem = Get-ArtContentProblem $current
            if ($null -ne $problem) {
                Write-ArtUncached $library.Asset $problem
                continue
            }

            $arguments += @("--clip-content", "$($library.SourcePath)=$(Get-ArtExportedContent $current)")
        }
        catch {
            Write-ArtUncached $library.Asset $_.Exception.Message
        }
    }

    if (-not $PSBoundParameters.ContainsKey("Pairs")) {
        try {
            $Pairs = @(Get-ArtGarmentPairs $template)
        }
        catch {
            $Pairs = @()
            Write-ArtUncached "the templates of lower layers under $($template.Name)" $_.Exception.Message
        }
    }

    foreach ($pair in @($Pairs)) {
        try {
            $current = $pair
            if ($null -ne (Get-ArtContentProblem $current)) {
                $current = Read-ArtAssetManifest $pair.Directory
                $null = Update-ArtContentRecord $Blender $current
            }

            $problem = Get-ArtContentProblem $current
            if ($null -ne $problem) {
                Write-ArtUncached $pair.RelativeDirectory $problem
                continue
            }

            $arguments += @("--pair-content", "$($current.Name)=$([string]$current.Content.stages['rig'])")
        }
        catch {
            Write-ArtUncached $pair.RelativeDirectory $_.Exception.Message
        }
    }

    if ($Preview -or $Manifest.Kind -eq "texture") {
        try {
            $null = Update-ArtContentRecord $Blender $template.Cage
            $arguments += @("--cage-content", (Get-ArtCageContent $template.Cage))
        }
        catch {
            Write-ArtUncached "the cage of $($template.Name)" $_.Exception.Message
        }
    }

    if ($Preview -and $Manifest.Kind -eq "garment") {
        $arguments += @("--roots", ($Manifest.RootObjects -join ","))
    }

    if ($Preview -and $Manifest.Kind -eq "texture") {
        try {
            $null = Update-ArtContentRecord $Blender $Manifest
            $problem = Get-ArtContentProblem $Manifest
            if ($null -eq $problem) {
                $arguments += @("--texture-content", [string]$Manifest.Content.parts["image"])
            }
            else {
                Write-ArtUncached $Manifest.RelativeDirectory $problem
            }
        }
        catch {
            Write-ArtUncached $Manifest.RelativeDirectory $_.Exception.Message
        }
    }

    return $arguments
}

function Invoke-ArtGarmentSheet([string]$Blender, $Manifest, [string]$Output, [switch]$NoCache) {
    # Renders the review sheet of a garment template with what it covers on every body, its own checks
    # through the cache of the checks, and returns Blender's result lines.
    return Invoke-ArtBlender $Blender $Manifest (@("preview", "--output", $Output, "--kind", "garment", "--garment", $Manifest.Name) +
        (Get-ArtGarmentCoverArguments $Manifest) + (Get-ArtGarmentClipArguments $Manifest) +
        (Get-ArtCacheArguments $Blender $Manifest -NoCache:$NoCache -Preview))
}

function Invoke-ArtTextureSheet([string]$Blender, $Manifest, [string]$Output, [switch]$NoCache) {
    # Renders the review sheet of a garment texture in its template's file, with the texture on every
    # version and, when it has transparent parts, its own masks through the cache of the checks, and
    # returns Blender's result lines.
    return Invoke-ArtBlender $Blender $Manifest.Garment (@("preview", "--output", $Output, "--kind", "texture",
            "--roots", ($Manifest.RootObjects -join ","), "--garment", $Manifest.Garment.Name, "--texture", $Manifest.Name,
            "--texture-file", $Manifest.SourcePath) + (Get-ArtGarmentCoverArguments $Manifest.Garment) +
        (Get-ArtGarmentClipArguments $Manifest) + (Get-ArtCacheArguments $Blender $Manifest -NoCache:$NoCache -Preview))
}

function Write-ArtUncached([string]$Asset, [string]$Reason) {
    # The line that says which checks the cache leaves out, and why: the checks that read the asset are
    # computed and not cached.
    Write-Host "The checks that read $Asset are not cached: $($Reason.Trim().TrimEnd('.'))."
}

function Format-ArtElapsed([string]$Name, [System.Diagnostics.Stopwatch]$Watch, [string[]]$Results) {
    # The line that ends the work on an asset: the seconds it took and, when its Blender runs checked a
    # garment template or a masked texture, how many of the units of those checks came from their cache.
    $seconds = $Watch.Elapsed.TotalSeconds.ToString("0.0", [System.Globalization.CultureInfo]::InvariantCulture)
    $units = 0
    $cached = 0
    $notes = New-Object System.Collections.Generic.List[string]
    foreach ($line in @($Results | Where-Object { $_ -like "ART_CACHE *" })) {
        $report = $line.Substring("ART_CACHE ".Length) | ConvertFrom-Json
        $units += [int]$report.units
        $cached += [int]$report.cached
        # A sync's sheet and validation may give the same note; it is said once.
        foreach ($note in @($report.notes | Where-Object { $null -ne $_ })) {
            if (-not $notes.Contains([string]$note)) {
                $notes.Add([string]$note)
            }
        }
    }

    if ($units -eq 0) {
        return "$Name took $seconds s."
    }

    $why = if ($notes.Count -gt 0) { " Not cached, since $($notes -join '; ')." } else { "" }
    return "$Name took $seconds s; $cached of $units check units came from the cache.$why"
}
