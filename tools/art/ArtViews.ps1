# The game views of tools/art/Render-ArtViews.ps1: the standard set of views in
# tools/art/views/standard-set.json, the committed key of a view, the baseline of their numbers in
# art/Views/baseline.json, which tools/art/Verify-ArtAssets.ps1 holds to the committed content without
# Blender, and the rules of the views' cache, their comparison, and their data. tools/ci/Test-ArtViews.ps1
# checks the functions here without Blender. Dot-source it after ArtPipelineCommon.ps1. See
# docs/ART_PIPELINE.md, Game Views.

# The version of the format of the views' keys and numbers, which
# tools/art/blender/art_pipeline/game_view.py keeps as VIEW_VERSION and a Python test holds equal: it is
# raised when the text of the committed key or the baseline's format changes. A change of the
# renderer's code changes every key by itself, through the fingerprint of its code in the key.
$script:ArtViewVersion = 3
$script:ArtViewSetPath = Join-Path $PSScriptRoot "views/standard-set.json"
$script:ArtViewBaselinePath = Join-Path $script:ArtRepositoryRoot "art/Views/baseline.json"
# What a camera frames, the phases of a pose, and the data folder of the exported garments.
$script:ArtViewFrames = @("figure", "head", "waist", "feet")
$script:ArtViewPhases = @("clean", "rare")
$script:ArtViewDataFolder = $script:ArtGarmentModelFolder
# The files the renderer runs, and the scripts that build its job and the views' keys, by repository
# path: every file below a folder but Python's caches. A Python test holds that they cover every module
# game_view.py imports.
$script:ArtViewCodePaths = @("tools/art/blender/art_pipeline", "tools/art/blender/human_base", "tools/art/blender/art_views_cli.py", "art/Skeletons", "tools/art/Render-ArtViews.ps1", "tools/art/ArtViews.ps1")
# AppearanceRules: the paint categories a body always shows one of per slot; and
# PlayerBodyMaterials.MaximumPaintLayers: the paint layers the skin shader draws on one material.
$script:ArtViewRequiredCategories = @("underwear")
$script:ArtViewMaxPaintLayers = 4
# More skin or garment through pixels than the baseline by more than this is a regression: two runs of
# the whole set give the same numbers to the pixel on one machine.
$script:ArtViewTolerance = 0
# Entries of the views cache that no run took for this many days and that no view of the set would
# take now go; a picture without its numbers, and a file an interrupted run left, after a day.
$script:ArtViewCacheDays = 30
$script:ArtViewManifests = @{}
$script:ArtViewCode = $null

function New-ArtViewTable {
    # A table whose keys are compared as the names are written, as the views are named.
    return New-Object System.Collections.Hashtable ([StringComparer]::Ordinal)
}

function Get-ArtViewManifest([string]$Asset) {
    # The manifest of an asset a view reads, by its folder, read once.
    if (-not $script:ArtViewManifests.ContainsKey($Asset)) {
        $script:ArtViewManifests[$Asset] = Read-ArtAssetManifest (Join-Path $script:ArtRepositoryRoot $Asset)
    }

    return $script:ArtViewManifests[$Asset]
}

function Format-ArtViewNumber($Value) {
    return ([double]$Value).ToString("R", [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-ArtTextSha256([string]$Text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return (($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Text)) | ForEach-Object { $_.ToString("x2") }) -join "")
    }
    finally {
        $sha.Dispose()
    }
}

function Get-ArtTextFileSha256([string]$Path) {
    # The SHA-256 of a file's text with its line ends as LF, byte for byte otherwise, so a checkout on
    # Windows and one on Linux give the same.
    $latin = [System.Text.Encoding]::GetEncoding(28591)
    $bytes = $latin.GetBytes($latin.GetString([System.IO.File]::ReadAllBytes($Path)).Replace("`r`n", "`n"))
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return (($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString("x2") }) -join "")
    }
    finally {
        $sha.Dispose()
    }
}

