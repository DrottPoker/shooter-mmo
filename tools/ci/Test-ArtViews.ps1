# Checks the helpers of tools/art/ArtViews.ps1 that decide what the game views of
# tools/art/Render-ArtViews.ps1 key, measure, keep, and report, without Blender: the committed key and
# what changes it, the fingerprint of the renderer's code, the paint layers a body shows, the baseline's
# round trip and partial write, the grouping of stale views and the asset that waits for them, the exit
# codes of the check, the order the data is looked up in, the views' choice, the upkeep of the views
# cache, the keys of the readable copies, and the root of the shared cache.
#
# Exit codes: 0 every case holds, 1 a case failed.
$ErrorActionPreference = "Stop"
$art = Join-Path (Split-Path -Parent $PSScriptRoot) "art"
. (Join-Path $art "ArtPipelineCommon.ps1")
. (Join-Path $art "ArtViews.ps1")

$root = Join-Path ([IO.Path]::GetTempPath()) ("art-views-test-" + [Guid]::NewGuid().ToString("N"))
$failures = @()
$ceiling = $env:GIT_CEILING_DIRECTORIES

function Assert-Equal([string]$Case, $Expected, $Actual) {
    if ("$Expected" -cne "$Actual") {
        $script:failures += "${Case}: expected '$Expected', got '$Actual'."
    }
}

function Assert-True([string]$Case, [bool]$Condition) {
    if (-not $Condition) {
        $script:failures += "${Case}: does not hold."
    }
}

function New-TestContent($Stages, $Parts = @{}) {
    return [pscustomobject]@{ stages = $Stages; parts = $Parts }
}

function New-TestView([string]$Name = "Linen_Male_Walk03_Y215", [string]$Outfit = "Linen", [string]$Label = "Male") {
    # A view of one garment and one paint layer in a frame of a clip, as Read-ArtViewSet gives it.
    $body = [pscustomobject]@{ RelativeDirectory = "art/Characters/Human_$Label"; Stages = @("model", "rig")
        Content = (New-TestContent @{ model = "body-model"; rig = "body-rig" }) }
    $template = [pscustomobject]@{ RelativeDirectory = "art/Garments/Shirt"; Slot = "body"; Layer = 150; Band = "tight"; Hides = @(); HidesUnderwear = $false
        Hair = $null; Recipe = [pscustomobject]@{ hem = 0.004 }; Content = (New-TestContent @{ model = "shirt-model"; rig = "shirt-rig" }) }
    $texture = [pscustomobject]@{ RelativeDirectory = "art/GarmentTextures/LinenShirt"; Content = (New-TestContent @{ texture = "linen-texture" }) }
    $paint = [pscustomobject]@{ RelativeDirectory = "art/Paint/Briefs"; Name = "Briefs"; Category = "underwear"; Slot = "legs"; Layer = 900; Part = "Body"
        Content = (New-TestContent @{ paint = "briefs-paint" }) }
    $library = [pscustomobject]@{ RelativeDirectory = "art/Animations/Human_Actions"
        Content = (New-TestContent @{ animation = "actions" } @{ "clip:Walk" = "walk"; "clip:Jog" = "jog"; skeleton = "skeleton" }) }
    return [pscustomobject]@{
        Name = $Name
        Outfit = $Outfit
        Label = $Label
        Body = $body
        Garments = @([pscustomobject]@{ Template = $template; Texture = $texture; Data = "src/Client/Assets/Art/Models/Garments/Shirt_$Label.json"; DataSha256 = "shirt-data" })
        Paints = @($paint)
        Library = $library
        Pose = [pscustomobject]@{ Name = "Walk03"; Clip = "Walk"; Frame = 3; Phase = "clean" }
        Camera = [pscustomobject]@{ Name = "Y215"; Yaw = 215.0; Pitch = 20.0; Frame = "figure" }
        Assets = [string[]]@($body.RelativeDirectory, $template.RelativeDirectory, $texture.RelativeDirectory, $paint.RelativeDirectory, $library.RelativeDirectory)
    }
}

function Get-TestKey($View, [string]$Code = "code-1") {
    return Get-ArtTextSha256 (Get-ArtViewKeyText $View $Code)
}

function Set-TestAge([string]$Path, [double]$Days) {
    [IO.File]::SetLastWriteTime($Path, (Get-Date).AddDays(-$Days))
}

