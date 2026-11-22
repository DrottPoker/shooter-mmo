# Proves the content fingerprints that approvals bind to, with Blender, on copies of the art assets'
# files in a temporary folder; nothing below art/ changes. It is not part of CI. See
# docs/ART_PIPELINE.md, Review Stages.
#
# 1. Resave: every asset's .blend file, opened and saved again by Blender, has another file hash and
#    exactly the parts and stages of the content record in its asset.json.
# 2. Sensitivity: for one asset of each kind, scripted edits of content change exactly the parts they
#    touch, with the stage of each and every later stage, and edits of what is not content, such as
#    the selection, the active object, viewport hiding, the current frame, and an unused datablock,
#    change nothing.
param(
    [string[]]$Asset,
    # Every asset below art/, with the sensitivity proof on one chosen asset of each kind.
    [switch]$All,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

if ($All -eq ($Asset.Count -gt 0)) {
    throw "Pass either -Asset <folder> or -All."
}

# The asset of each kind whose edits -All proves: each has what every edit of its kind needs.
$proofAssets = [ordered]@{
    static = "art/Projectiles/BronzeArrow"
    skinned = "art/Creatures/Wolf"
    animations = "art/Animations/Human_Actions"
    paint = "art/Paint/Tattoo"
    garment = "art/Garments/Shirt"
    cage = "art/Cages/Humanoid"
    texture = "art/GarmentTextures/WoolCloak"
}
# The edits of content of every kind and the part each changes; <clip> is the first clip by name and
# <body> the first body. A copy of another asset is not content.
$contentEdits = @{
    static = [ordered]@{ vertex = "shape"; uv = "uv"; material = "materials"; pixel = "materials"; "unit-scale" = "shape" }
    skinned = [ordered]@{ vertex = "shape"; uv = "uv"; material = "materials"; pixel = "materials"; "unit-scale" = "shape"; weight = "weights"; bone = "skeleton"; key = "clip:<clip>"; "key-easing" = "clip:<clip>" }
    animations = [ordered]@{ bone = "skeleton"; "unit-scale" = "skeleton"; key = "clip:<clip>"; "key-easing" = "clip:<clip>"; copy = "" }
    paint = [ordered]@{ vertex = "canvas"; pixel = "paint"; "body-pixel" = "body:<body>"; copy = "" }
    garment = [ordered]@{ vertex = "shape"; uv = "uv"; material = "materials"; "unit-scale" = "shape"; weight = "weights"; copy = "" }
    cage = [ordered]@{ vertex = "fit:<body>"; "net-vertex" = "net"; copy = "" }
    texture = [ordered]@{ pixel = "image"; material = "material"; copy = "" }
}
$viewEdits = @("select", "active", "hide", "frame", "unused")

$directories = if ($All) { Get-ArtAssetDirectories } else { @($Asset | ForEach-Object { Resolve-ArtAssetDirectory $_ }) }
$manifests = @($directories | ForEach-Object { Read-ArtAssetManifest $_ })
$blender = Resolve-ArtBlender $BlenderPath
$temp = Join-Path ([System.IO.Path]::GetTempPath()) "art-content-$([guid]::NewGuid().ToString('N'))"
$failures = New-Object System.Collections.Generic.List[string]

function Get-ArtCopy($Manifest, [string]$Label) {
    # A fresh copy of the asset's file, and the manifest that names it.
    $folder = Join-Path $temp "$($Manifest.Name)_$Label"
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $path = Join-Path $folder "$($Manifest.Name).blend"
    Copy-Item -LiteralPath $Manifest.SourcePath -Destination $path -Force
    $copy = $Manifest.PSObject.Copy()
    $copy.SourcePath = $path
    return $copy
}

function Get-ArtChanges($Manifest, $Read) {
    # The parts and stages whose fingerprints differ from the asset's content record.
    $parts = @($Manifest.Content.parts.Keys | Where-Object { [string]$Read.parts.$_ -cne $Manifest.Content.parts[$_] })
    $parts += @($Read.parts.PSObject.Properties.Name | Where-Object { -not $Manifest.Content.parts.Contains($_) })
    $stages = @($Manifest.Stages | Where-Object { [string]$Read.stages.$_ -cne $Manifest.Content.stages[$_] })
    return [pscustomobject]@{
        Version = [int]$Read.version -eq $Manifest.Content.version
        Parts = [string[]]@($parts | Sort-Object -Unique)
        Stages = [string[]]$stages
    }
}

function Get-ArtExpectedStages($Manifest, [string]$Part) {
    # The stage of a part and every later one.
    if ([string]::IsNullOrEmpty($Part)) {
        return [string[]]@()
    }

    $prefix = if ($Part.Contains(":")) { $Part.Substring(0, $Part.IndexOf(":") + 1) } else { $Part }
    $entry = @($script:ArtContentParts[$Manifest.Kind] | Where-Object { $_.Split("/", 2)[1] -ceq $prefix }) | Select-Object -First 1
    $index = [array]::IndexOf($Manifest.Stages, $entry.Split("/", 2)[0])
    return [string[]]@($Manifest.Stages | Select-Object -Skip $index)
}

try {
    New-Item -ItemType Directory -Force -Path $temp | Out-Null
    foreach ($manifest in $manifests) {
        $problem = Get-ArtContentProblem $manifest
        if ($null -ne $problem) {
            $failures.Add("$($manifest.RelativeDirectory): $problem")
            Write-Host "FAIL resave $($manifest.RelativeDirectory): $problem"
        }
    }

    if ($failures.Count -gt 0) {
        throw "Make the content records current first:`n - $($failures -join "`n - ")"
    }

    # 1. Resave.
    $resaved = 0
    foreach ($manifest in $manifests) {
        $copy = Get-ArtCopy $manifest "resave"
        $output = Invoke-ArtNative $blender @("--background", "--factory-startup", $copy.SourcePath, "--python-expr",
            "import bpy; bpy.ops.wm.save_mainfile()")
        if ($script:ArtNativeExitCode -ne 0) {
            throw "Blender could not save a copy of $($manifest.Name):`n$(($output | Select-Object -Last 20) -join "`n")"
        }

        $hash = Get-ArtFileSha256 $copy.SourcePath
        $read = Read-ArtContentLine (Invoke-ArtBlender $blender $copy (@("content") + (Get-ArtValidationArguments $manifest $manifest.Stages[-1])))
        $changes = Get-ArtChanges $manifest $read
        $same = $changes.Version -and $changes.Parts.Count -eq 0 -and $changes.Stages.Count -eq 0
        if ($hash -ceq $manifest.Content.sourceSha256 -or -not $same) {
            $why = if ($hash -ceq $manifest.Content.sourceSha256) { "the saved copy has the same bytes" } else { "parts [$($changes.Parts -join ', ')] and stages [$($changes.Stages -join ', ')] differ" }
            $failures.Add("resave $($manifest.RelativeDirectory): $why")
            Write-Host "FAIL resave $($manifest.RelativeDirectory): $why"
        }
        else {
            $resaved++
            Write-Host "PASS resave $($manifest.RelativeDirectory): another file hash, the same $($manifest.Content.parts.Count) parts and $($manifest.Content.stages.Count) stage(s)"
        }

        Remove-Item -LiteralPath (Split-Path -Parent $copy.SourcePath) -Recurse -Force
    }

    Write-Host "Resave: $resaved of $($manifests.Count) asset(s) keep every fingerprint."

    # 2. Sensitivity, on one asset of each kind.
    $proved = 0
    foreach ($kind in $proofAssets.Keys) {
        $manifest = if ($All) { @($manifests | Where-Object { $_.RelativeDirectory -ceq $proofAssets[$kind] }) | Select-Object -First 1 }
        else { @($manifests | Where-Object { $_.Kind -eq $kind }) | Select-Object -First 1 }
        if ($null -eq $manifest) {
            continue
        }

        $clip = $null
        if (@($manifest.Clips).Count -gt 0) {
            $sorted = [string[]]@($manifest.Clips)
            [Array]::Sort($sorted, [StringComparer]::Ordinal)
            $clip = $sorted[0]
        }

        $body = @(Get-ArtContentItems $manifest) | Select-Object -First 1
        $cases = [ordered]@{}
        foreach ($case in $contentEdits[$kind].Keys) {
            $cases[$case] = ([string]$contentEdits[$kind][$case]).Replace("<clip>", [string]$clip).Replace("<body>", [string]$body)
        }

        foreach ($case in $viewEdits) {
            $cases[$case] = ""
        }

        foreach ($case in $cases.Keys) {
            $expected = [string]$cases[$case]
            # An asset of the kind without clips has no clip to change.
            if ($expected -ceq "clip:") {
                Write-Host "SKIP $kind $($manifest.RelativeDirectory) ${case}: the asset has no clips"
                continue
            }

            $copy = Get-ArtCopy $manifest $case
            try {
                $read = Read-ArtContentLine (Invoke-ArtBlender $blender $copy (@("content-proof", "--edit", $case) +
                        (Get-ArtValidationArguments $manifest $manifest.Stages[-1])))
            }
            catch {
                # An asset chosen with -Asset may lack what an edit needs, such as an image.
                $need = [regex]::Match($_.Exception.Message, "The proof edit needs ([^,]+), which the file lacks")
                if (-not $need.Success) {
                    throw
                }

                Write-Host "SKIP $kind $($manifest.RelativeDirectory) ${case}: the file lacks $($need.Groups[1].Value)"
                continue
            }
            finally {
                Remove-Item -LiteralPath (Split-Path -Parent $copy.SourcePath) -Recurse -Force
            }

            $changes = Get-ArtChanges $manifest $read
            $expectedParts = if ($expected) { [string[]]@($expected) } else { [string[]]@() }
            $expectedStages = Get-ArtExpectedStages $manifest $expected
            $what = if ($expected) { "changes $expected and the stages $($expectedStages -join ', ')" } else { "changes nothing" }
            if ($changes.Version -and ($changes.Parts -join ",") -ceq ($expectedParts -join ",") -and ($changes.Stages -join ",") -ceq ($expectedStages -join ",")) {
                $proved++
                Write-Host "PASS $kind $($manifest.RelativeDirectory) ${case}: $what"
            }
            else {
                $failures.Add("$kind $($manifest.RelativeDirectory) ${case}: expected it $what, it changed parts [$($changes.Parts -join ', ')] and stages [$($changes.Stages -join ', ')]")
                Write-Host "FAIL $kind $($manifest.RelativeDirectory) ${case}: expected it $what, it changed parts [$($changes.Parts -join ', ')] and stages [$($changes.Stages -join ', ')]"
            }
        }
    }

    Write-Host "Sensitivity: $proved edit(s) changed exactly what they should."
}
finally {
    if (Test-Path -LiteralPath $temp) {
        Remove-Item -LiteralPath $temp -Recurse -Force
    }
}

if ($failures.Count -gt 0) {
    throw "The content proof failed:`n - $($failures -join "`n - ")"
}

Write-Host "Proved the content of $($manifests.Count) art asset(s)."