function Get-ArtViewCodeFingerprint([string]$Root = $script:ArtRepositoryRoot) {
    # The fingerprint of the code the renderer runs, without Blender: every file of ArtViewCodePaths by
    # its repository path and the SHA-256 of its text with LF line ends, sorted by the path.
    $base = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($path in $script:ArtViewCodePaths) {
        $full = Join-Path $base $path
        $files = if (Test-Path -LiteralPath $full -PathType Leaf) {
            @(Get-Item -LiteralPath $full)
        }
        elseif (Test-Path -LiteralPath $full -PathType Container) {
            @(Get-ChildItem -LiteralPath $full -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/]__pycache__[\\/]' -and $_.Extension -ne ".pyc" })
        }
        else {
            throw "The renderer's code has no $path."
        }

        foreach ($file in $files) {
            $relative = [System.IO.Path]::GetFullPath($file.FullName).Substring($base.Length + 1).Replace('\', '/')
            $lines.Add("$relative $(Get-ArtTextFileSha256 $file.FullName)")
        }
    }

    $sorted = $lines.ToArray()
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    return Get-ArtTextSha256 ("art-view-code/1`n" + ($sorted -join "`n") + "`n")
}

function Get-ArtViewCode {
    # The fingerprint of the renderer's code in this checkout, computed once.
    if ($null -eq $script:ArtViewCode) {
        $script:ArtViewCode = Get-ArtViewCodeFingerprint
    }

    return $script:ArtViewCode
}

function Get-ArtViewSortedPaints($Paints) {
    # Paint layers in the order of a body's catalog: the layer, then the name, as the names are written.
    $byKey = New-ArtViewTable
    foreach ($paint in @($Paints | Where-Object { $null -ne $_ })) {
        $byKey["{0:D10}|{1}" -f [int]$paint.Layer, [string]$paint.Name] = $paint
    }

    $keys = [string[]]@($byKey.Keys)
    [Array]::Sort($keys, [StringComparer]::Ordinal)
    return @($keys | ForEach-Object { $byKey[$_] })
}

function Get-ArtViewPaints($Catalog, [string[]]$Named) {
    # BodyAppearanceCatalog.ShownPaints before the garments: of the paint layers that fit a body, in the
    # catalog's order, of each category and slot the first the look names, then the body's first of a
    # required category, the underwear, of a slot the look names none of, since underwear is always
    # worn. The categories are compared without case, as the import parses them. Which underwear a drawn
    # garment hides, the renderer decides.
    $ordered = @(Get-ArtViewSortedPaints $Catalog)
    $taken = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $shown = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($byName in @($true, $false)) {
        foreach ($paint in $ordered) {
            $category = ([string]$paint.Category).ToLowerInvariant()
            $key = "$category|$([string]$paint.Slot)"
            if ($taken.Contains($key)) {
                continue
            }

            $wanted = if ($byName) { @($Named) -ccontains [string]$paint.Name } else { $script:ArtViewRequiredCategories -ccontains $category }
            if ($wanted) {
                $null = $taken.Add($key)
                $null = $shown.Add([string]$paint.Name)
            }
        }
    }

    return @($ordered | Where-Object { $shown.Contains([string]$_.Name) })
}

function Get-ArtViewPaintProblem($Paints, [string]$Where) {
    # Why a look cannot be drawn: more paint layers on one material, a part of the body, than the skin
    # shader draws; $null when it can.
    $counts = [ordered]@{}
    foreach ($paint in @($Paints)) {
        $counts[[string]$paint.Part] = 1 + [int]$counts[[string]$paint.Part]
    }

    $over = @($counts.Keys | Where-Object { $counts[$_] -gt $script:ArtViewMaxPaintLayers } | ForEach-Object { "$($counts[$_]) on $_" })
    if ($over.Count -eq 0) {
        return $null
    }

    return "${Where} shows $($over -join ', ') paint layers; the skin shader draws at most $script:ArtViewMaxPaintLayers on a material."
}

function Get-ArtViewPaintCatalog {
    # Every paint layer below art/Paint.
    $folder = Join-Path $script:ArtSourceRoot "Paint"
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) {
        return @()
    }

    return @(Get-ChildItem -LiteralPath $folder -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "asset.json") -PathType Leaf } |
            Sort-Object Name | ForEach-Object { Get-ArtViewManifest "art/Paint/$($_.Name)" })
}

