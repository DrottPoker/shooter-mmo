# Builds the Windows x64 player of the Unity client headless, with IL2CPP, for one environment of
# src/Client/Environments, through the Editor build method
# Mmorpg.Client.Editor.ClientPlayerBuild.BuildFromCommandLine. The build bakes the environment
# and a build stamp (commit, whether the working tree had changes, UTC time, development or
# release) into the player. -Development makes a development build, which has the development
# tools and accepts an override of the GameService address; without it the build is a release
# build. The player goes to src/Client/Builds/<environment>-<development|release>/, which Git
# ignores, and the build summary, the build report, and the Unity log go beside that folder.
#
# Unity opens a project only once, so the script refuses to run while an Editor or another Unity
# process has the project open. A Unity run rewrites some of its own tracked files, such as the
# player settings that a development build changes for its duration and a hash in the
# Addressables settings; the script puts every such file back, and it fails when the working
# tree is not as it was before the build.
#
# -StartCheck starts the built player once, windowed, and waits until its log shows that the
# content check ran and the login screen was shown, without an exception, then closes it.
# -OverrideAddress <url> starts it with -gameServiceBaseUrl <url> and checks that a development
# build uses that address and a release build ignores it.
#
# Exit codes: 0 the player was built (and passed the start check), 1 the build or the start check
# failed, 2 the build could not run, 3 the build left the working tree changed.
param(
    # The name of an environment file in src/Client/Environments, such as local.
    [Parameter(Mandatory = $true)]
    [string]$Environment,
    # A development build instead of a release build.
    [switch]$Development,
    # Starts the built player once and checks its log.
    [switch]$StartCheck,
    # With -StartCheck: the address the started player gets with -gameServiceBaseUrl.
    [string]$OverrideAddress,
    # Prints what the script would do and runs nothing.
    [switch]$Plan,
    [string]$UnityEditorPath = $env:UNITY_EDITOR_PATH,
    # How long the build may take before the script ends Unity.
    [int]$TimeoutMinutes = 60,
    # How long the start check waits for the login screen.
    [int]$StartCheckTimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "UnityCommon.ps1")

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectPath = Join-Path $repositoryRoot "src\Client"
$kind = if ($Development) { "development" } else { "release" }
$buildName = "$Environment-$kind"
$buildsFolder = Join-Path $projectPath "Builds"
$outputFolder = Join-Path $buildsFolder $buildName
$executable = Join-Path $outputFolder "MMORPG.exe"
$summaryPath = Join-Path $buildsFolder "$buildName.build-summary.txt"
$reportPath = Join-Path $buildsFolder "$buildName.build-report.json"
$unityLogPath = Join-Path $buildsFolder "$buildName.unity-build.log"
$startLogPath = Join-Path $buildsFolder "$buildName.start-check.log"
$environmentFile = Join-Path $projectPath "Environments\$Environment.json"
# The build file that the Editor bakes into the player for the build; Git ignores it.
$bakedFile = Join-Path $projectPath "Assets\Content\Config\client-build.json"
$buildMethod = "Mmorpg.Client.Editor.ClientPlayerBuild.BuildFromCommandLine"
# The folders that IL2CPP writes beside the player and that are not shipped.
$notShippedPattern = "_BackUpThisFolder_ButDontShipItWithYourGame$|_BurstDebugInformation_DoNotShip$"

# Host output, so a function's own messages never become part of what it returns.
function Write-Step([string]$Message) {
    Write-Host "[build] $Message"
}

function Stop-WithExitCode([int]$Code, [string]$Message) {
    if ($Code -eq 0) {
        Write-Step $Message
    }
    else {
        Write-Step "FAILED: $Message"
    }

    exit $Code
}

function Read-Environment {
    if ($Environment -notmatch '^[a-z0-9][a-z0-9-]{0,63}$') {
        Stop-WithExitCode 2 "The environment name '$Environment' is not lowercase letters, digits, and hyphens."
    }

    if (-not (Test-Path -LiteralPath $environmentFile)) {
        $known = @(Get-ChildItem -LiteralPath (Join-Path $projectPath "Environments") -Filter *.json |
            ForEach-Object { $_.BaseName }) -join ", "
        Stop-WithExitCode 2 "There is no environment '$Environment' in src/Client/Environments. Known: $known."
    }

    try {
        $document = Get-Content -LiteralPath $environmentFile -Raw | ConvertFrom-Json
    }
    catch {
        Stop-WithExitCode 2 "The environment file '$environmentFile' is no JSON: $($_.Exception.Message)"
    }

    if ($document.name -ne $Environment -or [string]::IsNullOrWhiteSpace($document.gameServiceBaseUrl)) {
        Stop-WithExitCode 2 "The environment file '$environmentFile' must name '$Environment' and a gameServiceBaseUrl."
    }

    return $document
}

