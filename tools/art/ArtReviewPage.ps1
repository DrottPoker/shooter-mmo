# The review page of tools/art/Build-Art.ps1: one HTML file, artifacts/art-build/review/index.html, with
# the pictures it shows copied beside it, that the owner of the art opens after a build. It says what
# waits for his approval with the commands that approve it, what changed in each asset that waits, how
# the game views that changed look before and after with their numbers, worse ones first, and what the
# build exported and measured. It never approves anything. The page is plain HTML and CSS, readable
# without a script and from the file system, written whole on every build. Dot-source it after
# ArtPipelineCommon.ps1 and ArtViews.ps1; tools/ci/Test-ArtViews.ps1 checks it without Blender. See
# docs/ART_PIPELINE.md, Build The Art.

$script:ArtReviewFolder = Join-Path $script:ArtRepositoryRoot "artifacts/art-build/review"

function ConvertTo-ArtHtml([string]$Text) {
    # Text as HTML, every character that HTML reads encoded, also for an attribute.
    return [System.Net.WebUtility]::HtmlEncode($Text)
}

function Get-ArtReviewKindWords([string]$Kind, [bool]$IsBody = $false) {
    # An asset's kind in plain words.
    if ($IsBody) {
        return "body"
    }

    switch ($Kind) {
        "static" { return "model" }
        "skinned" { return "skinned model" }
        "animations" { return "animation library" }
        "paint" { return "paint layer" }
        "garment" { return "garment template" }
        "texture" { return "garment texture" }
        "cage" { return "garment cage" }
        default { return $Kind }
    }
}

function Get-ArtReviewPartWords([string]$Part) {
    # One part of an asset's content in plain words.
    if ($Part.StartsWith("clip:")) {
        return "the clip $($Part.Substring(5))"
    }

    if ($Part.StartsWith("body:")) {
        return "the paint on $($Part.Substring(5))"
    }

    if ($Part.StartsWith("fit:")) {
        return "the fit to $($Part.Substring(4))"
    }

    switch ($Part) {
        "shape" { return "the shape" }
        "uv" { return "the UVs" }
        "materials" { return "the materials" }
        "weights" { return "the weights" }
        "skeleton" { return "the skeleton" }
        "image" { return "the image" }
        "material" { return "the material" }
        "templateUv" { return "the UVs it is painted in" }
        "canvas" { return "the canvas" }
        "paint" { return "the paint" }
        "net" { return "the cage net" }
        default { return $Part }
    }
}

function Join-ArtReviewWords([string[]]$Words) {
    # A list in plain words: "a", "a and b", "a, b, and c".
    $list = @($Words | Where-Object { $_ })
    if ($list.Count -le 1) {
        return ($list -join "")
    }

    if ($list.Count -eq 2) {
        return "$($list[0]) and $($list[1])"
    }

    return "$(@($list | Select-Object -First ($list.Count - 1)) -join ', '), and $($list[-1])"
}

function Format-ArtReviewChange([string[]]$Parts, $Followed, [bool]$New = $false) {
    # One sentence of what changed in an asset: the parts of its own content that differ from the
    # committed asset.json, in words, and the assets it follows whose content changed with theirs, such as
    # "Changed: the image. It follows the new UVs of Trousers." Followed is a list of objects with Name and
    # Parts. A texture's UVs it is painted in are said as following its template's.
    if ($New) {
        return "A new asset."
    }

    $own = @($Parts | Where-Object { $_ -cne "templateUv" } | ForEach-Object { Get-ArtReviewPartWords $_ })
    $follows = @($Followed | Where-Object { @($_.Parts).Count -gt 0 } | ForEach-Object {
            "the new $(Join-ArtReviewWords @($_.Parts | ForEach-Object { (Get-ArtReviewPartWords $_) -replace '^the ', '' })) of $($_.Name)"
        })
    $sentences = @()
    if ($own.Count -gt 0) {
        $sentences += "Changed: $(Join-ArtReviewWords $own)."
    }

    if ($follows.Count -gt 0) {
        $sentences += "It follows $(Join-ArtReviewWords $follows)."
    }

    if ($sentences.Count -eq 0) {
        return "Its content is the committed one."
    }

    return $sentences -join " "
}

function Get-ArtReviewPoseWords($View) {
    # A view's pose and camera in plain words, such as "Walk, frame 3, whole figure from camera Y215".
    $pose = if ($null -ne $View.Pose.Clip) { "$($View.Pose.Clip), frame $($View.Pose.Frame)" } else { "Rest pose" }
    $frame = switch ($View.Camera.Frame) {
        "figure" { "whole figure" }
        "head" { "head up close" }
        "waist" { "waist up close" }
        "feet" { "feet" }
        default { [string]$View.Camera.Frame }
    }
    return "$pose, $frame from camera $($View.Camera.Name)"
}