function Read-ArtViewSet {
    # Every view of the standard set, outfit by outfit, body by body, pose by pose, camera by camera, as
    # <Outfit>_<Body>_<Pose>_<Camera>: each with its outfit, its body, its pose and its phase, its
    # camera, and the assets it reads: the body, the garments, each a texture with its template and the
    # data the export wrote for the body, the paint layers the body shows with the look, and the clip
    # library of its pose. An outfit must be one a player can wear, one garment per visual slot, with no
    # more paint layers on a material than the skin shader draws.
    $where = "tools/art/views/standard-set.json"
    try {
        $json = Get-Content -Raw -LiteralPath $script:ArtViewSetPath | ConvertFrom-Json
    }
    catch {
        throw "${where}: invalid JSON. $($_.Exception.Message)"
    }

    $library = Get-ArtViewManifest ([string]$json.library)
    if ($library.Kind -ne "animations") {
        throw "${where}: library must name an animation library, such as art/Animations/Human_Actions."
    }

    # The export's frames of a clip is its last frame; a clip starts at frame 0.
    $frames = @{}
    foreach ($clip in @($library.Export.clips | Where-Object { $null -ne $_ })) {
        $frames[[string]$clip.name] = [int]$clip.frames
    }

    $cameras = [ordered]@{}
    foreach ($camera in @($json.cameras)) {
        $name = [string]$camera.name
        if ($name -cnotmatch '^[A-Za-z][A-Za-z0-9]*$' -or $cameras.Contains($name) -or $script:ArtViewFrames -cnotcontains [string]$camera.frame -or
            -not (Test-ArtNumber $camera.yaw) -or -not (Test-ArtNumber $camera.pitch) -or [Math]::Abs([double]$camera.pitch) -gt 89.0) {
            throw "${where}: every camera has a name of letters and digits, its own, a yaw and a pitch in degrees, and what it frames, one of $($script:ArtViewFrames -join ', ')."
        }

        $cameras[$name] = [pscustomobject]@{ Name = $name; Yaw = [double]$camera.yaw; Pitch = [double]$camera.pitch; Frame = [string]$camera.frame }
    }

    $poses = New-Object System.Collections.Generic.List[object]
    foreach ($pose in @($json.poses)) {
        $name = [string]$pose.name
        $clip = if ($null -ne $pose.clip) { [string]$pose.clip } else { $null }
        $bad = $name -cnotmatch '^[A-Za-z][A-Za-z0-9]*$' -or @($poses | Where-Object { $_.Name -ceq $name }).Count -gt 0 -or
            $script:ArtViewPhases -cnotcontains [string]$pose.phase -or @($pose.cameras).Count -eq 0 -or
            @($pose.cameras | Where-Object { -not $cameras.Contains([string]$_) }).Count -gt 0
        if ($null -ne $clip) {
            $bad = $bad -or $library.Clips -cnotcontains $clip -or -not ($pose.frame -is [int] -or $pose.frame -is [long]) -or
                [int]$pose.frame -lt 0 -or ($frames.ContainsKey($clip) -and [int]$pose.frame -gt $frames[$clip])
        }

        if ($bad) {
            throw "${where}: the pose '$name' needs a name of letters and digits, its own, a phase of $($script:ArtViewPhases -join ' or '), cameras of the set, and for a clip of $($library.RelativeDirectory) a frame from 0 to its last."
        }

        $poses.Add([pscustomobject]@{
                Name = $name
                Clip = $clip
                Frame = if ($null -ne $clip) { [int]$pose.frame } else { $null }
                Phase = [string]$pose.phase
                Cameras = [string[]]@($pose.cameras | ForEach-Object { [string]$_ })
            })
    }

    $catalog = @(Get-ArtViewPaintCatalog)
    $bodies = @($json.bodies | ForEach-Object { Get-ArtViewManifest ([string]$_) })
    $views = New-Object System.Collections.Generic.List[object]
    $names = @{}
    foreach ($outfit in @($json.outfits)) {
        $outfitName = [string]$outfit.name
        if ($outfitName -cnotmatch '^[A-Za-z][A-Za-z0-9]*$' -or $names.ContainsKey($outfitName)) {
            throw "${where}: every outfit has a name of letters and digits, its own."
        }

        $names[$outfitName] = $true
        $garments = @($outfit.garments | ForEach-Object {
                $texture = Get-ArtViewManifest "art/GarmentTextures/$_"
                [pscustomobject]@{ Texture = $texture; Template = $texture.Garment }
            })
        $slots = @($garments | ForEach-Object { $_.Template.Slot })
        if (@($slots | Sort-Object -Unique).Count -ne $slots.Count) {
            throw "${where}: the outfit $outfitName wears more than one garment of a slot ($($slots -join ', ')); a player wears one per slot."
        }

        $named = [string[]]@($outfit.paints | ForEach-Object { (Get-ArtViewManifest "art/Paint/$_").Name })
        foreach ($body in $bodies) {
            $label = [string]$body.RootObjects[0]
            $worn = @($garments | ForEach-Object {
                    $data = Get-ArtViewDataRecord $_.Texture $_.Template $label
                    [pscustomobject]@{ Texture = $_.Texture; Template = $_.Template; Data = $data.Data; DataSha256 = $data.DataSha256 }
                })
            $fitting = @($catalog | Where-Object { @($_.Bodies | ForEach-Object { $_.Asset }) -ccontains $body.RelativeDirectory })
            $shown = @(Get-ArtViewPaints $fitting $named)
            $paintProblem = Get-ArtViewPaintProblem $shown "${where}: the outfit $outfitName on $label"
            if ($null -ne $paintProblem) {
                throw $paintProblem
            }

            foreach ($pose in $poses) {
                foreach ($cameraName in $pose.Cameras) {
                    $assets = @(@($body.RelativeDirectory) + @($worn | ForEach-Object { $_.Template.RelativeDirectory; $_.Texture.RelativeDirectory }) +
                        @($shown | ForEach-Object { $_.RelativeDirectory }) + @(if ($null -ne $pose.Clip) { $library.RelativeDirectory }))
                    $views.Add([pscustomobject]@{
                            Name = "${outfitName}_${label}_$($pose.Name)_$cameraName"
                            Outfit = $outfitName
                            Label = $label
                            Body = $body
                            Garments = $worn
                            Paints = $shown
                            Catalog = $fitting
                            Named = $named
                            Library = $library
                            Pose = $pose
                            Camera = $cameras[$cameraName]
                            Assets = [string[]]$assets
                        })
                }
            }
        }
    }

    return $views.ToArray()
}

