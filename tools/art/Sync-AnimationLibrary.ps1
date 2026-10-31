# Builds or updates the .blend file of an animation library from the characters its asset.json
# names: the rig's armature, renamed after the library's root object, in the Export collection
# to key the clips on, and in the Reference collection the rig's body bound to it, every preview
# character copying its pose, and a ground grid. A library whose armature is already the current
# rig is left as it is. Otherwise its armature and bodies are replaced and every action is kept,
# so the file changes and needs a new review and approval. -Generate adds clips of the human gait
# model that the library lacks, as starting points to edit by hand, and never replaces a clip. A
# library on the Humanoid skeleton then gets a report of how far a foot on the ground slides in its
# Idle, Walk, and Jog clips. See docs/ART_PIPELINE.md.
param(
    [Parameter(Mandatory = $true)]
    [string]$Asset,
    # Rebuilds the armature and the bodies even when the rig did not change.
    [switch]$Force,
    # Clips of the gait model to add when the library lacks them: Idle, Walk, Jog.
    [string[]]$Generate = @(),
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

# A clip asked for twice is added once.
$Generate = @($Generate | Select-Object -Unique)

$manifest = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Asset) -AllowMissingSource
if ($manifest.Kind -ne "animations") {
    throw "$($manifest.Name) is a $($manifest.Kind) asset; only an animation library is synced from its rig."
}

foreach ($character in @($manifest.Rig) + @($manifest.Previews)) {
    $item = Get-Item -LiteralPath $character.SourcePath
    if ($item.Length -lt 1024 -and [System.IO.File]::ReadAllText($item.FullName).StartsWith("version https://git-lfs")) {
        throw "$($character.SourcePath) is a Git LFS pointer; run git lfs pull first."
    }
}

$arguments = @(
    "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
    "--python", $script:ArtBlenderCli, "--", "library-sync",
    "--library", $manifest.SourcePath,
    "--armature", $manifest.RootObjects[0],
    "--rig-source", $manifest.Rig.SourcePath,
    "--rig-root", $manifest.Rig.Root)
foreach ($preview in @($manifest.Previews)) {
    $arguments += @("--preview", "$($preview.SourcePath)=$($preview.Root)")
}

if ($Force) {
    $arguments += "--force"
}

$blender = Resolve-ArtBlender $BlenderPath
$output = Invoke-ArtNative $blender $arguments
$errors = @($output | Where-Object { $_ -like "ART_PIPELINE_ERROR *" } |
    ForEach-Object { $_.Substring("ART_PIPELINE_ERROR ".Length) })
if ($errors.Count -gt 0) {
    throw "$($manifest.Name) cannot be synced:`n - $($errors -join "`n - ")"
}

$result = @($output | Where-Object { $_ -like "ART_LIBRARY *" }) | Select-Object -First 1
if ($script:ArtNativeExitCode -ne 0 -or $null -eq $result) {
    throw "Blender failed with exit code $script:ArtNativeExitCode for $($manifest.Name):`n$(($output | Select-Object -Last 30) -join "`n")"
}

$changed = $result -notlike "ART_LIBRARY up-to-date *"
if ($changed) {
    Write-Host "Synced $($manifest.Name) from $($manifest.Rig.Asset): $($manifest.SourcePath)"
}
else {
    Write-Host "$($manifest.Name) is up to date: its armature is the current rig of $($manifest.Rig.Asset)."
}

if ($manifest.Skeleton -ne "Humanoid") {
    if ($Generate.Count -gt 0) {
        throw "The gait model makes clips of the Humanoid skeleton only; $($manifest.Name) has the $($manifest.Skeleton) skeleton."
    }
}
else {
    $humanBase = Join-Path $PSScriptRoot "blender/human_base_cli.py"
    function Invoke-HumanBaseLibraryStep([string[]]$StepArguments) {
        $stepOutput = Invoke-ArtNative $blender (@(
                "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
                "--python", $humanBase, "--") + $StepArguments)
        $failures = @($stepOutput | Where-Object { $_ -like "HUMAN_BASE_FAILED *" } |
            ForEach-Object { $_.Substring("HUMAN_BASE_FAILED ".Length) })
        if ($failures.Count -gt 0 -or $script:ArtNativeExitCode -ne 0) {
            throw "The '$($StepArguments[0])' step failed for $($manifest.Name):`n - $(@($failures + @($stepOutput | Select-Object -Last 20)) -join "`n - ")"
        }

        return $stepOutput
    }

    $generated = @()
    if ($Generate.Count -gt 0) {
        $stepOutput = Invoke-HumanBaseLibraryStep @("generate-clips", $manifest.SourcePath, $manifest.RootObjects[0], ($Generate -join ","), $manifest.SkeletonPath)
        foreach ($line in @($stepOutput | Where-Object { $_ -like "KEPT *" })) {
            Write-Host $line.Substring("KEPT ".Length)
        }

        $generated = @($Generate | Where-Object { $manifest.Clips -cnotcontains $_ })
        if ($generated.Count -gt 0) {
            # The record of the last export no longer lists every clip; the next export writes it.
            $manifest.Clips = [string[]](@($manifest.Clips) + $generated)
            $manifest.Export = $null
            Write-ArtAssetManifest $manifest
            Write-Host "Added $($generated -join ', ') to $($manifest.Name) and its asset.json; edit them in Blender as you like."
        }

        if (@($stepOutput | Where-Object { $_ -like "GENERATED *" }).Count -gt 0) {
            $changed = $true
        }
    }

    $stepOutput = Invoke-HumanBaseLibraryStep @("check-clips", $manifest.SourcePath, $manifest.RootObjects[0])
    foreach ($line in @($stepOutput | Where-Object { $_ -like "HUMAN_BASE_REPORT *" })) {
        Write-Host $line.Substring("HUMAN_BASE_REPORT ".Length)
    }
}

if ($changed -and @($manifest.Clips).Count -gt 0) {
    Write-Host "Review the clips, then record the approval with tools/art/Approve-ArtAsset.ps1 -Stage animation."
}
