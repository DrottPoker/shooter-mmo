# Shared helpers for the art pipeline scripts (Render-ArtPreview, Approve-ArtAsset,
# Export-ArtAsset, Verify-ArtAssets). The rules are described in docs/ART_PIPELINE.md.
# Dot-source this file; it works in Windows PowerShell 5.1 and PowerShell 7.

$script:ArtRepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script:ArtSourceRoot = Join-Path $script:ArtRepositoryRoot "art"
$script:ArtModelFolder = "src/Client/Assets/Art/Models"
$script:ArtSkeletonFolder = Join-Path $script:ArtSourceRoot "Skeletons"
$script:ArtBlenderVersion = "5.2"
$script:ArtBlenderCli = Join-Path $PSScriptRoot "blender/art_pipeline_cli.py"

# The review stages each asset kind passes in order. The last stage is the export gate.
$script:ArtReviewStages = @{
    static = @("model")
    skinned = @("model", "rig", "animation")
}

# The stages whose rules Blender can check, and so can be approved.
$script:ArtImplementedStages = @{
    static = @("model")
    skinned = @("model", "rig", "animation")
}

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

function ConvertTo-ArtMaterialRecords($Records) {
    # Returns the material records as one array, or $null when the list is empty or any entry
    # is missing, malformed, or a duplicate, so no invalid entry can drop out unnoticed.
    $result = New-Object System.Collections.Generic.List[object]
    $names = New-Object System.Collections.Generic.HashSet[string]
    foreach ($record in @($Records)) {
        if ($null -eq $record -or
            [string]$record.name -cnotmatch '^M_[A-Za-z0-9_]+$' -or
            [string]$record.baseColor -cnotmatch '^#[0-9A-F]{6}$' -or
            -not $names.Add([string]$record.name)) {
            return $null
        }

        $numbers = @()
        foreach ($field in @("metallic", "smoothness")) {
            $value = $record.$field
            if (-not ($value -is [int] -or $value -is [long] -or $value -is [double] -or $value -is [decimal]) -or
                $value -lt 0 -or $value -gt 1) {
                return $null
            }

            $numbers += [double]$value
        }

        $result.Add([ordered]@{
                name = [string]$record.name
                baseColor = [string]$record.baseColor
                metallic = $numbers[0]
                smoothness = $numbers[1]
            })
    }

    if ($result.Count -eq 0) {
        return $null
    }

    return , $result.ToArray()
}