function Split-ArtViewList([string[]]$Values) {
    return @($Values | ForEach-Object { "$_".Split(",") } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Select-ArtViews($Views, [string[]]$Outfit = @(), [string[]]$Body = @(), [string[]]$Pose = @()) {
    # The views the filters name, each a list or one text with commas; a name that no view has stops
    # the run with the names there are.
    $selected = @($Views)
    foreach ($filter in @(
            @{ Name = "Outfit"; Values = (Split-ArtViewList $Outfit); Of = { param($v) @($v.Outfit) } },
            @{ Name = "Body"; Values = (Split-ArtViewList $Body); Of = { param($v) @($v.Label, $v.Body.RelativeDirectory) } },
            @{ Name = "Pose"; Values = (Split-ArtViewList $Pose); Of = { param($v) @($v.Pose.Name) } })) {
        if ($filter.Values.Count -eq 0) {
            continue
        }

        $known = @($Views | ForEach-Object { & $filter.Of $_ } | Sort-Object -Unique)
        $unknown = @($filter.Values | Where-Object { $known -cnotcontains $_ })
        if ($unknown.Count -gt 0) {
            throw "-$($filter.Name) $($unknown -join ', ') names no view of the standard set; it has $($known -join ', ')."
        }

        $selected = @($selected | Where-Object { $view = $_; @(& $filter.Of $view | Where-Object { $filter.Values -ccontains $_ }).Count -gt 0 })
    }

    return $selected
}

function Get-ArtViewDataRecord($Texture, $Template, [string]$Label) {
    # The data a view reads of a garment on a body, as the export recorded it: a masked texture's own,
    # or else its template's, with the dataSha256 of the export record, or none before an export.
    $mask = @($Texture.Export.masks | Where-Object { $null -ne $_ -and [string]$_.body -ceq $Label }) | Select-Object -First 1
    if ($null -ne $mask) {
        return [pscustomobject]@{ Data = [string]$mask.data; DataSha256 = [string]$mask.dataSha256 }
    }

    $model = @($Template.Export.models | Where-Object { $null -ne $_ -and [string]$_.body -ceq $Label }) | Select-Object -First 1
    if ($null -ne $model) {
        return [pscustomobject]@{ Data = [string]$model.data; DataSha256 = [string]$model.dataSha256 }
    }

    return [pscustomobject]@{ Data = Get-ArtGarmentDataTarget $Template.Name $Label; DataSha256 = "none" }
}

function Find-ArtViewData($Garment, [string]$Label, [string[]]$Folders) {
    # The data a garment reads on a body: a masked texture's own, <Texture>_<Label>.json, from the first
    # folder that has it, or else its template's, <Template>_<Label>.json, from the first folder that has
    # it.
    foreach ($name in @("$($Garment.Texture.Name)_$Label.json", "$($Garment.Template.Name)_$Label.json")) {
        foreach ($folder in $Folders) {
            $path = Join-Path $folder $name
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                return (Resolve-Path -LiteralPath $path).Path
            }
        }
    }

    throw "No data of $($Garment.Texture.Name) or $($Garment.Template.Name) for $Label in $($Folders -join ', '); export $($Garment.Template.RelativeDirectory)."
}

function Get-ArtViewContent($Manifest, [string]$Field, [string]$Name) {
    # A fingerprint of an asset's content record, a stage or a part, or none while the file was never read.
    if ($null -eq $Manifest.Content) {
        return "none"
    }

    $table = if ($Field -eq "parts") { $Manifest.Content.parts } else { $Manifest.Content.stages }
    $value = if ($null -ne $table) { $table[$Name] } else { $null }
    if ([string]::IsNullOrEmpty($value)) {
        return "none"
    }

    return [string]$value
}

function Get-ArtViewKeyText($View, [string]$Code) {
    # The canonical text of a view's committed key, which PowerShell builds without Blender: the version
    # of its format, the fingerprint of the renderer's code and the Blender version the pipeline pins,
    # the view's own definition, and per asset it reads the fingerprint of its content record: the
    # body's last stage, each template's rig stage with the fields of its asset.json that decide what is
    # drawn and its band, whose reach decides contact, each texture's texture stage, the data each garment's export wrote for the body, each paint
    # layer the body shows with its stage and fields, and for a clip pose the clip's own part and the
    # skeleton part of its library, so another clip leaves the key alone. The garment cage is not read.
    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("art-view/$script:ArtViewVersion")
    $lines.Add("code $Code")
    $lines.Add("blender $script:ArtBlenderVersion")
    $lines.Add("view $($View.Name)")
    $lines.Add("body $($View.Label)")
    $lines.Add("pose " + $(if ($null -ne $View.Pose.Clip) { "$($View.Pose.Clip) $(([int]$View.Pose.Frame).ToString($culture))" } else { "rest" }))
    $lines.Add("camera yaw=$(Format-ArtViewNumber $View.Camera.Yaw) pitch=$(Format-ArtViewNumber $View.Camera.Pitch) frame=$($View.Camera.Frame)")
    $body = $View.Body
    $lines.Add("asset $($body.RelativeDirectory) $($body.Stages[-1])=$(Get-ArtViewContent $body 'stages' $body.Stages[-1])")
    foreach ($garment in $View.Garments) {
        $template, $texture = $garment.Template, $garment.Texture
        $turned = $template.Recipe -isnot [string] -and $null -ne $template.Recipe -and [double]$template.Recipe.hem -gt 0.0
        $lines.Add("asset $($template.RelativeDirectory) rig=$(Get-ArtViewContent $template 'stages' 'rig')")
        $lines.Add("fields $($template.RelativeDirectory) slot=$($template.Slot) layer=$($template.Layer) hides=$(@($template.Hides) -join '+') " +
            "hidesUnderwear=$($template.HidesUnderwear) hair=$($template.Hair) turned=$turned band=$($template.Band)")
        $lines.Add("asset $($texture.RelativeDirectory) texture=$(Get-ArtViewContent $texture 'stages' 'texture')")
        $lines.Add("data $($garment.Data)=$($garment.DataSha256)")
    }

    foreach ($paint in $View.Paints) {
        $lines.Add("asset $($paint.RelativeDirectory) paint=$(Get-ArtViewContent $paint 'stages' 'paint')")
        $lines.Add("fields $($paint.RelativeDirectory) category=$($paint.Category) slot=$($paint.Slot) layer=$($paint.Layer) part=$($paint.Part)")
    }

    if ($null -ne $View.Pose.Clip) {
        $library = $View.Library
        $lines.Add("asset $($library.RelativeDirectory) clip:$($View.Pose.Clip)=$(Get-ArtViewContent $library 'parts' "clip:$($View.Pose.Clip)") " +
            "skeleton=$(Get-ArtViewContent $library 'parts' 'skeleton')")
    }

    return ($lines -join "`n") + "`n"
}

function Get-ArtViewKey($View) {
    # A view's committed key: the SHA-256 of the canonical text of what it reads.
    return Get-ArtTextSha256 (Get-ArtViewKeyText $View (Get-ArtViewCode))
}

function Get-ArtViewLocalKey($View, [string]$Code, [string]$Blender, [string[]]$FileLines) {
    # The key of a view in the cache: its committed text, the version and build of the Blender that
    # renders it, and a line per file it reads with the file's SHA-256, so the views of content that is
    # not exported yet are cached too.
    return Get-ArtTextSha256 ("art-view-cache/$script:ArtViewVersion`nblender=$Blender`n" + (Get-ArtViewKeyText $View $Code) + ($FileLines -join "`n") + "`n")
}

function Write-ArtViewText([string]$Path, [string]$Text) {
    # Writes a text file under a temporary name and moves it into place, so a reader finds the whole
    # old or the whole new file.
    $folder = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $temporary = Join-Path $folder (".{0}.{1}.tmp" -f (Split-Path -Leaf $Path), $PID)
    [System.IO.File]::WriteAllText($temporary, $Text, (New-Object System.Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        # No backup file: PowerShell passes $null to a string as an empty one.
        [System.IO.File]::Replace($temporary, $Path, [NullString]::Value)
    }
    else {
        [System.IO.File]::Move($temporary, $Path)
    }
}

function Read-ArtViewBaseline([string]$Path = $script:ArtViewBaselinePath) {
    # The views of the baseline by name, each with its key and its numbers, or $null without a baseline.
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    $json = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    $entries = New-ArtViewTable
    foreach ($entry in @($json.views)) {
        $entries[[string]$entry.name] = [pscustomobject]@{ Name = [string]$entry.name; Key = [string]$entry.key
            Skin = [int]$entry.skin; Inner = [int]$entry.inner; Contact = [int]$entry.contact; Figure = [int]$entry.figure }
    }

    return $entries
}

function Write-ArtViewBaseline($Entries, [string]$Path = $script:ArtViewBaselinePath) {
    # Writes the baseline sorted by name, one line per view, so a diff shows each view that changed.
    $byName = New-ArtViewTable
    foreach ($entry in @($Entries)) {
        $byName[$entry.Name] = $entry
    }

    $sorted = [string[]]@($byName.Keys)
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("{")
    $lines.Add("    `"viewVersion`": $script:ArtViewVersion,")
    $lines.Add('    "views": [')
    for ($index = 0; $index -lt $sorted.Count; $index++) {
        $entry = $byName[$sorted[$index]]
        $comma = if ($index -lt $sorted.Count - 1) { "," } else { "" }
        $lines.Add("        { `"name`": `"$($entry.Name)`", `"key`": `"$($entry.Key)`", `"skin`": $([int]$entry.Skin), `"inner`": $([int]$entry.Inner), `"contact`": $([int]$entry.Contact), `"figure`": $([int]$entry.Figure) }$comma")
    }

    $lines.Add("    ]")
    $lines.Add("}")
    Write-ArtViewText $Path (($lines -join "`n") + "`n")
}

function Merge-ArtViewBaseline($Old, $Measured, [string[]]$Names) {
    # The entries of a baseline that a partial measurement writes: for every view of the set, by its
    # name, the measured entry, or else the old one, which stays as it was; a view with neither has
    # none, and an old entry of a view the set no longer has goes.
    $merged = New-Object System.Collections.Generic.List[object]
    foreach ($name in $Names) {
        if ($null -ne $Measured -and $Measured.ContainsKey($name)) {
            $merged.Add($Measured[$name])
        }
        elseif ($null -ne $Old -and $Old.ContainsKey($name)) {
            $merged.Add($Old[$name])
        }
    }

    return $merged.ToArray()
}

function Get-ArtViewStaleProblems($Views, $Baseline, $Keys) {
    # The problems of a baseline for the views of the set and their committed keys, by name: per
    # outfit and body the views whose entry is missing or of another key, with the assets those views
    # read, so a problem of an asset that waits for an approval is that asset's, and one problem for the
    # entries of views the set does not have; none when the baseline is current.
    $command = "run tools/art/Render-ArtViews.ps1 -WriteBaseline once every asset of these views is approved and exported"
    $where = "art/Views/baseline.json"
    if ($null -eq $Baseline) {
        return @([pscustomobject]@{ Assets = [string[]]@(); Problem = "${where} is missing; $command." })
    }

    $found = New-Object System.Collections.Generic.List[object]
    $groups = [ordered]@{}
    foreach ($view in $Views) {
        $entry = $Baseline[$view.Name]
        if ($null -ne $entry -and $entry.Key -ceq $Keys[$view.Name]) {
            continue
        }

        $group = "$($view.Outfit) on $($view.Label)"
        if (-not $groups.Contains($group)) {
            $groups[$group] = [pscustomobject]@{ Views = New-Object System.Collections.Generic.List[string]; Assets = [string[]]@() }
        }

        $groups[$group].Views.Add($view.Name)
        $groups[$group].Assets = [string[]]@(@($groups[$group].Assets) + @($view.Assets) | Sort-Object -Unique)
    }

    foreach ($group in $groups.Keys) {
        $names = $groups[$group].Views
        $some = @($names | Select-Object -First 3) -join ", "
        $more = if ($names.Count -gt 3) { ", and $($names.Count - 3) more" } else { "" }
        $found.Add([pscustomobject]@{
                Assets = $groups[$group].Assets
                Problem = "${where}: $($names.Count) view(s) of $group were measured on other content than the committed one or not at all: $some$more; $command."
            })
    }

    $known = New-ArtViewTable
    foreach ($view in $Views) {
        $known[$view.Name] = $true
    }

    $extra = [string[]]@($Baseline.Keys | Where-Object { -not $known.ContainsKey($_) })
    [Array]::Sort($extra, [StringComparer]::Ordinal)
    if ($extra.Count -gt 0) {
        $found.Add([pscustomobject]@{
                Assets = [string[]]@()
                Problem = "${where}: names views that tools/art/views/standard-set.json does not have: $(@($extra | Select-Object -First 3) -join ', '); run tools/art/Render-ArtViews.ps1 -WriteBaseline."
            })
    }

    return $found.ToArray()
}

function Get-ArtViewBaselineProblems {
    # Whether the baseline of the views is current for the committed content: every view of the standard
    # set has an entry whose key is the view's committed key, and no entry names a view the set does not
    # have. See Get-ArtViewStaleProblems.
    $views = @(Read-ArtViewSet)
    $keys = New-ArtViewTable
    foreach ($view in $views) {
        $keys[$view.Name] = Get-ArtViewKey $view
    }

    return @(Get-ArtViewStaleProblems $views (Read-ArtViewBaseline) $keys)
}

function Get-ArtViewProblemOwner($Problem, [string[]]$Waiting) {
    # The asset that waits for an approval whose problem a stale view is: the first asset of the views
    # that waits, or $null when none does.
    return @($Problem.Assets | Where-Object { @($Waiting) -ccontains $_ }) | Select-Object -First 1
}

function Get-ArtViewManifestsOf($View) {
    # The manifests of the assets a view reads.
    return @(@($View.Body) + @($View.Garments | ForEach-Object { $_.Template; $_.Texture }) + @($View.Paints) +
        @(if ($null -ne $View.Pose.Clip) { $View.Library }))
}

function Get-ArtViewGate($Views) {
    # Per view, by name, why it cannot be measured for the baseline: an asset it reads whose approvals do
    # not hold for its content, whose export is not of its current content, or whose exported files
    # differ from their record; an empty list when it can. Each asset is asked once.
    $byAsset = New-ArtViewTable
    $gate = New-ArtViewTable
    foreach ($view in $Views) {
        $problems = New-Object System.Collections.Generic.List[string]
        foreach ($manifest in (Get-ArtViewManifestsOf $view)) {
            $asset = $manifest.RelativeDirectory
            if (-not $byAsset.ContainsKey($asset)) {
                $byAsset[$asset] = [string[]]@(@((Get-ArtExportGateProblem $manifest), (Get-ArtExportChangeProblem $manifest)) + @(Get-ArtExportFileProblems $manifest) |
                        Where-Object { $null -ne $_ } | ForEach-Object { "${asset}: $_" })
            }

            foreach ($problem in $byAsset[$asset]) {
                if (-not $problems.Contains($problem)) {
                    $problems.Add($problem)
                }
            }
        }

        $gate[$view.Name] = $problems.ToArray()
    }

    return $gate
}

function Read-ArtViewNumbers([string]$Path, [string]$Key) {
    # The numbers of a cached view, or $null when the entry is missing, broken, or not of this key.
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    try {
        $numbers = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    }
    catch {
        return $null
    }

    if ($null -eq $numbers -or [string]$numbers.key -cne $Key) {
        return $null
    }

    return $numbers
}

function Format-ArtViewLine([string]$Name, $Numbers, [string]$Note) {
    return "  ${Name}: skin $([int]$Numbers.skin), inner $([int]$Numbers.inner), contact $([int]$Numbers.contact), figure $([int]$Numbers.figure) px$Note"
}

function Compare-ArtViews($Entries, $Baseline, [switch]$Writing) {
    # Lists per view what differs from the baseline: more skin or garment through pixels than it, beyond
    # the tolerance, is a regression, fewer an improvement; a change of the contact pixels is listed
    # apart and never fails; and apart from that the views of the clean phases whose baseline has
    # through pixels, the known show-through. Returns 2 when a view regressed, else 1 when a view has no
    # entry, which -Writing, the comparison before a baseline is written, lists as new, else 0.
    $regressions = New-Object System.Collections.Generic.List[string]
    $improvements = New-Object System.Collections.Generic.List[string]
    $contact = New-Object System.Collections.Generic.List[string]
    $changes = New-Object System.Collections.Generic.List[string]
    $absent = New-Object System.Collections.Generic.List[string]
    $known = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $Entries) {
        $name = $entry.View.Name
        $line = if ($null -ne $Baseline) { $Baseline[$name] } else { $null }
        if ($null -eq $line) {
            $absent.Add($name)
            continue
        }

        $other = if ($line.Key -cne $entry.Committed) { " (the baseline was measured on other content)" } else { "" }
        $skin, $inner, $touch = [int]$entry.Numbers.skin, [int]$entry.Numbers.inner, [int]$entry.Numbers.contact
        $was = "skin $($line.Skin), inner $($line.Inner)"
        $now = "skin $skin, inner $inner"
        if ($skin -gt $line.Skin + $script:ArtViewTolerance -or $inner -gt $line.Inner + $script:ArtViewTolerance) {
            $regressions.Add("${name}: $was -> $now px$other")
        }
        elseif ($skin -lt $line.Skin -or $inner -lt $line.Inner) {
            $improvements.Add("${name}: $was -> $now px$other")
        }
        elseif ($skin -ne $line.Skin -or $inner -ne $line.Inner -or [int]$entry.Numbers.figure -ne $line.Figure) {
            $changes.Add("${name}: figure $($line.Figure) -> $([int]$entry.Numbers.figure) px$other")
        }

        if ($touch -ne $line.Contact) {
            $contact.Add("${name}: contact $($line.Contact) -> $touch px$other")
        }

        if ($entry.View.Pose.Phase -ceq "clean" -and $line.Skin + $line.Inner -gt 0) {
            $known.Add("${name}: skin $($line.Skin), inner $($line.Inner) px (contact $($line.Contact) px)")
        }
    }

    $absentTitle = if ($Writing) { "New in the baseline" } else { "Not in the baseline; run tools/art/Render-ArtViews.ps1 -WriteBaseline" }
    foreach ($list in @(@("Regressions, more through pixels than the baseline", $regressions), @("Improvements, fewer through pixels", $improvements),
            @("Contact pixels changed, which never fails the check", $contact), @("Other drawn pixels, the same through pixels", $changes),
            @("Known show-through in the clean phases, from the baseline", $known), @($absentTitle, $absent))) {
        if ($list[1].Count -gt 0) {
            Write-Host "$($list[0]): $($list[1].Count)`n - $($list[1] -join "`n - ")"
        }
    }

    if ($regressions.Count -gt 0) {
        Write-Host "Result: $($regressions.Count) view(s) show more through pixels than the baseline."
        return 2
    }

    if ($absent.Count -gt 0 -and -not $Writing) {
        Write-Host "Result: $($absent.Count) view(s) have no baseline to compare with."
        return 1
    }

    Write-Host "Result: no view shows more through pixels than the baseline."
    return 0
}

function Read-ArtViewKeys([string]$Path) {
    # The keys of the readable copies and sheets, by name; a missing or broken file reads as none.
    $keys = New-ArtViewTable
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $keys
    }

    try {
        $json = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
        foreach ($property in $json.PSObject.Properties) {
            $keys[$property.Name] = [string]$property.Value
        }
    }
    catch {
        return New-ArtViewTable
    }

    return $keys
}

function Write-ArtViewKeys([string]$Path, $Keys) {
    $names = [string[]]@($Keys.Keys)
    [Array]::Sort($names, [StringComparer]::Ordinal)
    $lines = @(for ($index = 0; $index -lt $names.Count; $index++) {
            $comma = if ($index -lt $names.Count - 1) { "," } else { "" }
            "  `"$($names[$index])`": `"$($Keys[$names[$index]])`"$comma"
        })
    Write-ArtViewText $Path ("{`n" + ($lines -join "`n") + $(if ($lines.Count -gt 0) { "`n" } else { "" }) + "}`n")
}