function Get-FolderBytes([string]$Path) {
    $sum = (Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) { return 0 } else { return [long]$sum }
}

function Format-Megabytes([long]$Bytes) {
    return ("{0:N1} MB" -f ($Bytes / 1MB))
}

# The lines of the Unity log that the C++ compiler or linker of IL2CPP reported as warnings or
# errors, for example "warning C4100" of MSVC. Only these lines are read; the head of a Unity log
# is never printed.
function Get-NativeDiagnostics([string]$LogPath) {
    if (-not (Test-Path -LiteralPath $LogPath)) {
        return [pscustomobject]@{ Warnings = @(); Errors = @() }
    }

    $lines = Get-Content -LiteralPath $LogPath
    return [pscustomobject]@{
        Warnings = @($lines | Where-Object { $_ -match '\bwarning (C|LNK)\d{4}\b' } | Sort-Object -Unique)
        Errors = @($lines | Where-Object { $_ -match '\berror (C|LNK)\d{4}\b' } | Sort-Object -Unique)
    }
}

function Remove-BakedFile {
    foreach ($path in @($bakedFile, "$bakedFile.meta")) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
}

function Invoke-StartCheck([string]$ExpectedAddress) {
    Remove-Item -LiteralPath $startLogPath -Force -ErrorAction SilentlyContinue
    $arguments = @("-logFile", $startLogPath, "-screen-fullscreen", "0", "-screen-width", "1280", "-screen-height", "720")
    if (-not [string]::IsNullOrWhiteSpace($OverrideAddress)) {
        $arguments += @("-gameServiceBaseUrl", $OverrideAddress)
    }

    Write-Step "Start check: starting $executable $($arguments -join ' ')"
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $executable
    $startInfo.Arguments = ($arguments | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' '
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = $outputFolder
    # The player writes its log to the file; what it also prints is read and dropped.
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $player = [System.Diagnostics.Process]::Start($startInfo)
    $null = $player.StandardOutput.ReadToEndAsync()
    $null = $player.StandardError.ReadToEndAsync()
    $deadline = (Get-Date).AddSeconds($StartCheckTimeoutSeconds)
    $shown = $false
    try {
        while ((Get-Date) -lt $deadline -and -not $player.HasExited) {
            if ((Test-Path -LiteralPath $startLogPath) -and
                (Select-String -LiteralPath $startLogPath -SimpleMatch "Login screen shown" -Quiet)) {
                $shown = $true
                # A moment more, so an exception right after the screen appears is in the log.
                Start-Sleep -Seconds 3
                break
            }

            Start-Sleep -Milliseconds 500
        }
    }
    finally {
        if (-not $player.HasExited) {
            $null = $player.CloseMainWindow()
            if (-not $player.WaitForExit(15000)) {
                $player.Kill()
                $null = $player.WaitForExit(10000)
            }
        }

        $player.Dispose()
    }

    $failures = @()
    $lines = if (Test-Path -LiteralPath $startLogPath) { @(Get-Content -LiteralPath $startLogPath -Encoding UTF8) } else { @() }
    $clientLines = @($lines | Where-Object { $_ -match '^\[(CLIENT|AUTH|SIMULATION)\]' })
    $exceptions = @($lines | Where-Object { $_ -match '(^|\s)[A-Za-z0-9_.]*Exception(:|\s*$)' })
    foreach ($line in $clientLines) {
        Write-Step "  $line"
    }

    if (-not ($lines | Where-Object { $_ -match 'The content check resolved' })) {
        $failures += "The log shows no finished content check."
    }

    if (-not $shown) {
        $failures += "The log shows no login screen within $StartCheckTimeoutSeconds s."
    }

    if ($exceptions.Count -gt 0) {
        $failures += "The log shows $($exceptions.Count) exception line(s):"
        $failures += @($exceptions | Select-Object -First 10 | ForEach-Object { "  $_" })
    }

    $environmentLine = @($clientLines | Where-Object { $_ -match 'Environment ' } | Select-Object -First 1)
    if ($environmentLine.Count -eq 0 -or -not $environmentLine[0].Contains($ExpectedAddress)) {
        $failures += "The log does not show the GameService address $ExpectedAddress."
    }

    if (-not [string]::IsNullOrWhiteSpace($OverrideAddress)) {
        $loginLine = @($clientLines | Where-Object { $_ -match 'Login screen shown' } | Select-Object -First 1)
        if ($Development) {
            if (-not ($clientLines | Where-Object { $_ -match 'overridden at start' })) {
                $failures += "The development build did not take the override $OverrideAddress."
            }
        }
        elseif (-not ($clientLines | Where-Object { $_ -match 'release build ignores the GameService override' })) {
            $failures += "The release build did not report that it ignores the override."
        }

        if ($loginLine.Count -gt 0) {
            Write-Step "Login footer: $($loginLine[0])"
        }
    }

    if ($failures.Count -gt 0) {
        Write-Step "Start check log: $startLogPath"
        return $failures
    }

    Write-Step "Start check passed: the content check ran and the login screen was shown without an exception."
    return @()
}

$environmentDocument = Read-Environment
$requiredVersion = Get-UnityRequiredVersion $projectPath
$editorPath = Get-UnityEditorPath $UnityEditorPath $requiredVersion
$expectedAddress = if ($Development -and -not [string]::IsNullOrWhiteSpace($OverrideAddress)) {
    $OverrideAddress.Trim().TrimEnd('/')
}
else {
    ([string]$environmentDocument.gameServiceBaseUrl).Trim().TrimEnd('/')
}

if ($Plan) {
    Write-Output "Build-Client plan, nothing runs:"
    Write-Output "  Environment:  $Environment, $($environmentDocument.gameServiceBaseUrl), from $environmentFile"
    Write-Output "  Kind:         $kind ($(if ($Development) { 'IL2CPP, Low stripping, OptimizeSize, C++ Release, development build' } else { 'IL2CPP, Low stripping, OptimizeSpeed, C++ Master' }))"
    Write-Output "  Unity:        $editorPath, batch mode, $buildMethod"
    Write-Output "  Stamp:        the commit of HEAD, whether git status shows changes, and the UTC time"
    Write-Output "  Output:       $outputFolder (emptied first), summary $summaryPath"
    Write-Output "  Afterwards:   restore the Unity-owned tracked files the build changed, then fail unless git status is as before"
    if ($StartCheck) {
        $overrideText = if ([string]::IsNullOrWhiteSpace($OverrideAddress)) { "" } else { " with -gameServiceBaseUrl $OverrideAddress" }
        Write-Output "  Start check:  start $executable$overrideText, wait for the content check and the login screen, expect $expectedAddress"
    }

    exit 0
}

if ([string]::IsNullOrWhiteSpace($editorPath) -or -not (Test-Path -LiteralPath $editorPath)) {
    Stop-WithExitCode 2 "Unity Editor $requiredVersion was not found. Set UNITY_EDITOR_PATH to the Unity executable."
}

if (Test-UnityProjectOpen $projectPath) {
    Stop-WithExitCode 2 "A Unity Editor or another Unity process has '$projectPath' open. Close it first."
}

$commit = [string]@(Get-GitLines $repositoryRoot @("rev-parse", "HEAD"))[0]
$dirty = @(Get-GitLines $repositoryRoot @("status", "--porcelain", "--untracked-files=all")).Count -gt 0
$builtAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", [Globalization.CultureInfo]::InvariantCulture)
$before = Get-WorkingTreeState $repositoryRoot

New-Item -ItemType Directory -Force -Path $buildsFolder | Out-Null
if (Test-Path -LiteralPath $outputFolder) {
    Remove-Item -LiteralPath $outputFolder -Recurse -Force
}

Remove-Item -LiteralPath $summaryPath, $reportPath, $unityLogPath -Force -ErrorAction SilentlyContinue
Write-Step "Building $buildName from $($commit.Substring(0, 10))$(if ($dirty) { ' with uncommitted changes' }) with Unity $requiredVersion. The Unity log is $unityLogPath."

$arguments = @(
    "-batchmode",
    "-nographics",
    "-quit",
    "-projectPath", $projectPath,
    "-buildTarget", "Win64",
    "-executeMethod", $buildMethod,
    "-logFile", $unityLogPath,
    "-clientEnvironmentFile", $environmentFile,
    "-clientBuildKind", $kind,
    "-clientBuildOutput", $outputFolder,
    "-clientBuildCommit", $commit,
    "-clientBuildDirty", $(if ($dirty) { "true" } else { "false" }),
    "-clientBuildTime", $builtAt,
    "-clientBuildReport", $reportPath
)

$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $editorPath
$startInfo.Arguments = ($arguments | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' '
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $startInfo
$started = $false
$exitCode = $null
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
try {
    if (-not $process.Start()) {
        Stop-WithExitCode 2 "Unity could not start."
    }

    $started = $true
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    # Waits in slices, so an interrupted script reaches the cleanup below.
    while (-not $process.WaitForExit(500)) {
        if ((Get-Date) -gt $deadline) {
            Write-Step "The build took longer than $TimeoutMinutes minutes; ending Unity."
            break
        }
    }

    if ($process.HasExited) {
        $exitCode = $process.ExitCode
    }
}
finally {
    if ($started -and -not $process.HasExited) {
        & taskkill.exe /PID $process.Id /T /F | Out-Null
        $null = $process.WaitForExit(10000)
    }

    $process.Dispose()
    Remove-BakedFile
    foreach ($line in @(Restore-UnityOwnedFiles $before $repositoryRoot)) {
        Write-Step $line
    }
}

$stopwatch.Stop()
$report = $null
if (Test-Path -LiteralPath $reportPath) {
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
}

$native = Get-NativeDiagnostics $unityLogPath
$shippedBytes = 0
$notShippedBytes = 0
if (Test-Path -LiteralPath $outputFolder) {
    foreach ($item in Get-ChildItem -LiteralPath $outputFolder) {
        $bytes = if ($item.PSIsContainer) { Get-FolderBytes $item.FullName } else { $item.Length }
        if ($item.PSIsContainer -and $item.Name -match $notShippedPattern) {
            $notShippedBytes += $bytes
        }
        else {
            $shippedBytes += $bytes
        }
    }
}

$result = if ($null -ne $report) { $report.result } else { "no report (Unity exit code $exitCode)" }
$summary = @(
    "Client build $buildName",
    "  Result:              $result",
    "  Environment:         $Environment ($($environmentDocument.gameServiceBaseUrl))",
    "  Kind:                $kind",
    "  Commit:              $commit$(if ($dirty) { ' with uncommitted changes' })",
    "  Built at:            $builtAt",
    "  Scripting backend:   $(if ($report) { $report.scriptingBackend } else { 'unknown' })",
    "  Stripping level:     $(if ($report) { $report.strippingLevel } else { 'unknown' })",
    "  IL2CPP code gen:     $(if ($report) { $report.il2cppCodeGeneration } else { 'unknown' })",
    "  C++ configuration:   $(if ($report) { $report.il2cppCompilerConfiguration } else { 'unknown' })",
    "  Time:                $([TimeSpan]::FromSeconds([Math]::Round($stopwatch.Elapsed.TotalSeconds)).ToString()) in all, $(if ($report) { [TimeSpan]::FromSeconds([Math]::Round($report.buildSeconds)).ToString() } else { 'unknown' }) of it the player build",
    "  Size:                $(Format-Megabytes $shippedBytes) shipped, $(Format-Megabytes $notShippedBytes) of IL2CPP and Burst debug data beside it",
    "  Build errors:        $(if ($report) { $report.errors } else { 'unknown' })",
    "  Build warnings:      $(if ($report) { $report.warnings } else { 'unknown' })",
    "  C++ errors:          $($native.Errors.Count)",
    "  C++ warnings:        $($native.Warnings.Count)",
    "  Player:              $executable"
)
if ($null -ne $report) {
    foreach ($message in @($report.errorMessages) + @($report.warningMessages)) {
        if (-not [string]::IsNullOrWhiteSpace($message)) {
            $summary += "  Build message:       $(($message -split "`n")[0])"
        }
    }
}

foreach ($line in $native.Warnings | Select-Object -First 20) {
    $summary += "  C++ warning:         $line"
}

$summary | ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines($summaryPath, [string[]]$summary)

if ($null -eq $report -or $report.result -ne "Succeeded" -or $exitCode -ne 0 -or -not (Test-Path -LiteralPath $executable)) {
    if (Test-Path -LiteralPath $unityLogPath) {
        Write-Step "The last lines of the Unity log:"
        Get-Content -LiteralPath $unityLogPath -Tail 60
    }

    Stop-WithExitCode 1 "The build of $buildName failed."
}

$differences = @(Compare-WorkingTreeState $before $repositoryRoot)
if ($differences.Count -gt 0) {
    Write-Step "The working tree is not as it was before the build:"
    $differences | ForEach-Object { Write-Step "  $_" }
    Stop-WithExitCode 3 "The build left the working tree changed."
}

if ($StartCheck) {
    $failures = @(Invoke-StartCheck $expectedAddress)
    if ($failures.Count -gt 0) {
        $failures | ForEach-Object { Write-Step $_ }
        Stop-WithExitCode 1 "The start check of $buildName failed."
    }
}

Stop-WithExitCode 0 "Built $buildName into $outputFolder."
