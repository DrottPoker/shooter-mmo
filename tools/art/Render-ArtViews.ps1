# Renders the game views: the outfits of tools/art/views/standard-set.json on the bodies, in the poses
# and from the cameras the set names, drawn as the game draws them, with their numbers, the pixels of
# skin and of an inner garment through the cloth over them, of contact, where that cloth belongs to
# another part of the body, and of the whole figure. -All renders every view, -Outfit, -Body, -Pose, and
# -View, whole view names, choose some; -Jobs runs that many Blender processes at once, each one outfit
# on one body. A view is taken from the cache below the shared art cache's views folder while nothing it
# reads changed; -NoCache renders it again. A view drawn from exactly the data its export records name
# notes in the cache's index which entry holds the pictures of its committed key, so a later change can
# show it as the picture before. -DataFolder puts other data in front of the exported, such as the staged data of
# tools/art/Build-Art.ps1, and -OpaqueTexture names textures whose current content has no transparent
# parts, which read their template's data. -WriteBaseline measures every view whose assets are all
# approved and exported from their current content, with their exported files as recorded, and whose
# entry in art/Views/baseline.json is not current, prints how the numbers differ from the old entries,
# and writes them; the entries of the other views stay as they are. -Check compares the numbers with the
# baseline. -Report gets one record per chosen view with numbers: its name, committed key, cache key,
# numbers, and pictures, for tools/art/Build-Art.ps1. See docs/ART_PIPELINE.md, Game Views.
#
# Exit codes: 0 done, and for -Check no view shows more skin or garment through pixels than the
# baseline; 2 for -Check a view does; 1 an error, or for -Check a view without an entry.
param(
    [switch]$All,
    [string[]]$Outfit = @(),
    [string[]]$Body = @(),
    [string[]]$Pose = @(),
    [string[]]$View = @(),
    [int]$Jobs = 0,
    [switch]$NoCache,
    [switch]$WriteBaseline,
    [switch]$Check,
    [string[]]$DataFolder = @(),
    [string[]]$OpaqueTexture = @(),
    [System.Collections.IList]$Report,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")
. (Join-Path $PSScriptRoot "ArtViews.ps1")

$script:ArtViewsCli = Join-Path $PSScriptRoot "blender/art_views_cli.py"
$culture = [System.Globalization.CultureInfo]::InvariantCulture

function Get-ArtViewBlenderBuild([string]$Blender) {
    # The version and build of the Blender that renders, which the key of a view in the cache holds.
    $lines = @(Invoke-ArtNative $Blender @("--factory-startup", "--version") | Where-Object { $_ -match '^Blender |build hash' } | ForEach-Object { $_.Trim() })
    return $lines -join "; "
}

function Get-ArtViewFileName([string]$Path) {
    try {
        return Get-ArtRelativePath $Path
    }
    catch {
        return [System.IO.Path]::GetFullPath($Path).Replace("\", "/")
    }
}

function Read-ArtViewBitmap([string]$Path) {
    # A picture read into memory, so no file of the cache stays open while a sheet is drawn.
    $stream = New-Object System.IO.MemoryStream (, [System.IO.File]::ReadAllBytes($Path))
    try {
        $source = New-Object System.Drawing.Bitmap $stream
        try {
            return New-Object System.Drawing.Bitmap $source
        }
        finally {
            $source.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Write-ArtViewSheet([string]$Path, $Entries) {
    # One contact sheet of the views of an outfit on a body: per view its look and its look with the
    # class pixels over it, half size, under a line with its pose, camera, and numbers.
    Add-Type -AssemblyName System.Drawing
    $cellWidth, $cellHeight, $labelHeight = 256, 384, 20
    $columns = [Math]::Min(6, $Entries.Count)
    $rows = [Math]::Ceiling($Entries.Count / $columns)
    $sheet = New-Object System.Drawing.Bitmap ([int]($columns * 2 * $cellWidth)), ([int]($rows * ($cellHeight + $labelHeight)))
    $graphics = [System.Drawing.Graphics]::FromImage($sheet)
    $font = New-Object System.Drawing.Font "Segoe UI", 9
    try {
        $graphics.Clear([System.Drawing.Color]::FromArgb(40, 40, 40))
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear
        for ($index = 0; $index -lt $Entries.Count; $index++) {
            $entry = $Entries[$index]
            $x = ($index % $columns) * 2 * $cellWidth
            $y = [Math]::Floor($index / $columns) * ($cellHeight + $labelHeight)
            $label = "$($entry.View.Pose.Name) $($entry.View.Camera.Name)   skin $([int]$entry.Numbers.skin)  inner $([int]$entry.Numbers.inner)  contact $([int]$entry.Numbers.contact)"
            $graphics.DrawString($label, $font, [System.Drawing.Brushes]::White, [float]($x + 4), [float]($y + 2))
            $look = Read-ArtViewBitmap $entry.Look
            $classes = Read-ArtViewBitmap $entry.Classes
            try {
                $graphics.DrawImage($look, (New-Object System.Drawing.Rectangle ([int]$x), ([int]($y + $labelHeight)), $cellWidth, $cellHeight))
                $graphics.DrawImage($look, (New-Object System.Drawing.Rectangle ([int]($x + $cellWidth)), ([int]($y + $labelHeight)), $cellWidth, $cellHeight))
                $classes.MakeTransparent([System.Drawing.Color]::Black)
                $graphics.DrawImage($classes, (New-Object System.Drawing.Rectangle ([int]($x + $cellWidth)), ([int]($y + $labelHeight)), $cellWidth, $cellHeight))
            }
            finally {
                $look.Dispose()
                $classes.Dispose()
            }
        }

        $temporary = Join-Path (Split-Path -Parent $Path) (".{0}.{1}.tmp.png" -f (Split-Path -Leaf $Path), $PID)
        $sheet.Save($temporary, [System.Drawing.Imaging.ImageFormat]::Png)
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    }
    finally {
        $font.Dispose()
        $graphics.Dispose()
        $sheet.Dispose()
    }
}

function Get-ArtViewExportNotes($Entries, [string]$ExportFolder) {
    # One line per outfit and body that reads the data of a garment's last export while the garment's
    # content changed since then: until the next part brings staging data, such a view is drawn with
    # that older data.
    $notes = New-Object System.Collections.Generic.List[string]
    $root = [System.IO.Path]::GetFullPath($ExportFolder).TrimEnd('\', '/')
    foreach ($group in @($Entries | Group-Object { "$($_.View.Outfit) on $($_.View.Label)" })) {
        $stale = New-Object System.Collections.Generic.List[string]
        foreach ($garment in $group.Group[0].Garments) {
            if (-not [System.IO.Path]::GetFullPath($garment.Data).StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $own = [System.IO.Path]::GetFileName($garment.Data) -ceq "$($garment.Texture.Name)_$($group.Group[0].View.Label).json"
            foreach ($manifest in @(if ($own) { $garment.Texture }) + @($garment.Template)) {
                $problem = Get-ArtExportChangeProblem $manifest
                if ($null -ne $problem -and -not $stale.Contains($manifest.RelativeDirectory)) {
                    $stale.Add($manifest.RelativeDirectory)
                }
            }
        }

        if ($stale.Count -gt 0) {
            $notes.Add("$($group.Name): $($stale -join ', ') changed since its last export; drawn with the data of its last export.")
        }
    }

    return $notes.ToArray()
}

function Invoke-ArtViewRender($Views, $EveryView, $Committed) {
    # Renders the views, or takes them from the cache, writes their readable copies and contact sheets,
    # keeps the cache, and returns every view's entry with its numbers. A view of the set that the run
    # does not render keeps its entry in the cache while it can be read as the files are now.
    $blender = Resolve-ArtBlender $BlenderPath
    $build = Get-ArtViewBlenderBuild $blender
    $code = Get-ArtViewCode
    $exportFolder = Join-Path $script:ArtRepositoryRoot $script:ArtViewDataFolder
    # The folders of -DataFolder come before the export's own.
    $folders = @(@(Split-ArtViewList $DataFolder | ForEach-Object { if ([System.IO.Path]::IsPathRooted($_)) { $_ } else { Join-Path $script:ArtRepositoryRoot $_ } }) +
        @($exportFolder))
    $cacheRoot = Get-ArtCacheRoot
    $cache = Join-Path $cacheRoot "views"
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    $count = if ($Jobs -gt 0) { $Jobs } else { Get-ArtDefaultJobs }

    # Every view with what it reads, its keys, and its entry in the cache. The cache keeps the entries of
    # the chosen views and of every other view of the set that has data, as they read the files now.
    $entries = New-Object System.Collections.Generic.List[object]
    $keep = New-ArtViewTable
    $chosen = New-ArtViewTable
    foreach ($view in $Views) {
        $chosen[$view.Name] = $true
    }

    $fileLines = @{}
    $fileHashes = @{}
    $opaque = [string[]]@(Split-ArtViewList $OpaqueTexture)
    foreach ($view in $EveryView) {
        $isChosen = $chosen.ContainsKey($view.Name)
        $folderList = if ($isChosen) { $folders } else { @($exportFolder) }
        $opaqueList = if ($isChosen) { $opaque } else { [string[]]@() }
        try {
            $garments = @($view.Garments | ForEach-Object {
                    [pscustomobject]@{ Texture = $_.Texture; Template = $_.Template; Data = (Find-ArtViewData $_ $view.Label $folderList $opaqueList) }
                })
        }
        catch {
            if ($isChosen) {
                throw
            }

            # A garment without data only leaves this view out of the views the cache keeps.
            continue
        }

        $files = [string[]]@(@($view.Body.SourcePath) + @($garments | ForEach-Object { $_.Template.SourcePath; $_.Texture.SourcePath; $_.Data }) +
            @($view.Paints | ForEach-Object { $_.SourcePath }) + @(if ($null -ne $view.Pose.Clip) { $view.Library.SourcePath }) +
            @(if ($null -ne $view.Rig) { $view.Rig.Manifest.SourcePath }))
        $lines = foreach ($file in $files) {
            if (-not $fileLines.ContainsKey($file)) {
                $fileHashes[$file] = Get-ArtFileSha256 $file
                $fileLines[$file] = "file $(Get-ArtViewFileName $file)=$($fileHashes[$file])"
            }

            $fileLines[$file]
        }

        $key = Get-ArtViewLocalKey $view $code $build ([string[]]$lines)
        $keep[$key] = $true
        if (-not $isChosen) {
            continue
        }

        $entry = [pscustomobject]@{
            View = $view
            Garments = $garments
            Files = $files
            Key = $key
            Committed = $Committed[$view.Name]
            Look = Join-Path $cache "$key.png"
            Classes = Join-Path $cache "$key.classes.png"
            NumbersPath = Join-Path $cache "$key.json"
            Numbers = $null
            Cached = $false
        }
        if (-not $NoCache -and (Test-Path -LiteralPath $entry.Look) -and (Test-Path -LiteralPath $entry.Classes)) {
            $entry.Numbers = Read-ArtViewNumbers $entry.NumbersPath $key
            $entry.Cached = $null -ne $entry.Numbers
            if ($entry.Cached) {
                # The age of an entry for the upkeep is its last use.
                try {
                    [System.IO.File]::SetLastWriteTimeUtc($entry.NumbersPath, [DateTime]::UtcNow)
                }
                catch {
                    # A cache that cannot be touched ages its entries from their writing.
                }
            }
        }

        $entries.Add($entry)
    }

    foreach ($note in @(Get-ArtViewExportNotes $entries $exportFolder)) {
        Write-Host $note
    }

    # One Blender per outfit and body renders its views that the cache lacks.
    $logs = Get-ArtBuildFolder "views"
    $jobs = New-Object System.Collections.Generic.List[object]
    foreach ($group in @($entries | Where-Object { -not $_.Cached } | Group-Object { "$($_.View.Outfit)_$($_.View.Label)" })) {
        $first = $group.Group[0]
        # A body that is not the library's rig follows the rig in the clip poses, through the reference
        # poses of the two avatars.
        $rig = @($group.Group | Where-Object { $null -ne $_.View.Rig } | ForEach-Object { $_.View.Rig }) | Select-Object -First 1
        $body = [ordered]@{ file = $first.View.Body.SourcePath; root = $first.View.Label }
        if ($null -ne $rig) {
            $body["reference"] = $rig.BodyAvatar.Path
        }

        $job = [ordered]@{
            body = $body
            garments = @($first.Garments | ForEach-Object {
                    [ordered]@{ texture = $_.Texture.Name; textureFile = $_.Texture.SourcePath; template = $_.Template.Name
                        templateFile = $_.Template.SourcePath; data = $_.Data }
                })
            paints = @($first.View.Catalog | ForEach-Object {
                    [ordered]@{ name = $_.Name; file = $_.SourcePath; category = $_.Category; slot = $_.Slot; layer = $_.Layer; part = $_.Part
                        named = (@($first.View.Named) -ccontains $_.Name) }
                })
            library = $first.View.Library.SourcePath
            views = @($group.Group | ForEach-Object {
                    $pose = if ($null -ne $_.View.Pose.Clip) { [ordered]@{ clip = $_.View.Pose.Clip; frame = $_.View.Pose.Frame } } else { [ordered]@{} }
                    [ordered]@{ name = $_.View.Name; key = $_.Key; committed = $_.Committed; pose = $pose
                        camera = [ordered]@{ yaw = $_.View.Camera.Yaw; pitch = $_.View.Camera.Pitch; frame = $_.View.Camera.Frame }
                        look = $_.Look; classes = $_.Classes; numbers = $_.NumbersPath }
                })
        }
        if ($null -ne $rig) {
            $job["rig"] = [ordered]@{ file = $rig.Manifest.SourcePath; root = $rig.Manifest.RootObjects[0]; reference = $rig.Avatar.Path
                skeleton = $rig.Manifest.SkeletonPath }
        }

        $jobPath = Join-Path $logs "$($group.Name).job.json"
        [System.IO.File]::WriteAllText($jobPath, ($job | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding($false)))
        $manifest = [pscustomobject]@{ Name = "$($first.View.Outfit) on $($first.View.Label)"; SourcePath = "" }
        $read = [string[]]@($group.Group | ForEach-Object { $_.Files } | Sort-Object -Unique)
        $jobs.Add((New-ArtBlenderJob $manifest @($jobPath) (Join-Path $logs "$($group.Name).log") @{
                    Script = $script:ArtViewsCli; Group = $group.Name; Entries = @($group.Group); Files = $read }))
    }

    $byName = New-ArtViewTable
    foreach ($entry in $entries) {
        $byName[$entry.View.Name] = $entry
    }

    $failures = New-Object System.Collections.Generic.List[string]
    $seconds = [ordered]@{}
    if ($jobs.Count -gt 0) {
        Write-Host "Rendering $(@($entries | Where-Object { -not $_.Cached }).Count) view(s) of $($jobs.Count) outfit(s) and bodies, $count at once."
        $onDone = {
            param($Job)
            $seconds[$Job.Group] = $Job.Seconds
            # A file the job read that changed while it ran makes its pictures of neither version.
            $changed = @($Job.Files | Where-Object { $file = $_; try { (Get-ArtFileSha256 $file) -cne $fileHashes[$file] } catch { $true } })
            if ($changed.Count -gt 0) {
                foreach ($entry in $Job.Entries) {
                    foreach ($path in @($entry.Look, $entry.Classes, $entry.NumbersPath)) {
                        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
                    }

                    $entry.Numbers = $null
                }

                $message = "$($Job.Name): $(Get-ArtViewFileName $changed[0]) changed during the run; run again."
                $failures.Add($message)
                Write-Host $message
                return
            }

            if ($null -ne $Job.Error) {
                $failures.Add($Job.Error)
                Write-Host "$($Job.Name) failed; see $(Get-ArtRelativePath $Job.LogPath)."
                return
            }

            Write-Host "$($Job.Name) took $($Job.Seconds.ToString('0.0', $culture)) s:"
            foreach ($line in @($Job.Results | Where-Object { $_ -like "ART_VIEW *" })) {
                $numbers = $line.Substring("ART_VIEW ".Length) | ConvertFrom-Json
                $entry = $byName[[string]$numbers.name]
                $entry.Numbers = $numbers
                Write-Host (Format-ArtViewLine $entry.View.Name $numbers " ($(([double]$numbers.seconds).ToString('0.00', $culture)) s)")
            }
        }
        Invoke-ArtBlenderJobs $blender $jobs.ToArray() $count $onDone
        foreach ($job in $jobs) {
            if ($null -ne $job.Error -and -not $failures.Contains($job.Error)) {
                $failures.Add($job.Error)
            }
        }
    }

    foreach ($entry in @($entries | Where-Object { $_.Cached })) {
        Write-Host (Format-ArtViewLine $entry.View.Name $entry.Numbers " (cached)")
    }

    $missing = @($entries | Where-Object { $null -eq $_.Numbers -or -not (Test-Path -LiteralPath $_.Look) -or -not (Test-Path -LiteralPath $_.Classes) })
    if ($missing.Count -gt 0) {
        $failures.Add("$($missing.Count) view(s) have no numbers, such as $($missing[0].View.Name).")
    }

    # The readable copies and one contact sheet per outfit and body, each written again only when the
    # keys of its views changed.
    $readable = Join-Path $script:ArtRepositoryRoot "artifacts/art-views"
    New-Item -ItemType Directory -Force -Path $readable | Out-Null
    $done = @($entries | Where-Object { $null -ne $_.Numbers -and (Test-Path -LiteralPath $_.Look) -and (Test-Path -LiteralPath $_.Classes) })
    $keysPath = Join-Path $readable "keys.json"
    $keys = Read-ArtViewKeys $keysPath
    foreach ($entry in $done) {
        $look = Join-Path $readable "$($entry.View.Name).png"
        $numbersCopy = Join-Path $readable "$($entry.View.Name).json"
        if ($keys[$entry.View.Name] -cne $entry.Key -or -not (Test-Path -LiteralPath $look) -or -not (Test-Path -LiteralPath $numbersCopy)) {
            Copy-Item -LiteralPath $entry.Look -Destination $look -Force
            Copy-Item -LiteralPath $entry.Classes -Destination (Join-Path $readable "$($entry.View.Name).classes.png") -Force
            # The numbers with the bone heads of the pose, which tools/art/Capture-GameViews.ps1 holds
            # against the game's.
            Copy-Item -LiteralPath $entry.NumbersPath -Destination $numbersCopy -Force
            $keys[$entry.View.Name] = $entry.Key
        }
    }

    foreach ($group in @($done | Group-Object { "$($_.View.Outfit)_$($_.View.Label)" })) {
        $sheet = Join-Path $readable "$($group.Name).png"
        $content = Get-ArtTextSha256 ((@($group.Group | ForEach-Object { $_.Key }) -join "`n") + "`n")
        if ($keys["sheet:$($group.Name)"] -cne $content -or -not (Test-Path -LiteralPath $sheet)) {
            Write-ArtViewSheet $sheet @($group.Group)
            $keys["sheet:$($group.Name)"] = $content
        }
    }

    Write-ArtViewKeys $keysPath $keys

    # The index of the pictures by committed key, of the views drawn from exactly the data their export
    # records name, as the baseline measures them; the upkeep keeps the entries the baseline's keys
    # point to.
    foreach ($entry in $done) {
        if (Test-ArtViewIndexable $entry.View ([string[]]@($entry.Garments | ForEach-Object { $_.Data })) $fileHashes) {
            Write-ArtViewIndex $cacheRoot $entry.Committed $entry.Key
        }
    }

    Add-ArtViewBaselineKeep $cacheRoot (Read-ArtViewBaseline) $keep
    if ($null -ne $Report) {
        foreach ($entry in $done) {
            $null = $Report.Add([pscustomobject]@{ Name = $entry.View.Name; Committed = $entry.Committed; Key = $entry.Key; Numbers = $entry.Numbers
                    Look = $entry.Look; Classes = $entry.Classes; Cached = $entry.Cached; Data = [string[]]@($entry.Garments | ForEach-Object { $_.Data }) })
        }
    }

    $removed = Remove-ArtViewCacheEntries $cacheRoot $keep
    $skin = [int](($done | ForEach-Object { [int]$_.Numbers.skin } | Measure-Object -Sum).Sum)
    $inner = [int](($done | ForEach-Object { [int]$_.Numbers.inner } | Measure-Object -Sum).Sum)
    $touch = [int](($done | ForEach-Object { [int]$_.Numbers.contact } | Measure-Object -Sum).Sum)
    $through = @($done | Where-Object { [int]$_.Numbers.skin + [int]$_.Numbers.inner -gt 0 }).Count
    $rendered = @($entries | Where-Object { -not $_.Cached }).Count
    Write-Host "$($entries.Count) view(s), $rendered rendered and $($entries.Count - $rendered) from the cache: skin through $skin px and inner through $inner px in $through view(s), contact $touch px. The pictures are in artifacts/art-views."
    foreach ($name in $seconds.Keys) {
        Write-Host "  $name rendered in $($seconds[$name].ToString('0.0', $culture)) s."
    }

    if ($removed -gt 0) {
        Write-Host "Removed $removed entries of the views cache that no run took for $script:ArtViewCacheDays days."
    }

    if ($failures.Count -gt 0) {
        throw "Some views could not be rendered:`n - $($failures -join "`n - ")"
    }

    return $entries.ToArray()
}

function Invoke-ArtViews {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $filtered = (Split-ArtViewList $Outfit).Count + (Split-ArtViewList $Body).Count + (Split-ArtViewList $Pose).Count + (Split-ArtViewList $View).Count -gt 0
    if ($WriteBaseline -and $filtered) {
        throw "-WriteBaseline measures the views of the whole standard set; leave out -Outfit, -Body, -Pose, and -View."
    }

    if ($WriteBaseline -and ($DataFolder.Count -gt 0 -or $OpaqueTexture.Count -gt 0)) {
        throw "-WriteBaseline measures the exported data only; leave out -DataFolder and -OpaqueTexture."
    }

    if (-not ($All -or $filtered -or $WriteBaseline -or $Check)) {
        throw "Name -All, or choose views with -Outfit, -Body, -Pose, or -View."
    }

    $everyView = @(Read-ArtViewSet)
    $views = @(Select-ArtViews $everyView $Outfit $Body $Pose $View)
    $baseline = Read-ArtViewBaseline
    if ($Check -and $null -eq $baseline) {
        throw "art/Views/baseline.json is missing; run tools/art/Render-ArtViews.ps1 -WriteBaseline first."
    }

    $committed = New-ArtViewTable
    foreach ($view in $everyView) {
        $committed[$view.Name] = Get-ArtViewKey $view
    }

    $exit = 0
    if ($WriteBaseline) {
        # The views whose assets are all approved and exported from their current content are measured
        # where their entry is not current; the others keep their entries, with the reason.
        $gate = Get-ArtViewGate $everyView
        foreach ($group in @($everyView | Where-Object { $gate[$_.Name].Count -gt 0 } | Group-Object { "$($_.Outfit) on $($_.Label)" })) {
            $reasons = @($group.Group | ForEach-Object { $gate[$_.Name] } | Sort-Object -Unique)
            Write-Host "$($group.Name): $($group.Count) view(s) keep their entries, since $($reasons -join '; ')."
        }

        $views = @($everyView | Where-Object {
                $gate[$_.Name].Count -eq 0 -and ($null -eq $baseline -or $null -eq $baseline[$_.Name] -or $baseline[$_.Name].Key -cne $committed[$_.Name]) })
        $measured = New-ArtViewTable
        if ($views.Count -gt 0) {
            $entries = @(Invoke-ArtViewRender $views $everyView $committed)
            $null = Compare-ArtViews $entries $baseline -Writing
            foreach ($entry in $entries) {
                $measured[$entry.View.Name] = [pscustomobject]@{ Name = $entry.View.Name; Key = $entry.Committed; Skin = [int]$entry.Numbers.skin
                    Inner = [int]$entry.Numbers.inner; Contact = [int]$entry.Numbers.contact; Figure = [int]$entry.Numbers.figure }
            }
        }
        else {
            Write-Host "Every view whose assets are approved and exported has a current entry; nothing is measured."
        }

        $names = [string[]]@($everyView | ForEach-Object { $_.Name })
        $merged = @(Merge-ArtViewBaseline $baseline $measured $names)
        $kept = @($merged | Where-Object { -not $measured.ContainsKey($_.Name) }).Count
        $before = if (Test-Path -LiteralPath $script:ArtViewBaselinePath -PathType Leaf) { [System.IO.File]::ReadAllText($script:ArtViewBaselinePath) } else { "" }
        Write-ArtViewBaseline $merged
        $state = if ([System.IO.File]::ReadAllText($script:ArtViewBaselinePath) -ceq $before) { "unchanged" } else { "written" }
        Write-Host "The baseline art/Views/baseline.json is $state`: $($measured.Count) view(s) measured now, $kept kept as they were, $($names.Count - $merged.Count) without an entry."
    }
    elseif ($views.Count -gt 0) {
        $entries = @(Invoke-ArtViewRender $views $everyView $committed)
        if ($Check) {
            $exit = Compare-ArtViews $entries $baseline
        }
    }

    Write-Host "Render-ArtViews took $($watch.Elapsed.TotalSeconds.ToString('0.0', $culture)) s."
    return $exit
}

try {
    $code = [int](@(Invoke-ArtViews)[-1])
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}

exit $code