function Remove-ArtViewCacheEntries([string]$CacheRoot, $Keep, [DateTime]$Now = (Get-Date)) {
    # The upkeep of the views cache, below views of the cache root and nowhere else: removes every entry,
    # its numbers <key>.json with its pictures, that no run took for ArtViewCacheDays, by the time of its
    # numbers, which a run that takes it touches, and whose key is not one to keep; and, after a day, a
    # picture without its numbers and a file an interrupted run left under its temporary name. Returns
    # how many entries went.
    $folder = Join-Path $CacheRoot "views"
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) {
        return 0
    }

    $limit = $Now.AddDays(-$script:ArtViewCacheDays)
    $day = $Now.AddDays(-1)
    $removed = 0
    foreach ($numbers in @(Get-ChildItem -LiteralPath $folder -Filter "*.json" -File | Where-Object { -not $_.Name.StartsWith(".") })) {
        $key = [System.IO.Path]::GetFileNameWithoutExtension($numbers.Name)
        if ($numbers.LastWriteTime -ge $limit -or $Keep.ContainsKey($key)) {
            continue
        }

        foreach ($file in @("$key.png", "$key.classes.png", "$key.json")) {
            Remove-Item -LiteralPath (Join-Path $folder $file) -Force -ErrorAction SilentlyContinue
        }

        $removed++
    }

    foreach ($picture in @(Get-ChildItem -LiteralPath $folder -Filter "*.png" -File | Where-Object { -not $_.Name.StartsWith(".") -and $_.LastWriteTime -lt $day })) {
        $key = $picture.Name -replace '(\.classes)?\.png$', ''
        if (-not (Test-Path -LiteralPath (Join-Path $folder "$key.json") -PathType Leaf)) {
            Remove-Item -LiteralPath $picture.FullName -Force -ErrorAction SilentlyContinue
        }
    }

    foreach ($left in @(Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Name.StartsWith(".") -and $_.Name -like "*.tmp*" -and $_.LastWriteTime -lt $day })) {
        Remove-Item -LiteralPath $left.FullName -Force -ErrorAction SilentlyContinue
    }

    return $removed
}