function Get-ArtReviewViews($Views, $Baseline, $Keys, $Rendered, $Reasons, [string]$CacheRoot) {
    # The game views that changed, each a view whose committed key differs from its baseline entry's key,
    # with its numbers before, from the baseline, and its pictures before, from the cache entry the index
    # notes for the baseline's key, if the cache still has it; its numbers and pictures after from
    # Rendered, by name, or the reason from Reasons, by name, why it was not drawn; and its class as
    # -Check takes it: worse, better, other, or new without an entry before. A view of a clean phase that
    # still shows skin or an inner garment after is marked. Views whose entry is current are left out.
    $items = New-Object System.Collections.Generic.List[object]
    $place = 0
    foreach ($view in $Views) {
        $place++
        $line = if ($null -ne $Baseline) { $Baseline[$view.Name] } else { $null }
        if ($null -ne $line -and $line.Key -ceq $Keys[$view.Name]) {
            continue
        }

        $after = if ($null -ne $Rendered) { $Rendered[$view.Name] } else { $null }
        $class = if ($null -eq $after) { "missing" } elseif ($null -eq $line) { "new" } else { Get-ArtViewChangeClass $line $after.Numbers }
        $reason = if ($null -eq $after) {
            if ($null -ne $Reasons -and $Reasons.ContainsKey($view.Name)) { [string]$Reasons[$view.Name] } else { "It was not drawn in this build." }
        }
        else {
            $null
        }

        $items.Add([pscustomobject]@{
                Name = $view.Name
                Place = $place
                Group = "$($view.Outfit) on $($view.Label)"
                Words = Get-ArtReviewPoseWords $view
                Phase = $view.Pose.Phase
                Before = $line
                BeforePictures = if ($null -ne $line -and -not [string]::IsNullOrEmpty($CacheRoot)) { Get-ArtViewPictures $CacheRoot $line.Key } else { $null }
                After = if ($null -ne $after) { $after.Numbers } else { $null }
                AfterPictures = if ($null -ne $after) { [pscustomobject]@{ Look = $after.Look; Classes = $after.Classes } } else { $null }
                Class = $class
                Reason = $reason
                StillThrough = $null -ne $after -and $view.Pose.Phase -ceq "clean" -and [int]$after.Numbers.skin + [int]$after.Numbers.inner -gt 0
                ThroughBefore = $null -ne $line -and $view.Pose.Phase -ceq "clean" -and [int]$line.Skin + [int]$line.Inner -gt 0
                SamePicture = $false
            })
    }

    return $items.ToArray()
}

function Format-ArtNotExported($Entry) {
    # What an asset of the build's plan that was not exported waits for, in one plain clause, from its need,
    # its stages that wait, and the assets it follows that it waits for.
    $words = switch ($Entry.Need) {
        "approval" { "waits for your approval of its $(Join-ArtReviewWords @($Entry.Stages | ForEach-Object { "'$_'" })) stage(s)" }
        "waits" { "waits for the approval of $(Join-ArtReviewWords @($Entry.Follows)), which it follows" }
        "later" { "waits for $(Join-ArtReviewWords @($Entry.Follows)), which the build fits or exports first" }
        default { "was not exported, since the build stopped before" }
    }

    return $words
}

function Remove-ArtReviewCommands([string]$Text) {
    # A reason of the pipeline without its clauses that tell to run a script, since the page's only
    # commands are the approvals, the build, and the way back.
    $clauses = @($Text -split ';\s*' | Where-Object { $_ -notmatch 'tools[\\/]art[\\/]' })
    return ($clauses -join "; ").TrimEnd('.', ' ', ';')
}