function Read-ArtSkeleton([string]$Name) {
    # Returns the path of art/Skeletons/<Name>.json after checking its bones: unique names, one
    # root, every parent before its children, and a deform flag on every bone.
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

function Read-ArtAssetManifest([string]$AssetDirectory) {
    $name = Split-Path -Leaf $AssetDirectory
    $manifestPath = Join-Path $AssetDirectory "asset.json"
    $where = Get-ArtRelativePath $manifestPath
    try {
        $json = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    }
    catch {
        throw "${where}: invalid JSON. $($_.Exception.Message)"
    }

    $problems = New-Object System.Collections.Generic.List[string]
    if ($name -notmatch $script:ArtNamePattern) {
        $problems.Add("the folder name '$name' must use only letters, digits, and underscores")
    }

    if ($json.schemaVersion -ne 1) {
        $problems.Add("schemaVersion must be 1")
    }

    $stages = $script:ArtReviewStages[[string]$json.kind]
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

    if (-not ($json.triangleBudget -is [int] -or $json.triangleBudget -is [long]) -or $json.triangleBudget -le 0) {
        $problems.Add("triangleBudget must be a positive whole number")
    }

    # A skinned asset names its shared skeleton and the animation clips the game plays on it.
    $skeleton = $null
    $skeletonPath = $null
    $clips = @()
    if ([string]$json.kind -eq "skinned") {
        $skeleton = [string]$json.skeleton
        $clips = @($json.clips | ForEach-Object { [string]$_ })
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

        if ($clips.Count -eq 0 -or @($clips | Where-Object { $_ -cnotmatch $script:ArtNamePattern }).Count -gt 0 -or
            @($clips | Sort-Object -Unique).Count -ne $clips.Count) {
            $problems.Add("clips must list one or more unique animation clip names")
        }
    }
    elseif ($null -ne $json.skeleton -or $null -ne $json.clips) {
        $problems.Add("only a skinned asset has a skeleton and clips")
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
    if ($null -ne $json.export) {
        $materials = ConvertTo-ArtMaterialRecords $json.export.materials
        if ([string]$json.export.sourceSha256 -cnotmatch $script:ArtShaPattern -or
            [string]$json.export.targetSha256 -cnotmatch $script:ArtShaPattern) {
            $problems.Add("export needs a sourceSha256 and a targetSha256")
        }
        elseif ($null -eq $materials) {
            $problems.Add("export.materials must list every exported material once with its name, baseColor (#RRGGBB), metallic, and smoothness")
        }
        else {
            $export = [ordered]@{
                sourceSha256 = [string]$json.export.sourceSha256
                targetSha256 = [string]$json.export.targetSha256
                materials = $materials
            }
        }
    }

    $sourcePath = Join-Path $AssetDirectory "$name.blend"
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
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
        ImplementedStages = $script:ArtImplementedStages[[string]$json.kind]
        Target = $target
        TargetPath = Join-Path $script:ArtRepositoryRoot $target
        RootObjects = [string[]]$roots
        TriangleBudget = [int]$json.triangleBudget
        Skeleton = $skeleton
        SkeletonPath = $skeletonPath
        Clips = [string[]]$clips
        Approvals = $approvals
        Export = $export
    }
}

function Write-ArtAssetManifest($Manifest) {
    # Written by hand so Windows PowerShell 5.1 and PowerShell 7 produce identical files.
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
    $lines.Add("    `"triangleBudget`": $($Manifest.TriangleBudget),")
    if ($Manifest.Kind -eq "skinned") {
        $lines.Add("    `"skeleton`": `"$($Manifest.Skeleton)`",")
        $clips = (@($Manifest.Clips) | ForEach-Object { '"' + $_ + '"' }) -join ", "
        $lines.Add("    `"clips`": [$clips],")
    }
    $approvedStages = @($Manifest.Stages | Where-Object { $Manifest.Approvals.Contains($_) })
    if ($approvedStages.Count -eq 0) {
        $lines.Add('    "approvals": {},')
    }
    else {
        $lines.Add('    "approvals": {')
        for ($i = 0; $i -lt $approvedStages.Count; $i++) {
            $approval = $Manifest.Approvals[$approvedStages[$i]]
            $comma = if ($i -lt $approvedStages.Count - 1) { "," } else { "" }
            $lines.Add("        `"$($approvedStages[$i])`": {")
            $lines.Add("            `"date`": `"$($approval.date)`",")
            $lines.Add("            `"sourceSha256`": `"$($approval.sourceSha256)`"")
            $lines.Add("        }$comma")
        }
        $lines.Add("    },")
    }

    if ($null -eq $Manifest.Export) {
        $lines.Add('    "export": null')
    }
    else {
        $lines.Add('    "export": {')
        $lines.Add("        `"sourceSha256`": `"$($Manifest.Export.sourceSha256)`",")
        $lines.Add("        `"targetSha256`": `"$($Manifest.Export.targetSha256)`",")
        $lines.Add('        "materials": [')
        $materials = @($Manifest.Export.materials)
        for ($i = 0; $i -lt $materials.Count; $i++) {
            $material = $materials[$i]
            $comma = if ($i -lt $materials.Count - 1) { "," } else { "" }
            $lines.Add("            {")
            $lines.Add("                `"name`": `"$($material.name)`",")
            $lines.Add("                `"baseColor`": `"$($material.baseColor)`",")
            $lines.Add("                `"metallic`": $(Format-ArtNumber $material.metallic),")
            $lines.Add("                `"smoothness`": $(Format-ArtNumber $material.smoothness)")
            $lines.Add("            }$comma")
        }
        $lines.Add("        ]")
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
    # Runs one art_pipeline_cli.py command and returns its ART_ result lines.
    $output = Invoke-ArtNative $Blender (@(
            "--background", "--factory-startup", $Manifest.SourcePath, "--python-exit-code", "1",
            "--python", $script:ArtBlenderCli, "--") + $Arguments)
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
    $arguments = @(
        "--kind", $Manifest.Kind,
        "--roots", ($Manifest.RootObjects -join ","),
        "--triangle-budget", [string]$Manifest.TriangleBudget,
        "--stage", $Stage)
    if ($Manifest.Kind -eq "skinned") {
        $arguments += @("--skeleton", $Manifest.SkeletonPath, "--clips", ($Manifest.Clips -join ","))
    }

    return $arguments
}