function New-TestFile([string]$Path, [string]$Text = "x", [double]$Days = 0) {
    $null = New-Item -ItemType Directory -Force (Split-Path -Parent $Path)
    [IO.File]::WriteAllText($Path, $Text)
    if ($Days -gt 0) {
        Set-TestAge $Path $Days
    }
}

function New-TestEntry([string]$Name, [int]$Skin, [int]$Inner, [int]$Contact, [int]$Figure = 1000, [string]$Phase = "clean") {
    return [pscustomobject]@{ View = [pscustomobject]@{ Name = $Name; Pose = [pscustomobject]@{ Phase = $Phase } }; Committed = "k-$Name"
        Numbers = [pscustomobject]@{ skin = $Skin; inner = $Inner; contact = $Contact; figure = $Figure } }
}

function New-TestLine([string]$Name, [int]$Skin, [int]$Inner, [int]$Contact, [int]$Figure = 1000) {
    return [pscustomobject]@{ Name = $Name; Key = "k-$Name"; Skin = $Skin; Inner = $Inner; Contact = $Contact; Figure = $Figure }
}

try {
    $null = New-Item -ItemType Directory -Force $root
    $env:GIT_CEILING_DIRECTORIES = $root

    # The committed key: a field of a template's asset.json, a content fingerprint, the data of an
    # export, and the code change it; another clip's part does not.
    $base = Get-TestKey (New-TestView)
    Assert-Equal "The same view gives the same key" $base (Get-TestKey (New-TestView))
    $view = New-TestView
    $view.Garments[0].Template.Layer = 160
    Assert-True "A template's layer changes the key" ((Get-TestKey $view) -cne $base)
    $view = New-TestView
    $view.Garments[0].Template.HidesUnderwear = $true
    Assert-True "Whether a template hides the underwear changes the key" ((Get-TestKey $view) -cne $base)
    $view = New-TestView
    $view.Garments[0].Template.Band = "loose"
    Assert-True "A template's band, whose reach decides contact, changes the key" ((Get-TestKey $view) -cne $base)
    $view = New-TestView
    $view.Garments[0].Texture.Content.stages.texture = "other-texture"
    Assert-True "A texture's content changes the key" ((Get-TestKey $view) -cne $base)
    $view = New-TestView
    $view.Body.Content.stages.rig = "other-rig"
    Assert-True "The body's last stage changes the key" ((Get-TestKey $view) -cne $base)
    $view = New-TestView
    $view.Garments[0].DataSha256 = "other-data"
    Assert-True "The data of an export changes the key" ((Get-TestKey $view) -cne $base)
    Assert-True "The renderer's code changes the key" ((Get-TestKey (New-TestView) "code-2") -cne $base)
    $view = New-TestView
    $view.Library.Content.parts["clip:Jog"] = "other-jog"
    Assert-Equal "Another clip's part leaves the key alone" $base (Get-TestKey $view)
    $view = New-TestView
    $view.Library.Content.parts["clip:Walk"] = "other-walk"
    Assert-True "The view's own clip changes the key" ((Get-TestKey $view) -cne $base)
    $view = New-TestView
    $view.Library.Content.parts["skeleton"] = "other-skeleton"
    Assert-True "The skeleton of the clips changes the key" ((Get-TestKey $view) -cne $base)
    $text = Get-ArtViewKeyText (New-TestView) "code-1"
    Assert-True "The key text names the code and the pinned Blender" ($text.Contains("`ncode code-1`n") -and $text.Contains("`nblender $script:ArtBlenderVersion`n"))
    Assert-True "The local key holds the committed text and the files" ((Get-ArtViewLocalKey (New-TestView) "code-1" "Blender 5.2.2" @("file a=1")) -cne
        (Get-ArtViewLocalKey (New-TestView) "code-1" "Blender 5.2.2" @("file a=2")))

    # The fingerprint of the code: every file below the paths by its path and its text with LF line ends.
    $code = Join-Path $root "code"
    New-TestFile (Join-Path $code "tools/art/blender/art_pipeline/game_view.py") "a = 1`r`nb = 2`r`n"
    New-TestFile (Join-Path $code "tools/art/blender/human_base/rig.py") "rig`n"
    New-TestFile (Join-Path $code "tools/art/blender/art_views_cli.py") "cli`n"
    New-TestFile (Join-Path $code "art/Skeletons/Humanoid.json") "{}`n"
    New-TestFile (Join-Path $code "tools/art/Render-ArtViews.ps1") "render`r`n"
    New-TestFile (Join-Path $code "tools/art/ArtViews.ps1") "views`r`n"
    $first = Get-ArtViewCodeFingerprint $code
    New-TestFile (Join-Path $code "tools/art/blender/art_pipeline/game_view.py") "a = 1`nb = 2`n"
    Assert-Equal "Line ends do not change the code's fingerprint" $first (Get-ArtViewCodeFingerprint $code)
    New-TestFile (Join-Path $code "tools/art/blender/art_pipeline/__pycache__/game_view.cpython-311.pyc") "cache"
    Assert-Equal "Python's caches are no code" $first (Get-ArtViewCodeFingerprint $code)
    New-TestFile (Join-Path $code "tools/art/blender/human_base/rig.py") "rig 2`n"
    Assert-True "A changed file changes the code's fingerprint" ((Get-ArtViewCodeFingerprint $code) -cne $first)
    New-TestFile (Join-Path $code "tools/art/blender/human_base/rig.py") "rig`n"
    New-TestFile (Join-Path $code "art/Skeletons/Quadruped.json") "{}`n"
    Assert-True "A new skeleton changes the code's fingerprint" ((Get-ArtViewCodeFingerprint $code) -cne $first)
    Remove-Item -LiteralPath (Join-Path $code "art/Skeletons/Quadruped.json")
    Assert-Equal "The fingerprint comes back with the files" $first (Get-ArtViewCodeFingerprint $code)
    New-TestFile (Join-Path $code "tools/art/Render-ArtViews.ps1") "render 2`r`n"
    Assert-True "The script that builds the job changes the code's fingerprint" ((Get-ArtViewCodeFingerprint $code) -cne $first)

    # The paint layers a body shows before the garments: the named ones, then its first underwear of a
    # slot the look names none of; the category without case; and at most four on a material.
    $catalog = @(
        [pscustomobject]@{ Name = "Boxers"; Category = "Underwear"; Slot = "legs"; Layer = 905; Part = "Body" },
        [pscustomobject]@{ Name = "Briefs"; Category = "underwear"; Slot = "legs"; Layer = 900; Part = "Body" },
        [pscustomobject]@{ Name = "Bra"; Category = "UNDERWEAR"; Slot = "body"; Layer = 910; Part = "Body" },
        [pscustomobject]@{ Name = "Tattoo"; Category = "tattoo"; Slot = ""; Layer = 200; Part = "Body" },
        [pscustomobject]@{ Name = "Runes"; Category = "tattoo"; Slot = ""; Layer = 210; Part = "Body" })
    Assert-Equal "The underwear a body always shows" "Briefs Bra" ((Get-ArtViewPaints $catalog @()) | ForEach-Object { $_.Name })
    Assert-Equal "A named underwear and tattoo" "Tattoo Boxers Bra" ((Get-ArtViewPaints $catalog @("Boxers", "Tattoo")) | ForEach-Object { $_.Name })
    Assert-Equal "One tattoo of a slot" "Tattoo Briefs Bra" ((Get-ArtViewPaints $catalog @("Runes", "Tattoo")) | ForEach-Object { $_.Name })
    Assert-Equal "Four on a material" "" "$(Get-ArtViewPaintProblem @($catalog | Select-Object -First 4) 'test')"
    Assert-True "Five on a material" ("$(Get-ArtViewPaintProblem $catalog 'test')" -match "5 on Body")

    # The baseline round trip, and a partial write that keeps what it does not measure.
    $baselinePath = Join-Path $root "views/baseline.json"
    Write-ArtViewBaseline @((New-TestLine "B_Male_Rest_Y215" 1 2 3 400), (New-TestLine "A_Male_Rest_Y215" 0 5 0 300)) $baselinePath
    $read = Read-ArtViewBaseline $baselinePath
    Assert-Equal "Entries read back" "A_Male_Rest_Y215 B_Male_Rest_Y215" (@($read.Keys | Sort-Object) -join " ")
    Assert-Equal "Numbers read back" "1 2 3 400 k-B_Male_Rest_Y215" "$($read['B_Male_Rest_Y215'].Skin) $($read['B_Male_Rest_Y215'].Inner) $($read['B_Male_Rest_Y215'].Contact) $($read['B_Male_Rest_Y215'].Figure) $($read['B_Male_Rest_Y215'].Key)"
    $lines = @([IO.File]::ReadAllLines($baselinePath) | Where-Object { $_ -match '"name"' })
    Assert-True "One sorted line per view" ($lines.Count -eq 2 -and $lines[0] -match "A_Male" -and $lines[1] -match "B_Male")
    Assert-True "No temporary file is left" (@(Get-ChildItem -LiteralPath (Split-Path -Parent $baselinePath) -Filter "*.tmp").Count -eq 0)
    $measured = New-ArtViewTable
    $measured["A_Male_Rest_Y215"] = New-TestLine "A_Male_Rest_Y215" 0 1 0 300
    $measured["C_Male_Rest_Y215"] = New-TestLine "C_Male_Rest_Y215" 9 9 9 900
    $merged = @(Merge-ArtViewBaseline $read $measured @("A_Male_Rest_Y215", "C_Male_Rest_Y215", "D_Male_Rest_Y215"))
    Assert-Equal "A partial write measures, adds, and drops" "A_Male_Rest_Y215:1 C_Male_Rest_Y215:9" (($merged | ForEach-Object { "$($_.Name):$($_.Inner)" }) -join " ")
    $kept = @(Merge-ArtViewBaseline $read (New-ArtViewTable) @("A_Male_Rest_Y215", "B_Male_Rest_Y215"))
    Assert-Equal "A partial write keeps an entry it does not measure" "A_Male_Rest_Y215:5 B_Male_Rest_Y215:2" (($kept | ForEach-Object { "$($_.Name):$($_.Inner)" }) -join " ")
    Assert-True "A missing baseline reads as none" ($null -eq (Read-ArtViewBaseline (Join-Path $root "missing.json")))

    # The stale views of an outfit and body make one problem, with the assets they read, which an asset
    # that waits for an approval owns.
    $views = @(foreach ($pose in "P1", "P2", "P3", "P4", "P5") { New-TestView "Linen_Male_${pose}_Y215" }) + @(New-TestView "Net_Female_P1_Y215" "Net" "Female")
    $keys = New-ArtViewTable
    $baseline = New-ArtViewTable
    foreach ($item in $views) {
        $keys[$item.Name] = "key-$($item.Name)"
        $baseline[$item.Name] = [pscustomobject]@{ Name = $item.Name; Key = "key-$($item.Name)" }
    }

    Assert-Equal "A current baseline has no problem" 0 @(Get-ArtViewStaleProblems $views $baseline $keys).Count
    foreach ($pose in "P1", "P2", "P3", "P4") {
        $keys["Linen_Male_${pose}_Y215"] = "new-key"
    }

    $baseline["Old_Male_Rest_Y215"] = [pscustomobject]@{ Name = "Old_Male_Rest_Y215"; Key = "x" }
    $problems = @(Get-ArtViewStaleProblems $views $baseline $keys)
    Assert-Equal "One problem per outfit and body, one for entries the set lacks" 2 $problems.Count
    Assert-True "The problem names the outfit and body, three views, and how many more" ($problems[0].Problem -match "4 view\(s\) of Linen on Male" -and $problems[0].Problem -match ", and 1 more;")
    Assert-True "The problem names the entries the set lacks" ($problems[1].Problem -match "Old_Male_Rest_Y215")
    Assert-Equal "A waiting asset owns the problem" "art/Garments/Shirt" (Get-ArtViewProblemOwner $problems[0] @("art/Paint/Tattoo", "art/Garments/Shirt"))
    Assert-True "No waiting asset owns it" ($null -eq (Get-ArtViewProblemOwner $problems[0] @("art/Garments/Trousers")))
    Assert-True "Nothing owns the entries the set lacks" ($null -eq (Get-ArtViewProblemOwner $problems[1] @("art/Garments/Shirt")))
    $missing = @(Get-ArtViewStaleProblems $views $null $keys)
    Assert-True "A missing baseline is one problem" ($missing.Count -eq 1 -and $missing[0].Problem -match "is missing")

    # The exit codes of the check: a regression wins over a view without an entry, a change of contact
    # never fails, and a baseline being written takes new views.
    $old = New-ArtViewTable
    $old["A"] = New-TestLine "A" 0 10 5
    $old["B"] = New-TestLine "B" 3 0 0
    Assert-Equal "No change" 0 (Compare-ArtViews @((New-TestEntry "A" 0 10 5), (New-TestEntry "B" 3 0 0)) $old 6>$null)
    Assert-Equal "A regression and a view without an entry" 2 (Compare-ArtViews @((New-TestEntry "A" 0 11 5), (New-TestEntry "C" 0 0 0)) $old 6>$null)
    Assert-Equal "A view without an entry" 1 (Compare-ArtViews @((New-TestEntry "A" 0 10 5), (New-TestEntry "C" 0 0 0)) $old 6>$null)
    Assert-Equal "A new view while writing" 0 (Compare-ArtViews @((New-TestEntry "C" 0 0 0)) $old -Writing 6>$null)
    Assert-Equal "More contact never fails" 0 (Compare-ArtViews @((New-TestEntry "A" 0 10 500)) $old 6>$null)
    Assert-Equal "An improvement" 0 (Compare-ArtViews @((New-TestEntry "B" 1 0 0)) $old 6>$null)
    Assert-Equal "More skin" 2 (Compare-ArtViews @((New-TestEntry "B" 4 0 0)) $old 6>$null)
    Assert-Equal "Less inner but more skin" 2 (Compare-ArtViews @((New-TestEntry "A" 1 0 5)) $old 6>$null)

    # The data a garment reads: a texture's own from any folder before its template's, each from the
    # first folder that has it.
    $first = Join-Path (Join-Path $root "data") "first"
    $second = Join-Path (Join-Path $root "data") "second"
    $garment = [pscustomobject]@{ Texture = [pscustomobject]@{ Name = "LinenShirt" }; Template = [pscustomobject]@{ Name = "Shirt"; RelativeDirectory = "art/Garments/Shirt" } }
    New-TestFile (Join-Path $first "Shirt_Male.json")
    New-TestFile (Join-Path $second "Shirt_Male.json")
    Assert-True "The template's data from the first folder" (Test-ArtSamePath (Join-Path $first "Shirt_Male.json") (Find-ArtViewData $garment "Male" @($first, $second)))
    New-TestFile (Join-Path $second "LinenShirt_Male.json")
    Assert-True "A texture's own data before its template's" (Test-ArtSamePath (Join-Path $second "LinenShirt_Male.json") (Find-ArtViewData $garment "Male" @($first, $second)))
    New-TestFile (Join-Path $first "LinenShirt_Male.json")
    Assert-True "A texture's own data from the first folder" (Test-ArtSamePath (Join-Path $first "LinenShirt_Male.json") (Find-ArtViewData $garment "Male" @($first, $second)))
    $refused = $null
    try { $null = Find-ArtViewData $garment "Female" @($first, $second) } catch { $refused = $_.Exception.Message }
    Assert-True "No data names the export" ("$refused" -match "export art/Garments/Shirt")

    # The views' choice by outfit, body, and pose, as lists or one text with commas.
    $choice = @((New-TestView "Linen_Male_Walk03_Y215"), (New-TestView "Net_Male_Walk03_Y215" "Net"), (New-TestView "Net_Female_Walk03_Y215" "Net" "Female"))
    Assert-Equal "Every view without a filter" 3 @(Select-ArtViews $choice).Count
    Assert-Equal "An outfit and a body" "Net_Female_Walk03_Y215" ((Select-ArtViews $choice @("Net") @("Female")) | ForEach-Object { $_.Name })
    Assert-Equal "Outfits as one text" 3 @(Select-ArtViews $choice @("Linen,Net")).Count
    $refused = $null
    try { $null = Select-ArtViews $choice @("Robe") } catch { $refused = $_.Exception.Message }
    Assert-True "An unknown outfit" ("$refused" -match "-Outfit Robe names no view")

    # The upkeep of the views cache: only below views, only entries no run took for 30 days and none
    # would take now, a picture without numbers after a day, and what an interrupted run left.
    $cache = Join-Path $root "cache"
    $folder = Join-Path $cache "views"
    New-TestFile (Join-Path $folder "old.json") "{}" 40
    New-TestFile (Join-Path $folder "old.png") "x" 40
    New-TestFile (Join-Path $folder "old.classes.png") "x" 40
    New-TestFile (Join-Path $folder "kept.json") "{}" 40
    New-TestFile (Join-Path $folder "kept.png") "x" 40
    New-TestFile (Join-Path $folder "recent.json") "{}" 10
    New-TestFile (Join-Path $folder "lone.png") "x" 2
    New-TestFile (Join-Path $folder "fresh.png") "x"
    New-TestFile (Join-Path $folder ".old.json.123.tmp") "x" 2
    New-TestFile (Join-Path $folder ".new.png.123.tmp.png") "x"
    New-TestFile (Join-Path $cache "checks/ab/old.json") "{}" 40
    $keep = New-ArtViewTable
    $keep["kept"] = $true
    Assert-Equal "Entries removed" 1 (Remove-ArtViewCacheEntries $cache $keep)
    $left = [string[]]@(Get-ChildItem -LiteralPath $folder -File | ForEach-Object { $_.Name })
    [Array]::Sort($left, [StringComparer]::Ordinal)
    Assert-Equal "What the upkeep leaves" ".new.png.123.tmp.png fresh.png kept.json kept.png recent.json" ($left -join " ")
    Assert-True "The upkeep leaves the cache of the checks alone" (Test-Path -LiteralPath (Join-Path $cache "checks/ab/old.json"))
    Assert-Equal "A cache without views" 0 (Remove-ArtViewCacheEntries (Join-Path $root "no-cache") $keep)

    # The keys of the readable copies: a broken file reads as none, and they are written by a rename.
    $keysPath = Join-Path $root "readable/keys.json"
    New-TestFile $keysPath "{ broken"
    Assert-Equal "Broken keys read as none" 0 (Read-ArtViewKeys $keysPath).Count
    $copies = New-ArtViewTable
    $copies["b"] = "2"
    $copies["a"] = "1"
    Write-ArtViewKeys $keysPath $copies
    $again = Read-ArtViewKeys $keysPath
    Assert-Equal "Keys read back" "a=1 b=2" "a=$($again['a']) b=$($again['b'])"
    Assert-True "No temporary keys file is left" (@(Get-ChildItem -LiteralPath (Split-Path -Parent $keysPath) -File).Count -eq 1)

    # The root of the shared cache: the main checkout's when Git's common folder lies in a checkout of
    # this repository, else the checkout's own.
    $checkout = Join-Path $root "worktree"
    $null = New-Item -ItemType Directory -Force $checkout
    $own = Join-Path $checkout "artifacts/art-cache"
    $main = Join-Path $root "main"
    New-TestFile (Join-Path $main "tools/art/ArtPipelineCommon.ps1")
    $null = Invoke-ArtNative "git" @("init", "-q", $main)
    Assert-True "The main checkout is a Git repository" ($script:ArtNativeExitCode -eq 0)
    Assert-True "The main checkout's cache" (Test-ArtSamePath (Join-Path $main "artifacts/art-cache") (Resolve-ArtCacheRoot $checkout (Join-Path $main ".git")))
    $stranger = Join-Path $root "stranger"
    $null = New-Item -ItemType Directory -Force $stranger
    $null = Invoke-ArtNative "git" @("init", "-q", $stranger)
    Assert-True "A repository without the art scripts" (Test-ArtSamePath $own (Resolve-ArtCacheRoot $checkout (Join-Path $stranger ".git")))
    $plain = Join-Path $root "plain"
    New-TestFile (Join-Path $plain "tools/art/ArtPipelineCommon.ps1")
    $null = New-Item -ItemType Directory -Force (Join-Path $plain ".git")
    Assert-True "A folder that is no Git checkout" (Test-ArtSamePath $own (Resolve-ArtCacheRoot $checkout (Join-Path $plain ".git")))
    Assert-True "No common folder" (Test-ArtSamePath $own (Resolve-ArtCacheRoot $checkout ""))
}
finally {
    $env:GIT_CEILING_DIRECTORIES = $ceiling
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Host "  $failure"
    }

    Write-Host "Result: the game view helpers failed $($failures.Count) case(s)"
    exit 1
}

Write-Host "Result: the game view helpers hold in every case"
exit 0