function Format-ArtBaselineChange([string[]]$Names, $Before, $After, $Phase) {
    # What a measurement of the baseline did to its numbers, from the baseline before and after it: one
    # line over every measured view, then one per outfit and body, each with the skin and inner pixels of
    # the poses where nothing may come through, before, where a view new to the baseline counts 0, and
    # after. Phase gives each view's phase by name.
    $names = @($Names | Where-Object { $_ })
    if ($names.Count -eq 0) {
        return @()
    }

    function Format-ArtCleanSum([string[]]$Group) {
        $clean = @($Group | Where-Object { $Phase[$_] -ceq "clean" })
        $was = @($clean | ForEach-Object { if ($null -ne $Before -and $null -ne $Before[$_]) { $Before[$_] } })
        $now = @($clean | ForEach-Object { $After[$_] })
        $sum = { param($List, $Field) [int](($List | ForEach-Object { [int]$_.$Field } | Measure-Object -Sum).Sum) }
        return "in the poses where nothing may come through, skin $(& $sum $was 'Skin') -> $(& $sum $now 'Skin') px and inner $(& $sum $was 'Inner') -> $(& $sum $now 'Inner') px."
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("All $($names.Count) measured view(s): $(Format-ArtCleanSum $names)")
    foreach ($group in @($names | Group-Object { $parts = $_.Split("_"); "$($parts[0]) on $($parts[1])" })) {
        $lines.Add("$($group.Name): $($group.Count) view(s) measured; $(Format-ArtCleanSum ([string[]]@($group.Group)))")
    }

    return $lines.ToArray()
}

function Get-ArtReviewDelta($Item) {
    # How much more skin and inner garment comes through after the change than before, together.
    return ([int]$Item.After.skin - [int]$Item.Before.Skin) + ([int]$Item.After.inner - [int]$Item.Before.Inner)
}

function Get-ArtRejectPaths([string[]]$StatusLines, [string[]]$AssetFolders, [string[]]$CommittedFolders) {
    # What restores every art file that differs from Git, from the lines of git status --porcelain -- art:
    # Restore, the asset folders below art/ that hold a tracked file that differs from HEAD, each the
    # folder that holds its asset.json, or the file itself outside any asset folder, never below
    # art/Views; and New, the asset folders whose files are not in Git, which the restore leaves. The
    # asset folders are those there are now, AssetFolders, and those HEAD has, CommittedFolders, as
    # repository paths such as art/Garments/Shirt.
    $restore = New-Object 'System.Collections.Generic.SortedSet[string]' ([StringComparer]::Ordinal)
    $new = New-Object 'System.Collections.Generic.SortedSet[string]' ([StringComparer]::Ordinal)
    # The longest folder first, so a path takes the folder that holds it most closely.
    $folders = @(@($AssetFolders) + @($CommittedFolders) | Where-Object { $_ } | Select-Object -Unique | Sort-Object -Property Length -Descending)
    foreach ($line in @($StatusLines | Where-Object { $_ -and $_.Length -gt 3 })) {
        $code = $line.Substring(0, 2)
        foreach ($path in @($line.Substring(3) -split ' -> ' | ForEach-Object { $_.Trim().Trim('"').Replace('\', '/').TrimEnd('/') })) {
            if (-not $path.StartsWith("art/") -or $path -ceq "art/Views" -or $path.StartsWith("art/Views/")) {
                continue
            }

            $folder = @($folders | Where-Object { $path -ceq $_ -or $path.StartsWith("$_/") }) | Select-Object -First 1
            if ($code -ceq "??") {
                # A file that is not in Git: only an asset folder that HEAD lacks is named.
                $inside = if ($null -ne $folder) { @($folder) } else { @($AssetFolders | Where-Object { $_.StartsWith("$path/") }) }
                foreach ($candidate in $inside) {
                    if (@($CommittedFolders) -cnotcontains $candidate) {
                        $null = $new.Add($candidate)
                    }
                }
            }
            elseif ($null -ne $folder) {
                $null = $restore.Add($folder)
            }
            else {
                $null = $restore.Add($path)
            }
        }
    }

    return [pscustomobject]@{ Restore = [string[]]@($restore); New = [string[]]@($new) }
}

function Read-ArtReviewBitmap([string]$Path) {
    # A picture read into memory, so the file in the cache stays free.
    Add-Type -AssemblyName System.Drawing
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

function Get-ArtReviewPixelHash([string]$Path) {
    # The SHA-256 of a picture's pixels with its size, whatever its file holds besides them.
    $bitmap = Read-ArtReviewBitmap $Path
    try {
        $rectangle = New-Object System.Drawing.Rectangle 0, 0, $bitmap.Width, $bitmap.Height
        $data = $bitmap.LockBits($rectangle, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $bytes = New-Object byte[] ($data.Stride * $data.Height)
            [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
        }
        finally {
            $bitmap.UnlockBits($data)
        }

        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            return "$($bitmap.Width)x$($bitmap.Height):" + (($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString("x2") }) -join "")
        }
        finally {
            $sha.Dispose()
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

function Copy-ArtReviewClasses([string]$Source, [string]$Target) {
    # A class picture with its black, the figure where nothing comes through, made transparent, so it lies
    # over the look and shows only the red, green, and blue pixels.
    $bitmap = Read-ArtReviewBitmap $Source
    try {
        $bitmap.MakeTransparent([System.Drawing.Color]::Black)
        $bitmap.Save($Target, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

function Format-ArtReviewDifference([int]$Before, [int]$After, [bool]$Counts) {
    # The difference of one number as HTML: more of what makes a view worse in the class worse, less in
    # better.
    $delta = $After - $Before
    $text = if ($delta -gt 0) { "+$delta" } elseif ($delta -lt 0) { "$delta" } else { "0" }
    $class = if (-not $Counts -or $delta -eq 0) { "" } elseif ($delta -gt 0) { " class=`"worse`"" } else { " class=`"better`"" }
    return "<td$class>$(ConvertTo-ArtHtml $text)</td>"
}

function Get-ArtReviewStyle {
    return @"
:root { color-scheme: light dark; --text: #1d1d1f; --muted: #5f6368; --page: #ffffff; --panel: #f3f4f6; --line: #d6d8dc;
  --worse: #b3261e; --better: #1e7b34; --code: #f6f8fa; --link: #0b57d0; }
@media (prefers-color-scheme: dark) {
  :root { --text: #e8eaed; --muted: #a8adb4; --page: #17181a; --panel: #222427; --line: #3a3d42;
    --worse: #f28b82; --better: #81c995; --code: #1f2124; --link: #8ab4f8; } }
* { box-sizing: border-box; }
body { margin: 0; background: var(--page); color: var(--text);
  font: 16px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif; }
main { max-width: 1600px; margin: 0 auto; padding: 16px; }
.text, h1, h2, h3, h4, p, ul, pre, table, details > summary { max-width: 72rem; }
h1 { font-size: 1.8rem; margin: 0.2em 0; }
h2 { font-size: 1.4rem; margin-top: 2em; border-bottom: 1px solid var(--line); padding-bottom: 0.2em; }
h3 { font-size: 1.2rem; margin-top: 1.6em; }
h4 { font-size: 1.05rem; margin-top: 1.4em; }
a { color: var(--link); }
.muted { color: var(--muted); }
pre { background: var(--code); border: 1px solid var(--line); border-radius: 6px; padding: 12px; overflow-x: auto; }
code { font-family: ui-monospace, "Cascadia Mono", Consolas, monospace; font-size: 0.92rem; }
.failure { border-left: 4px solid var(--worse); padding: 8px 12px; background: var(--panel); white-space: pre-wrap; }
.asset, .view { border: 1px solid var(--line); border-radius: 8px; padding: 12px 16px; margin: 16px 0; background: var(--panel); }
.view h5 { font-size: 1rem; margin: 0 0 8px 0; }
.asset h3 { margin-top: 0; }
.kind { color: var(--muted); font-weight: normal; font-size: 0.95rem; }
table.numbers { border-collapse: collapse; margin: 8px 0; display: block; overflow-x: auto; }
table.numbers th, table.numbers td { border: 1px solid var(--line); padding: 2px 10px; text-align: right; }
table.numbers th:first-child, table.numbers td:first-child { text-align: left; }
td.worse, .worse { color: var(--worse); font-weight: 600; }
td.better, .better { color: var(--better); font-weight: 600; }
.pictures { display: grid; grid-template-columns: repeat(3, minmax(0, 512px)); gap: 12px; }
.pictures.two { grid-template-columns: repeat(2, minmax(0, 512px)); }
figure { margin: 0; }
figure img { display: block; width: 100%; height: auto; background: #808080; }
figcaption { color: var(--muted); font-size: 0.9rem; }
.stack { position: relative; }
.stack img.over { position: absolute; left: 0; top: 0; width: 100%; height: 100%; background: transparent; image-rendering: pixelated; }
.sheet img { max-width: 100%; height: auto; display: block; }
.missing { font-style: italic; color: var(--muted); }
.legend span { font-weight: 600; }
"@
}

function Write-ArtReviewPage([string]$Folder, $Review) {
    # Writes the review page whole into Folder, after removing what an earlier build left there, with the
    # pictures it shows copied beside it under the names it refers to, and returns the page's path. Review
    # holds what the build found: Failure, the message of a build that failed; Approvals, per asset that
    # waits in the order to approve it its Asset, Name, Kind words, Change sentence, a Note, Sheet picture
    # or SheetNote, and Commands; Reject, from Get-ArtRejectPaths; Views, from Get-ArtReviewViews, or
    # ViewsNote when the build stopped before it drew them; Exported and NotExported, per asset its Asset
    # and Detail; Baseline, the lines of what was measured; Staging, per asset its Asset and Detail;
    # Problems; and Did, the lines of what the build did.
    if (Test-Path -LiteralPath $Folder) {
        Remove-Item -LiteralPath $Folder -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $Folder | Out-Null
    $html = New-Object System.Text.StringBuilder
    function Add-Html([string]$Text) {
        $null = $html.AppendLine($Text)
    }

    function Add-Picture([string]$Source, [string]$Name, [switch]$Classes) {
        # Copies a picture beside the page and returns its name, as the page refers to it.
        $target = Join-Path $Folder $Name
        if ($Classes) {
            Copy-ArtReviewClasses $Source $target
        }
        else {
            Copy-Item -LiteralPath $Source -Destination $target -Force
        }

        return [Uri]::EscapeDataString($Name)
    }

    $approvals = @($Review.Approvals)
    $commands = @($approvals | ForEach-Object { $_.Commands })
    $failed = -not [string]::IsNullOrEmpty($Review.Failure)
    $views = @($Review.Views)
    $written = (Get-Date).ToString("yyyy-MM-dd HH:mm", [System.Globalization.CultureInfo]::InvariantCulture)
    Add-Html "<!doctype html>"
    Add-Html "<html lang=`"en`"><head><meta charset=`"utf-8`"><meta name=`"viewport`" content=`"width=device-width, initial-scale=1`">"
    Add-Html "<title>Art review</title><style>$(Get-ArtReviewStyle)</style></head><body><main>"
    Add-Html "<h1>Art review</h1>"
    Add-Html "<p class=`"muted`">Written by .\tools\art\Build-Art.ps1 on $(ConvertTo-ArtHtml $written). This page only shows; it approves nothing.</p>"

    # 1. What this is and what to do.
    Add-Html "<h2 id=`"todo`">What to do</h2>"
    if ($failed) {
        # A build that failed asks for the cause to be fixed first; nothing is to be approved before it runs through.
        Add-Html "<p class=`"failure`"><strong>The build failed.</strong> $(ConvertTo-ArtHtml $Review.Failure)</p>"
        Add-Html "<p>Fix the cause, then run the build again: <code>.\tools\art\Build-Art.ps1</code></p>"
        if ($commands.Count -gt 0) {
            Add-Html "<p>Approve nothing until the build runs through; it then lists what waits for your approval.</p>"
        }
    }
    elseif ($commands.Count -gt 0) {
        $stages = if ($commands.Count -eq 1) { "1 review stage" } else { "$($commands.Count) review stages" }
        $assets = if ($approvals.Count -eq 1) { "1 asset" } else { "$($approvals.Count) assets" }
        Add-Html "<p><strong>$stages of $assets wait for your approval.</strong> Look at what changed below. When you accept it, run these commands in PowerShell from the repository root, in this order, and then the build again:</p>"
        Add-Html "<pre><code>$(ConvertTo-ArtHtml ((@($commands) + @('.\tools\art\Build-Art.ps1')) -join "`n"))</code></pre>"
    }
    else {
        Add-Html "<p><strong>Nothing to approve.</strong></p>"
    }

    if ($failed -or $commands.Count -gt 0) {
        # The way back, from what differs from Git, so it holds also after a build that synced nothing
        # and after one that failed.
        $restore = @($Review.Reject.Restore | Where-Object { $_ })
        if ($restore.Count -gt 0) {
            $lead = if ($failed) { "To give the changes up instead" } else { "If you do not accept the changes" }
            Add-Html "<p>$lead, this restores every art file that differs from Git, yours and the ones the build fitted to them; then run the build again:</p>"
            Add-Html "<pre><code>$(ConvertTo-ArtHtml ("git checkout -- $($restore -join ' ')`n.\tools\art\Build-Art.ps1"))</code></pre>"
        }

        $newFolders = @($Review.Reject.New | Where-Object { $_ })
        if ($newFolders.Count -gt 0) {
            Add-Html "<p>$(ConvertTo-ArtHtml "These asset folders are new and not in Git, so the command does not restore them: $(Join-ArtReviewWords $newFolders).")</p>"
        }
    }

    if (@($Review.Did).Count -gt 0) {
        Add-Html "<p>What the build did:</p><ul>"
        foreach ($line in @($Review.Did)) {
            Add-Html "<li>$(ConvertTo-ArtHtml $line)</li>"
        }

        Add-Html "</ul>"
    }

    # 2. Per asset that waits, in the order to approve.
    if ($approvals.Count -gt 0) {
        $title = if ($failed) { "What changed" } else { "What waits for your approval" }
        Add-Html "<h2 id=`"assets`">$title</h2>"
        Add-Html "<p>$(if ($failed) { 'The assets whose content differs from their approval, as far as the build came.' } else { 'In the order to approve them.' })</p>"
        foreach ($approval in $approvals) {
            Add-Html "<div class=`"asset`"><h3>$(ConvertTo-ArtHtml $approval.Name) <span class=`"kind`">$(ConvertTo-ArtHtml "$($approval.Kind), $($approval.Asset)")</span></h3>"
            Add-Html "<p>$(ConvertTo-ArtHtml $approval.Change)</p>"
            if (-not [string]::IsNullOrEmpty($approval.Note)) {
                Add-Html "<p>$(ConvertTo-ArtHtml $approval.Note)</p>"
            }

            if (-not [string]::IsNullOrEmpty($approval.Sheet) -and (Test-Path -LiteralPath $approval.Sheet -PathType Leaf)) {
                $sheet = Add-Picture $approval.Sheet "sheet-$($approval.Name).png"
                Add-Html "<figure class=`"sheet`"><a href=`"$sheet`"><img src=`"$sheet`" alt=`"$(ConvertTo-ArtHtml "Review sheet of $($approval.Name)")`" loading=`"lazy`"></a><figcaption>The review sheet the build rendered; open it at full size by clicking it.</figcaption></figure>"
            }
            elseif (-not [string]::IsNullOrEmpty($approval.SheetNote)) {
                Add-Html "<p class=`"missing`">$(ConvertTo-ArtHtml $approval.SheetNote)</p>"
            }

            if (-not $failed) {
                Add-Html "<pre><code>$(ConvertTo-ArtHtml (@($approval.Commands) -join "`n"))</code></pre>"
            }

            Add-Html "</div>"
        }
    }

    if (-not [string]::IsNullOrEmpty($Review.ViewsNote)) {
        Add-Html "<h2 id=`"views`">How it looks in the game</h2><p class=`"missing`">$(ConvertTo-ArtHtml $Review.ViewsNote)</p>"
    }

    # 3. The game views that changed.
    $drawn = @($views | Where-Object { $_.Class -ne "missing" })
    if ($views.Count -gt 0) {
        # The worse views by how much more comes through, the most first, the better ones by how much less,
        # alike ones in the order of the set.
        $worse = @($drawn | Where-Object { $_.Class -eq "worse" } | Sort-Object -Property @{ Expression = { Get-ArtReviewDelta $_ }; Descending = $true }, @{ Expression = { $_.Place }; Descending = $false })
        $better = @($drawn | Where-Object { $_.Class -eq "better" } | Sort-Object -Property @{ Expression = { Get-ArtReviewDelta $_ }; Descending = $false }, @{ Expression = { $_.Place }; Descending = $false })
        $missing = @($views | Where-Object { $_.Class -eq "missing" })
        foreach ($item in @($drawn | Where-Object { $_.Class -ne "worse" -and $_.Class -ne "better" -and $null -ne $_.BeforePictures })) {
            $item.SamePicture = (Get-ArtReviewPixelHash $item.BeforePictures.Look) -ceq (Get-ArtReviewPixelHash $item.AfterPictures.Look) -and
                [int]$item.Before.Skin -eq [int]$item.After.skin -and [int]$item.Before.Inner -eq [int]$item.After.inner -and
                [int]$item.Before.Contact -eq [int]$item.After.contact -and [int]$item.Before.Figure -eq [int]$item.After.figure
        }

        $same = @($drawn | Where-Object { $_.SamePicture })
        $new = @($drawn | Where-Object { $_.Class -eq "new" })
        $other = @($drawn | Where-Object { $_.Class -eq "other" -and -not $_.SamePicture })
        Add-Html "<h2 id=`"views`">How it looks in the game</h2>"
        $parts = @("$($worse.Count) worse", "$($better.Count) better") + @(if ($other.Count -gt 0) { "$($other.Count) with other pixels but the same show-through" }) +
            @(if ($same.Count -gt 0) { "$($same.Count) that look exactly as before" }) + @(if ($new.Count -gt 0) { "$($new.Count) new" }) +
            @(if ($missing.Count -gt 0) { "$($missing.Count) that could not be drawn" })
        $totals = "$($views.Count) game view(s) changed: $(Join-ArtReviewWords $parts)"

        Add-Html "<p><strong>$(ConvertTo-ArtHtml "$totals.")</strong></p>"
        # Once, not per view: the baseline has skin or inner pixels in many poses where nothing may come through.
        $clean = @($drawn | Where-Object { $_.Phase -ceq "clean" })
        if ($clean.Count -gt 0) {
            $through = @($clean | Where-Object { $_.StillThrough })
            $before = @($through | Where-Object { $_.ThroughBefore }).Count
            Add-Html "<p>$(ConvertTo-ArtHtml "Of the $($clean.Count) changed view(s) in poses where nothing may come through, $($through.Count) show skin or an inner garment after the change; $before of those did before it.")</p>"
        }

        # Per outfit and body: how many views got worse or better, and the pixels before and after, summed.
        $groups = @($drawn | Group-Object Group)
        if ($groups.Count -gt 0) {
            Add-Html "<h3>By outfit and body</h3><table class=`"numbers`"><tr><th>Outfit and body</th><th>Worse</th><th>Better</th><th>Skin before</th><th>Skin after</th><th>Inner before</th><th>Inner after</th></tr>"
            foreach ($group in $groups) {
                $items = @($group.Group)
                $sum = { param($List, $Field) [int](($List | ForEach-Object { [int]$_.$Field } | Measure-Object -Sum).Sum) }
                $withBefore = @($items | Where-Object { $null -ne $_.Before })
                $beforeLines = @($withBefore | ForEach-Object { $_.Before })
                $afterLines = @($items | ForEach-Object { $_.After })
                Add-Html ("<tr><td>$(ConvertTo-ArtHtml $group.Name)</td><td>$(@($items | Where-Object { $_.Class -eq 'worse' }).Count)</td><td>$(@($items | Where-Object { $_.Class -eq 'better' }).Count)</td>" +
                    "<td>$(& $sum $beforeLines 'Skin')</td><td>$(& $sum $afterLines 'skin')</td><td>$(& $sum $beforeLines 'Inner')</td><td>$(& $sum $afterLines 'inner')</td></tr>")
            }

            Add-Html "</table>"
        }

        if ($worse.Count -gt 0) {
            Add-Html "<h3>The worse views, the most first</h3><table class=`"numbers`"><tr><th>View</th><th>Skin through, difference</th><th>Inner through, difference</th></tr>"
            foreach ($item in $worse) {
                Add-Html ("<tr><td><a href=`"#v-$(ConvertTo-ArtHtml $item.Name)`">$(ConvertTo-ArtHtml $item.Name)</a></td>" +
                    (Format-ArtReviewDifference $item.Before.Skin $item.After.skin $true) + (Format-ArtReviewDifference $item.Before.Inner $item.After.inner $true) + "</tr>")
            }

            Add-Html "</table>"
        }

        Add-Html "<p class=`"legend`">Each view is drawn as the game draws it, before your change and after it, and once more after it with what comes through: <span class=`"worse`">red</span> where skin shows through a garment, <span class=`"better`">green</span> where an inner garment shows through the one over it, and <span style=`"color:#3b82f6`">blue</span> for contact, where a limb presses into cloth over another part of the body in motion. More red or green pixels make a view worse; contact is counted apart and never does.</p>"

        function Add-ViewGroup([string]$Title, $Items, [switch]$Folded) {
            if (@($Items).Count -eq 0) {
                return
            }

            if ($Folded) {
                Add-Html "<details><summary><strong>$(ConvertTo-ArtHtml "$Title ($(@($Items).Count))")</strong></summary>"
            }
            else {
                Add-Html "<h3>$(ConvertTo-ArtHtml "$Title ($(@($Items).Count))")</h3>"
            }

            foreach ($group in @($Items | Group-Object Group)) {
                Add-Html "<h4>$(ConvertTo-ArtHtml $group.Name)</h4>"
                foreach ($item in $group.Group) {
                    Add-ViewItem $item
                }
            }

            if ($Folded) {
                Add-Html "</details>"
            }
        }

        function Add-RankedGroup([string]$Title, $Items, [string]$Rest) {
            # The first 12 in full, in their order, the others folded, still in that order.
            if (@($Items).Count -eq 0) {
                return
            }

            Add-Html "<h3>$(ConvertTo-ArtHtml "$Title ($(@($Items).Count))")</h3>"
            foreach ($item in @($Items | Select-Object -First 12)) {
                Add-ViewItem $item
            }

            $others = @($Items | Select-Object -Skip 12)
            if ($others.Count -gt 0) {
                Add-Html "<details><summary><strong>$(ConvertTo-ArtHtml "The other $($others.Count) $Rest, with smaller differences")</strong></summary>"
                foreach ($item in $others) {
                    Add-ViewItem $item
                }

                Add-Html "</details>"
            }
        }

        function Add-ViewItem($Item) {
            Add-Html "<div class=`"view`" id=`"v-$(ConvertTo-ArtHtml $Item.Name)`"><h5>$(ConvertTo-ArtHtml $Item.Name) <span class=`"kind`">$(ConvertTo-ArtHtml $Item.Words)</span></h5>"
            if ($Item.Phase -ceq "rare") {
                Add-Html "<p class=`"muted`">A rare pose, where some show-through is accepted.</p>"
            }

            Add-Html "<table class=`"numbers`"><tr><th>Pixels</th><th>Skin through</th><th>Inner through</th><th>Contact</th><th>Figure</th></tr>"
            if ($null -ne $Item.Before) {
                Add-Html "<tr><td>Before</td><td>$([int]$Item.Before.Skin)</td><td>$([int]$Item.Before.Inner)</td><td>$([int]$Item.Before.Contact)</td><td>$([int]$Item.Before.Figure)</td></tr>"
            }
            else {
                Add-Html "<tr><td>Before</td><td colspan=`"4`" class=`"missing`">This view has no numbers from before.</td></tr>"
            }

            if ($null -ne $Item.After) {
                Add-Html "<tr><td>After</td><td>$([int]$Item.After.skin)</td><td>$([int]$Item.After.inner)</td><td>$([int]$Item.After.contact)</td><td>$([int]$Item.After.figure)</td></tr>"
                if ($null -ne $Item.Before) {
                    Add-Html ("<tr><td>Difference</td>" + (Format-ArtReviewDifference $Item.Before.Skin $Item.After.skin $true) + (Format-ArtReviewDifference $Item.Before.Inner $Item.After.inner $true) +
                        (Format-ArtReviewDifference $Item.Before.Contact $Item.After.contact $false) + (Format-ArtReviewDifference $Item.Before.Figure $Item.After.figure $false) + "</tr>")
                }
            }

            Add-Html "</table>"
            if ($null -eq $Item.After) {
                Add-Html "<p class=`"missing`">$(ConvertTo-ArtHtml $Item.Reason)</p></div>"
                return
            }

            if ($Item.SamePicture) {
                Add-Html "<p class=`"muted`">It looks exactly as before, pixel for pixel.</p></div>"
                return
            }

            $after = Add-Picture $Item.AfterPictures.Look "$($Item.Name).after.png"
            $classes = Add-Picture $Item.AfterPictures.Classes "$($Item.Name).classes.png" -Classes
            $alt = ConvertTo-ArtHtml $Item.Name
            if ($null -ne $Item.BeforePictures) {
                $before = Add-Picture $Item.BeforePictures.Look "$($Item.Name).before.png"
                Add-Html "<div class=`"pictures`"><figure><img src=`"$before`" width=`"512`" height=`"768`" loading=`"lazy`" alt=`"$alt before`"><figcaption>Before</figcaption></figure>"
            }
            else {
                Add-Html "<div class=`"pictures`"><figure><p class=`"missing`">The picture from before is no longer in the cache; the numbers before are the baseline's.</p><figcaption>Before</figcaption></figure>"
            }

            Add-Html "<figure><img src=`"$after`" width=`"512`" height=`"768`" loading=`"lazy`" alt=`"$alt after`"><figcaption>After</figcaption></figure>"
            Add-Html "<figure><div class=`"stack`"><img src=`"$after`" width=`"512`" height=`"768`" loading=`"lazy`" alt=`"$alt after, with what comes through`"><img class=`"over`" src=`"$classes`" width=`"512`" height=`"768`" loading=`"lazy`" alt=`"`"></div><figcaption>After, with what comes through</figcaption></figure></div></div>"
        }

        Add-RankedGroup "Worse" $worse "worse views"
        Add-RankedGroup "Better" $better "better views"
        Add-ViewGroup "New, without numbers from before" $new
        Add-ViewGroup "Other pixels, the same show-through" $other -Folded
        if ($same.Count -gt 0) {
            Add-Html "<details><summary><strong>$(ConvertTo-ArtHtml "Look exactly as before ($($same.Count))")</strong></summary><p>$(ConvertTo-ArtHtml ((@($same | ForEach-Object { $_.Name })) -join ', ')).</p></details>"
        }

        Add-ViewGroup "Could not be drawn" $missing
    }

    # 4. What the build exported and measured, and what it could not.
    Add-Html "<h2 id=`"build`">What this build exported and measured</h2>"
    $lists = @(@("Exported", $Review.Exported), @("Not exported", $Review.NotExported), @("Baseline of the game views", $Review.Baseline),
        @("Data staged for the game views, not exported", $Review.Staging), @("Problems", $Review.Problems))
    if (@($lists | Where-Object { @($_[1] | Where-Object { $null -ne $_ }).Count -gt 0 }).Count -eq 0) {
        Add-Html "<p class=`"muted`">It exported, measured, and staged nothing, and found no problem.</p>"
    }

    foreach ($list in $lists) {
        $lines = @($list[1] | Where-Object { $null -ne $_ })
        if ($lines.Count -eq 0) {
            continue
        }

        Add-Html "<h3>$(ConvertTo-ArtHtml $list[0])</h3>"

        Add-Html "<ul>"
        foreach ($line in $lines) {
            $text = if ($line -is [string]) { $line } else { "$($line.Asset): $($line.Detail)" }
            Add-Html "<li>$(ConvertTo-ArtHtml $text)</li>"
        }

        Add-Html "</ul>"
    }

    Add-Html "</main></body></html>"
    $path = Join-Path $Folder "index.html"
    $temporary = Join-Path $Folder ".index.html.$PID.tmp"
    [System.IO.File]::WriteAllText($temporary, $html.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::Move($temporary, $path)
    return $path
}
