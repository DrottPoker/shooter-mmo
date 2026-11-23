# Checks the helpers of tools/art/ArtViews.ps1 that decide what the game views of
# tools/art/Render-ArtViews.ps1 key, measure, keep, and report, without Blender: the committed key and
# what changes it, the rig a body follows with the avatar skeletons of the two models' .fbx.meta and
# their fingerprint, the fingerprint of the renderer's code, the paint layers a body shows, the baseline's
# round trip and partial write, the grouping of stale views and the asset that waits for them, the exit
# codes of the check, the order the data is looked up in, the views' choice, the upkeep of the views
# cache with the index of the pictures by committed key, the class of a change, the data staged for the
# views by tools/art/Build-Art.ps1 with its index and key, the review page of tools/art/ArtReviewPage.ps1,
# the keys of the readable copies, and the root of the shared cache.
#
# Exit codes: 0 every case holds, 1 a case failed.
$ErrorActionPreference = "Stop"
$art = Join-Path (Split-Path -Parent $PSScriptRoot) "art"
. (Join-Path $art "ArtPipelineCommon.ps1")
. (Join-Path $art "ArtViews.ps1")
. (Join-Path $art "ArtReviewPage.ps1")

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

function New-TestRig {
    # The library's rig that a body which is not the rig follows, with the avatars of the two models.
    $manifest = [pscustomobject]@{ RelativeDirectory = "art/Characters/Human_Male"
        Content = (New-TestContent @{ model = "male-model"; rig = "male-rig" } @{ skeleton = "male-skeleton" }) }
    return [pscustomobject]@{
        Manifest = $manifest
        Avatar = [pscustomobject]@{ Name = "src/Client/Assets/Art/Models/Characters/Human_Male.fbx.meta"; Fingerprint = "male-avatar" }
        BodyAvatar = [pscustomobject]@{ Name = "src/Client/Assets/Art/Models/Characters/Human_Female.fbx.meta"; Fingerprint = "female-avatar" }
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

    # A body that is not the library's rig follows it in the clip poses: the rig's skeleton and the
    # reference poses of the two avatars key its clip views, and nothing of the rig keys a view of the rig
    # itself or a rest view.
    $female = New-TestView "Linen_Female_Walk03_Y215" "Linen" "Female"
    $alone = Get-TestKey $female
    $female | Add-Member -NotePropertyName Rig -NotePropertyValue (New-TestRig)
    $followed = Get-TestKey $female
    Assert-True "Following the rig changes the key of a clip view" ($followed -cne $alone)
    $female.Rig.BodyAvatar.Fingerprint = "female-avatar-2"
    Assert-True "The body's reference pose changes the key" ((Get-TestKey $female) -cne $followed)
    $female.Rig = New-TestRig
    $female.Rig.Avatar.Fingerprint = "male-avatar-2"
    Assert-True "The rig's reference pose changes the key" ((Get-TestKey $female) -cne $followed)
    $female.Rig = New-TestRig
    $female.Rig.Manifest.Content.parts["skeleton"] = "male-skeleton-2"
    Assert-True "The rig's skeleton changes the key" ((Get-TestKey $female) -cne $followed)
    $female.Rig = New-TestRig
    $female.Rig.Manifest.Content.stages["rig"] = "male-rig-2"
    Assert-Equal "The rig's other content leaves the key alone" $followed (Get-TestKey $female)
    $female.Rig = New-TestRig
    $text = Get-ArtViewKeyText $female "code-1"
    Assert-True "The key text names the rig and both avatars" $text.Contains("`nrig art/Characters/Human_Male skeleton=male-skeleton avatars " +
        "src/Client/Assets/Art/Models/Characters/Human_Male.fbx.meta=male-avatar src/Client/Assets/Art/Models/Characters/Human_Female.fbx.meta=female-avatar`n")
    Assert-Equal "The manifests of a view hold the rig it follows" "art/Characters/Human_Male" (@(Get-ArtViewManifestsOf $female)[-1].RelativeDirectory)
    $rest = New-TestView "Linen_Female_Rest_Y215" "Linen" "Female"
    $rest.Pose = [pscustomobject]@{ Name = "Rest"; Clip = $null; Frame = $null; Phase = "clean" }
    $restKey = Get-TestKey $rest
    $rest | Add-Member -NotePropertyName Rig -NotePropertyValue (New-TestRig)
    Assert-Equal "A rest view keys no rig" $restKey (Get-TestKey $rest)
    $male = New-TestView
    $male | Add-Member -NotePropertyName Rig -NotePropertyValue $null
    Assert-Equal "A view of the rig itself keys no rig" $base (Get-TestKey $male)

    # The avatar skeleton of a model's .fbx.meta: the lines under humanDescription's skeleton, whatever
    # the line ends, and a fingerprint that only they change.
    $metaText = @(
        "ModelImporter:", "  humanDescription:", "    serializedVersion: 3", "    human: []", "    skeleton:",
        "    - name: Model(Clone)", "      parentName: ", "      position: {x: 0, y: 0, z: 0}", "      rotation: {x: 0, y: 0, z: 0, w: 1}",
        "    - name: Root", "      parentName: Model(Clone)", "      position: {x: 0, y: 1, z: 0.5}", "      rotation: {x: 0, y: 0, z: 0, w: 1}",
        "    armTwist: 0.5", "  globalScale: 1", "") -join "`n"
    $avatarLines = @(Get-ArtViewAvatarLines $metaText)
    Assert-Equal "The avatar skeleton's lines" "8 |    - name: Model(Clone)|      rotation: {x: 0, y: 0, z: 0, w: 1}" "$($avatarLines.Count) |$($avatarLines[0])|$($avatarLines[-1])"
    Assert-Equal "A meta without an avatar has no lines" 0 @(Get-ArtViewAvatarLines "ModelImporter:`n  globalScale: 1`n    skeleton:`n    - name: A`n").Count
    $metaPath = Join-Path $root "meta/Human_Test.fbx.meta"
    New-TestFile $metaPath $metaText
    $fingerprint = Get-ArtViewAvatarFingerprint $metaPath
    Assert-True "An avatar has a fingerprint" ($fingerprint -cmatch '^[0-9a-f]{64}$')
    New-TestFile $metaPath $metaText.Replace("`n", "`r`n")
    Assert-Equal "Line ends leave the avatar's fingerprint" $fingerprint (Get-ArtViewAvatarFingerprint $metaPath)
    New-TestFile $metaPath $metaText.Replace("globalScale: 1", "globalScale: 2")
    Assert-Equal "An import setting outside the avatar leaves its fingerprint" $fingerprint (Get-ArtViewAvatarFingerprint $metaPath)
    New-TestFile $metaPath $metaText.Replace("y: 1, z: 0.5", "y: 1, z: 0.6")
    Assert-True "The reference pose changes the avatar's fingerprint" ((Get-ArtViewAvatarFingerprint $metaPath) -cne $fingerprint)
    Assert-Equal "A missing meta has no avatar" "none" (Get-ArtViewAvatarFingerprint (Join-Path $root "meta/missing.fbx.meta"))

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
    Assert-True "No temporary file is left" (@(Get-ChildItem -LiteralPath (Split-Path -Parent $baselinePath) -Filter "*.tmp" -Force).Count -eq 0)
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
    # -Force: a name that starts with a dot is a hidden file outside Windows.
    $left = [string[]]@(Get-ChildItem -LiteralPath $folder -File -Force | ForEach-Object { $_.Name })
    [Array]::Sort($left, [StringComparer]::Ordinal)
    Assert-Equal "What the upkeep leaves" ".new.png.123.tmp.png fresh.png kept.json kept.png recent.json" ($left -join " ")
    Assert-True "The upkeep leaves the cache of the checks alone" (Test-Path -LiteralPath (Join-Path $cache "checks/ab/old.json"))
    Assert-Equal "A cache without views" 0 (Remove-ArtViewCacheEntries (Join-Path $root "no-cache") $keep)

    # The index of the pictures by committed key: one note per key, read back, the pictures only while the
    # entry has both, the entries the baseline's keys point to kept, and a note whose entry went removed.
    $index = Join-Path $root "index-cache"
    $committedKey, $entryKey, $goneKey = ("a" * 64), ("b" * 64), ("c" * 64)
    Assert-True "No note reads as none" ($null -eq (Read-ArtViewIndex $index $committedKey))
    Write-ArtViewIndex $index $committedKey $entryKey
    Assert-Equal "A note reads back" $entryKey (Read-ArtViewIndex $index $committedKey)
    Assert-True "A note without both pictures gives none" ($null -eq (Get-ArtViewPictures $index $committedKey))
    New-TestFile (Join-Path $index "views/$entryKey.png") "x" 40
    New-TestFile (Join-Path $index "views/$entryKey.classes.png") "x" 40
    New-TestFile (Join-Path $index "views/$entryKey.json") "{}" 40
    Assert-True "The pictures of a committed key" ((Get-ArtViewPictures $index $committedKey).Look -like "*$entryKey.png")
    Write-ArtViewIndex $index ("d" * 64) $goneKey
    New-TestFile (Join-Path $index "views/committed/.$("e" * 64).123.tmp") "x" 2
    Assert-True "Not a key reads as none" ($null -eq (Read-ArtViewIndex $index "../views"))
    $pointed = New-ArtViewTable
    $baselineLines = New-ArtViewTable
    $baselineLines["A_Male_Rest_Y215"] = [pscustomobject]@{ Name = "A_Male_Rest_Y215"; Key = $committedKey }
    Add-ArtViewBaselineKeep $index $baselineLines $pointed
    Assert-Equal "The baseline's keys point to their entries" "True" "$($pointed.ContainsKey($entryKey))"
    Assert-Equal "The pointed entry stays, however old" 0 (Remove-ArtViewCacheEntries $index $pointed)
    Assert-True "Its note stays" ($null -ne (Read-ArtViewIndex $index $committedKey))
    Assert-True "A note whose entry is gone goes" ($null -eq (Read-ArtViewIndex $index ("d" * 64)))
    Assert-Equal "A note an interrupted run left goes after a day" 1 @(Get-ChildItem -LiteralPath (Join-Path $index "views/committed") -File -Force).Count
    Assert-Equal "Without the baseline's keys the old entry goes" 1 (Remove-ArtViewCacheEntries $index (New-ArtViewTable))
    Assert-True "And its note with it" ($null -eq (Read-ArtViewIndex $index $committedKey))

    # A view's pictures go into the index only when it read exactly the data its export records name.
    $indexView = New-TestView
    $indexView.Body | Add-Member -NotePropertyName Export -NotePropertyValue ([ordered]@{ dataSha256 = "body" })
    $indexView.Garments[0].DataSha256 = "e" * 64
    $hashes = @{ "staged/Shirt_Male.json" = ("e" * 64); "other/Shirt_Male.json" = ("f" * 64) }
    Assert-True "The data its export record names" (Test-ArtViewIndexable $indexView @("staged/Shirt_Male.json") $hashes)
    Assert-True "Other data than the record names" (-not (Test-ArtViewIndexable $indexView @("other/Shirt_Male.json") $hashes))
    $indexView.Garments[0].DataSha256 = "none"
    Assert-True "A garment without an export record" (-not (Test-ArtViewIndexable $indexView @("staged/Shirt_Male.json") @{ "staged/Shirt_Male.json" = "none" }))
    Assert-True "A body without an export record" (-not (Test-ArtViewIndexable (New-TestView) @("staged/Shirt_Male.json") $hashes))

    # The class of a view's numbers against its entry, as -Check and the review page take it.
    $line = New-TestLine "A" 3 4 5
    Assert-Equal "More skin is worse" "worse" (Get-ArtViewChangeClass $line ([pscustomobject]@{ skin = 4; inner = 0; contact = 0 }))
    Assert-Equal "Less skin and inner is better" "better" (Get-ArtViewChangeClass $line ([pscustomobject]@{ skin = 2; inner = 4; contact = 50 }))
    Assert-Equal "Only contact and figure changed" "other" (Get-ArtViewChangeClass $line ([pscustomobject]@{ skin = 3; inner = 4; contact = 0 }))

    # The views by whole names, and a texture whose current content is opaque reads its template's data.
    Assert-Equal "A view by its name" "Net_Male_Walk03_Y215" ((Select-ArtViews $choice @() @() @() @("Net_Male_Walk03_Y215")) | ForEach-Object { $_.Name })
    $refused = $null
    try { $null = Select-ArtViews $choice @() @() @() @("Gone_Male_Rest_Y215") } catch { $refused = $_.Exception.Message }
    Assert-True "An unknown view name" ("$refused" -match "-View Gone_Male_Rest_Y215 names no view")
    Assert-True "An opaque texture reads its template's data" (Test-ArtSamePath (Join-Path $first "Shirt_Male.json") (Find-ArtViewData $garment "Male" @($first, $second) @("LinenShirt")))
    Assert-True "Other code has another fingerprint" ((Get-ArtViewCodeFingerprint $code -Paths @("tools/art/blender/art_pipeline") -Tag "other/1") -cne (Get-ArtViewCodeFingerprint $code))

    # The data staged for the views: the index's round trip, an entry that holds only for its key and its
    # files' bytes, and a key that follows the content and the code.
    $staging = Join-Path $root "staging"
    New-TestFile (Join-Path $staging "Shirt_Male.json") "male"
    $stagedIndex = @{}
    $stagedIndex["art/Garments/Shirt"] = [pscustomobject]@{ Key = "k1"; State = "staged"; Problem = $null; Seconds = 12.34
        Files = [ordered]@{ "Shirt_Male.json" = (Get-ArtFileSha256 (Join-Path $staging "Shirt_Male.json")) } }
    $stagedIndex["art/GarmentTextures/NetShirt"] = [pscustomobject]@{ Key = "k2"; State = "refused"; Problem = "a rule"; Seconds = 1; Files = [ordered]@{} }
    Write-ArtStagingIndex $staging $stagedIndex
    $readIndex = Read-ArtStagingIndex $staging
    Assert-Equal "The staging index reads back" "k1 staged 12.3 Shirt_Male.json" "$($readIndex['art/Garments/Shirt'].Key) $($readIndex['art/Garments/Shirt'].State) $($readIndex['art/Garments/Shirt'].Seconds) $(@($readIndex['art/Garments/Shirt'].Files.Keys) -join ',')"
    Assert-True "An entry holds for its key" (Test-ArtStagedEntry $readIndex["art/Garments/Shirt"] "k1" $staging)
    Assert-True "Not for another key" (-not (Test-ArtStagedEntry $readIndex["art/Garments/Shirt"] "k3" $staging))
    Assert-True "A refusal holds without files" (Test-ArtStagedEntry $readIndex["art/GarmentTextures/NetShirt"] "k2" $staging)
    New-TestFile (Join-Path $staging "Shirt_Male.json") "changed"
    Assert-True "Not with a changed file" (-not (Test-ArtStagedEntry $readIndex["art/Garments/Shirt"] "k1" $staging))
    New-TestFile (Join-Path $staging "index.json") "{ broken"
    Assert-Equal "A broken index reads as none" 0 (Read-ArtStagingIndex $staging).Count
    $shirt = Read-ArtAssetManifest (Join-Path $script:ArtRepositoryRoot "art/Garments/Shirt")
    Assert-Equal "A template's staged files" "Shirt_Male.json Shirt_Female.json" ((Get-ArtStagedNames $shirt) -join " ")
    $stagingKey = Get-ArtStagingKey $shirt "code-1"
    Assert-True "A key of a template" ("$stagingKey" -cmatch '^[0-9a-f]{64}$')
    Assert-Equal "The same key again" $stagingKey (Get-ArtStagingKey $shirt "code-1")
    Assert-True "The code changes the key" ((Get-ArtStagingKey $shirt "code-2") -cne $stagingKey)
    $shirt.Content.stages["rig"] = "0" * 64
    Assert-True "The content changes the key" ((Get-ArtStagingKey $shirt "code-1") -cne $stagingKey)
    $net = Read-ArtAssetManifest (Join-Path $script:ArtRepositoryRoot "art/GarmentTextures/NetShirt")
    Assert-True "A texture's key is its own" ("$(Get-ArtStagingKey $net 'code-1')" -cmatch '^[0-9a-f]{64}$' -and (Get-ArtStagingKey $net "code-1") -cne $stagingKey)
    $net.Garment.Content.stages["rig"] = "0" * 64
    Assert-True "Its template's content changes a texture's key" ((Get-ArtStagingKey $net "code-1") -cne (Get-ArtStagingKey (Read-ArtAssetManifest (Join-Path $script:ArtRepositoryRoot "art/GarmentTextures/NetShirt")) "code-1"))

    # The review page: what changed in words, the views that changed with their class, and a page that
    # encodes every text, shows only what changed, and approves nothing.
    Assert-Equal "A texture that follows its template" "Changed: the image. It follows the new UVs of Trousers." (Format-ArtReviewChange @("image", "templateUv") @([pscustomobject]@{ Name = "Trousers"; Parts = @("uv") }, [pscustomobject]@{ Name = "Human_Actions"; Parts = @() }))
    Assert-Equal "Parts in words" "Changed: the shape, the weights, and the clip Walk." (Format-ArtReviewChange @("shape", "weights", "clip:Walk") @())
    Assert-Equal "A new asset" "A new asset." (Format-ArtReviewChange $null @() $true)
    Assert-Equal "A kind in words" "garment texture" (Get-ArtReviewKindWords "texture")
    Assert-Equal "What an asset waits for" "waits for the approval of art/Garments/Shirt, which it follows" (Format-ArtNotExported ([pscustomobject]@{ Need = "waits"; Stages = @(); Follows = @("art/Garments/Shirt") }))
    Assert-Equal "Its own stages" "waits for your approval of its 'model' and 'rig' stage(s)" (Format-ArtNotExported ([pscustomobject]@{ Need = "approval"; Stages = @("model", "rig"); Follows = @() }))
    Assert-Equal "A reason without its command" "was checked over other templates of a lower layer" (Remove-ArtReviewCommands "was checked over other templates of a lower layer; run tools/art/Sync-GarmentAsset.ps1 to see its checks over them, then run tools/art/Export-ArtAsset.ps1.")

    # The way back: the asset folders with tracked files that differ from Git, never art/Views, and the new
    # asset folders, which it does not restore.
    $statusLines = @(" M art/Garments/Shirt/Shirt.blend", " M art/Garments/Shirt/asset.json", "M  art/GarmentTextures/LinenShirt/LinenShirt.blend",
        " M art/Views/baseline.json", " D art/Skeletons/Humanoid.json", "?? art/Garments/Cape/asset.json", "?? art/Garments/Cape/Cape.blend",
        "?? art/Garments/Shirt/Shirt.blend1", "R  art/Paint/Old/asset.json -> art/Paint/Tattoo/asset.json", "A  art/Garments/Hat/asset.json")
    $reject = Get-ArtRejectPaths $statusLines @("art/Garments/Shirt", "art/GarmentTextures/LinenShirt", "art/Garments/Cape", "art/Garments/Hat", "art/Paint/Tattoo") @("art/Garments/Shirt", "art/GarmentTextures/LinenShirt", "art/Paint/Old")
    Assert-Equal "The folders to restore" "art/GarmentTextures/LinenShirt art/Garments/Shirt art/Paint/Old art/Skeletons/Humanoid.json" ($reject.Restore -join " ")
    # A folder that HEAD lacks is new also when its files are added to the index or renamed into it.
    Assert-Equal "Nothing differs" 0 @((Get-ArtRejectPaths @() @("art/Garments/Shirt") @("art/Garments/Shirt")).Restore).Count

    # What a measurement did to the numbers of the poses where nothing may come through.
    $was = New-ArtViewTable
    $was["Linen_Male_Walk03_Y215"] = New-TestLine "Linen_Male_Walk03_Y215" 1 11 0
    $was["Linen_Male_Death31_Y215"] = New-TestLine "Linen_Male_Death31_Y215" 50 50 0
    $now = New-ArtViewTable
    $now["Linen_Male_Walk03_Y215"] = New-TestLine "Linen_Male_Walk03_Y215" 2 605 0
    $now["Linen_Male_Death31_Y215"] = New-TestLine "Linen_Male_Death31_Y215" 70 70 0
    $now["Net_Female_Rest_Y215"] = New-TestLine "Net_Female_Rest_Y215" 0 4 0
    $phase = @{ "Linen_Male_Walk03_Y215" = "clean"; "Linen_Male_Death31_Y215" = "rare"; "Net_Female_Rest_Y215" = "clean" }
    $measuredLines = @(Format-ArtBaselineChange @("Linen_Male_Death31_Y215", "Linen_Male_Walk03_Y215", "Net_Female_Rest_Y215") $was $now $phase)
    Assert-Equal "The sum over every measured view first" "All 3 measured view(s): in the poses where nothing may come through, skin 1 -> 2 px and inner 11 -> 609 px." $measuredLines[0]
    Assert-Equal "Then per outfit and body" "Linen on Male: 2 view(s) measured; in the poses where nothing may come through, skin 1 -> 2 px and inner 11 -> 605 px." $measuredLines[1]
    Assert-Equal "A view new to the baseline counts 0 before" "Net on Female: 1 view(s) measured; in the poses where nothing may come through, skin 0 -> 0 px and inner 0 -> 4 px." $measuredLines[2]
    $pageViews = @((New-TestView "Linen_Male_Walk03_Y215"), (New-TestView "Linen_Male_Walk10_Y215"), (New-TestView "Linen_Male_Rest_Y215"), (New-TestView "Linen_Male_Jog01_Y215"))
    $pageKeys = New-ArtViewTable
    $pageBaseline = New-ArtViewTable
    foreach ($item in $pageViews) {
        $pageKeys[$item.Name] = "new-$($item.Name)"
        $pageBaseline[$item.Name] = [pscustomobject]@{ Name = $item.Name; Key = "old-$($item.Name)"; Skin = 2; Inner = 0; Contact = 1; Figure = 900 }
    }

    $pageKeys["Linen_Male_Jog01_Y215"] = "old-Linen_Male_Jog01_Y215"
    $pageRendered = New-ArtViewTable
    $pageRendered["Linen_Male_Walk03_Y215"] = [pscustomobject]@{ Numbers = [pscustomobject]@{ skin = 9; inner = 0; contact = 1; figure = 900 }; Look = "l"; Classes = "c" }
    $pageRendered["Linen_Male_Walk10_Y215"] = [pscustomobject]@{ Numbers = [pscustomobject]@{ skin = 0; inner = 0; contact = 1; figure = 900 }; Look = "l"; Classes = "c" }
    $pageReasons = New-ArtViewTable
    $pageReasons["Linen_Male_Rest_Y215"] = "Not drawn, since <Shirt> has no data."
    $changedViews = @(Get-ArtReviewViews $pageViews $pageBaseline $pageKeys $pageRendered $pageReasons "")
    Assert-Equal "Only the changed views, each with its class" "Linen_Male_Walk03_Y215:worse:True Linen_Male_Walk10_Y215:better:False Linen_Male_Rest_Y215:missing:False" (($changedViews | ForEach-Object { "$($_.Name):$($_.Class):$($_.StillThrough)" }) -join " ")
    $page = Join-Path $root "review"
    New-TestFile (Join-Path $page "Old_Male_Rest_Y215.after.png") "old"
    $review = [pscustomobject]@{ Failure = $null; Views = @($changedViews | Where-Object { $_.Class -eq "missing" }); ViewsNote = $null
        Reject = [pscustomobject]@{ Restore = @("art/Garments/Shirt", "art/GarmentTextures/LinenShirt"); New = @("art/Garments/Cape") }
        Approvals = @([pscustomobject]@{ Asset = "art/Characters/Human_Male"; Name = "Human_Male"; Kind = "body"; Change = "Changed: <script>alert(1)</script>."; Sheet = $null
                Note = "While this body waits for your approval, the game views show it as its file is now."
                SheetNote = "No review sheet."; Commands = @(".\tools\art\Approve-ArtAsset.ps1 -Asset art/Garments/Shirt -Stage model") })
        Exported = @(); NotExported = @([pscustomobject]@{ Asset = "art/Garments/Cloak"; Detail = "waits for the approval of art/Garments/Shirt, which it follows" })
        Baseline = @(); Staging = @(); Problems = @(); Did = @("Synced & exported nothing.") }
    $html = [IO.File]::ReadAllText((Write-ArtReviewPage $page $review))
    Assert-True "The page encodes every text" ($html.Contains("&lt;script&gt;alert(1)&lt;/script&gt;") -and -not $html.Contains("<script>") -and $html.Contains("Synced &amp; exported"))
    Assert-True "The page gives the commands and the build after them" ($html.Contains(".\tools\art\Approve-ArtAsset.ps1 -Asset art/Garments/Shirt -Stage model`n.\tools\art\Build-Art.ps1"))
    Assert-True "The way back is a real command, with the build after it" ($html.Contains("git checkout HEAD -- art/Garments/Shirt art/GarmentTextures/LinenShirt`n.\tools\art\Build-Art.ps1") -and -not $html.Contains("the asset you changed"))
    Assert-True "New folders are named as not restored" ($html.Contains("not restore them: art/Garments/Cape."))
    Assert-True "A body that waits says what its views show" ($html.Contains("While this body waits for your approval, the game views show it as its file is now."))
    Assert-True "The page waits for an approval and claims none" ($html.Contains("wait for your approval") -and -not ($html -match "(?i)\bapproved\b"))
    Assert-True "The page names why a view was not drawn" ($html.Contains("Not drawn, since &lt;Shirt&gt; has no data."))
    Assert-Equal "The page left no old picture" "index.html" ((Get-ChildItem -LiteralPath $page -File -Force | ForEach-Object { $_.Name }) -join " ")
    $review.Approvals = @()
    $review.Views = @()
    $html = [IO.File]::ReadAllText((Write-ArtReviewPage $page $review))
    Assert-True "Nothing to approve and no picture" ($html.Contains("Nothing to approve") -and -not $html.Contains("<img"))
    $review.Failure = "Blender <5.2> was not found."
    $html = [IO.File]::ReadAllText((Write-ArtReviewPage $page $review))
    Assert-True "A build that failed says so first" ($html.Contains("The build failed.") -and $html.Contains("Blender &lt;5.2&gt; was not found.") -and -not $html.Contains("Nothing to approve"))
    $review.Approvals = @([pscustomobject]@{ Asset = "art/Garments/Shirt"; Name = "Shirt"; Kind = "garment template"; Change = "Changed: the shape."; Sheet = $null
            SheetNote = $null; Commands = @(".\tools\art\Approve-ArtAsset.ps1 -Asset art/Garments/Shirt -Stage model") })
    $review.ViewsNote = "280 game view(s) read what changed; the build stopped before it drew them."
    $html = [IO.File]::ReadAllText((Write-ArtReviewPage $page $review))
    Assert-True "A build that failed offers no approval" ($html.Contains("Approve nothing until the build runs through") -and -not $html.Contains("Approve-ArtAsset.ps1") -and $html.Contains("Changed: the shape."))
    Assert-True "Views the build stopped before are said once" ($html.Contains("280 game view(s) read what changed; the build stopped before it drew them."))
    $review.ViewsNote = $null

    # The pictures of the page, where System.Drawing draws: copied beside it, the class picture's black
    # made transparent, and a view that looks exactly as before listed without pictures.
    $drawing = $true
    try {
        Add-Type -AssemblyName System.Drawing
        (New-Object System.Drawing.Bitmap 1, 1).Dispose()
    }
    catch {
        $drawing = $false
    }

    if ($drawing) {
        function New-TestPicture([string]$Path, [System.Drawing.Color]$Color) {
            $null = New-Item -ItemType Directory -Force (Split-Path -Parent $Path)
            $bitmap = New-Object System.Drawing.Bitmap 4, 6
            try {
                for ($x = 0; $x -lt 4; $x++) { for ($y = 0; $y -lt 6; $y++) { $bitmap.SetPixel($x, $y, $Color) } }
                $bitmap.SetPixel(1, 1, [System.Drawing.Color]::Red)
                $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
            }
            finally {
                $bitmap.Dispose()
            }
        }

        $pictures = Join-Path $root "pictures"
        New-TestPicture (Join-Path $pictures "before.png") ([System.Drawing.Color]::Gray)
        New-TestPicture (Join-Path $pictures "after.png") ([System.Drawing.Color]::White)
        New-TestPicture (Join-Path $pictures "classes.png") ([System.Drawing.Color]::Black)
        $worse = $changedViews[0]
        $worse.BeforePictures = [pscustomobject]@{ Look = (Join-Path $pictures "before.png"); Classes = (Join-Path $pictures "classes.png") }
        $worse.AfterPictures = [pscustomobject]@{ Look = (Join-Path $pictures "after.png"); Classes = (Join-Path $pictures "classes.png") }
        $sameLine = [pscustomobject]@{ Name = "Linen_Male_Jog08_Y215"; Key = "old"; Skin = 2; Inner = 0; Contact = 1; Figure = 900 }
        $same = [pscustomobject]@{ Name = "Linen_Male_Jog08_Y215"; Group = "Linen on Male"; Words = "Jog"; Phase = "clean"; Before = $sameLine
            BeforePictures = $worse.AfterPictures; After = [pscustomobject]@{ skin = 2; inner = 0; contact = 1; figure = 900 }; AfterPictures = $worse.AfterPictures
            Class = "other"; Reason = $null; StillThrough = $true; SamePicture = $false }
        $review.Failure = $null
        $review.Views = @($same, $worse)
        $html = [IO.File]::ReadAllText((Write-ArtReviewPage $page $review))
        $files = [string[]]@(Get-ChildItem -LiteralPath $page -File -Force | ForEach-Object { $_.Name })
        [Array]::Sort($files, [StringComparer]::Ordinal)
        Assert-Equal "The pictures beside the page, none of a view that looks the same" "Linen_Male_Walk03_Y215.after.png Linen_Male_Walk03_Y215.before.png Linen_Male_Walk03_Y215.classes.png index.html" ($files -join " ")
        Assert-True "The worse view comes first, linked from the top" ($html.IndexOf("href=`"#v-Linen_Male_Walk03_Y215`"") -lt $html.IndexOf("id=`"v-Linen_Male_Walk03_Y215`"") -and $html.IndexOf("id=`"v-Linen_Male_Walk03_Y215`"") -lt $html.IndexOf("Linen_Male_Jog08_Y215"))
        Assert-True "The view that looks the same is named" ($html.Contains("Look exactly as before (1)") -and -not $html.Contains("Linen_Male_Jog08_Y215.after.png"))
        $classes = Read-ArtReviewBitmap (Join-Path $page "Linen_Male_Walk03_Y215.classes.png")
        try {
            Assert-Equal "The class picture's black is transparent and its colours stay" "0 255" "$($classes.GetPixel(0, 0).A) $($classes.GetPixel(1, 1).R)"
        }
        finally {
            $classes.Dispose()
        }

        # Worse views by how much more comes through, the most first, 12 in full and the others folded in
        # that order; better ones by how much less; the pose rule said once; the sums by outfit and body.
        function New-TestItem([string]$Name, [int]$Place, [int]$SkinWas, [int]$InnerWas, [int]$Skin, [int]$Inner, [string]$Class) {
            return [pscustomobject]@{ Name = $Name; Place = $Place; Group = "$($Name.Split('_')[0]) on $($Name.Split('_')[1])"; Words = "pose"; Phase = "clean"
                Before = [pscustomobject]@{ Skin = $SkinWas; Inner = $InnerWas; Contact = 0; Figure = 900 }; BeforePictures = $worse.BeforePictures
                After = [pscustomobject]@{ skin = $Skin; inner = $Inner; contact = 0; figure = 900 }; AfterPictures = $worse.AfterPictures
                Class = $Class; Reason = $null; StillThrough = ($Skin + $Inner -gt 0); ThroughBefore = ($SkinWas + $InnerWas -gt 0); SamePicture = $false }
        }

        $ranked = @(for ($index = 1; $index -le 14; $index++) { New-TestItem "Linen_Male_P$($index)_Y215" $index 0 10 0 (10 + $index) "worse" }) +
            @(New-TestItem "Net_Female_P1_Y215" 20 0 10 0 (10 + 14) "worse") + @(New-TestItem "Net_Female_P2_Y215" 21 5 0 0 0 "better")
        $review.Views = $ranked
        $html = [IO.File]::ReadAllText((Write-ArtReviewPage $page $review))
        $order = @([regex]::Matches($html, 'id="v-([A-Za-z0-9_]+)"') | ForEach-Object { $_.Groups[1].Value })
        Assert-Equal "The worse views, the most first, alike ones in the order of the set" "Linen_Male_P14_Y215 Net_Female_P1_Y215 Linen_Male_P13_Y215" (@($order | Select-Object -First 3) -join " ")
        Assert-True "The table of the worse views in that order" ($html.IndexOf("href=`"#v-Linen_Male_P14_Y215`"") -lt $html.IndexOf("href=`"#v-Net_Female_P1_Y215`"") -and $html.Contains("<td class=`"worse`">+14</td>"))
        $folded = $html.IndexOf("The other 3 worse views, with smaller differences")
        Assert-True "Twelve in full, the others folded" ($folded -gt $html.IndexOf("id=`"v-Linen_Male_P4_Y215`"") -and $folded -lt $html.IndexOf("id=`"v-Linen_Male_P3_Y215`"") -and $html.IndexOf("id=`"v-Linen_Male_P1_Y215`"") -lt $html.IndexOf("id=`"v-Net_Female_P2_Y215`""))
        Assert-True "No sentence of the pose rule per view, once in all" (-not $html.Contains("it still shows skin") -and $html.Contains("Of the 16 changed view(s) in poses where nothing may come through, 15 show skin or an inner garment after the change; 15 of those did before it."))
        Assert-True "The sums by outfit and body" ($html.Contains("By outfit and body") -and $html.Contains("<tr><td>Net on Female</td><td>1</td><td>1</td><td>5</td><td>0</td><td>10</td><td>24</td></tr>"))
    }

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
    $elsewhere = Join-Path $root "elsewhere-cache"
    Assert-True "MMORPG_ART_CACHE_ROOT names the cache over the main checkout's" (Test-ArtSamePath $elsewhere (Resolve-ArtCacheRoot $checkout (Join-Path $main ".git") $elsewhere))
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
