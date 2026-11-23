# Brings the Unity project up to date with the exported art without a focused Editor: runs
# Mmorpg.Client.Editor.ArtExportFollowUp.Apply, which imports the exported files, creates the project
# materials they lack, gives the garment materials their dyes, updates the appearance catalogs with
# their content and Addressables entries, writes the cloth fixtures when they changed, and saves the
# assets. When an Editor has this checkout's project open, it runs there through the Unity CLI;
# otherwise Unity runs headless in batch mode, with the log kept in artifacts/art-build/unity. A run
# with nothing to do changes no file. A run that succeeds removes the marker
# artifacts/art-build/unity/pending that Export-ArtAsset.ps1 leaves after an export, by which
# tools/art/Build-Art.ps1 knows that the follow-up is due. See docs/ART_PIPELINE.md.
#
# Exit codes: 0 the follow-up ran, 1 it failed, 2 it could not run.
param(
    [string]$UnityEditorPath = $env:UNITY_EDITOR_PATH,
    [string]$UnityCliPath = $env:UNITY_CLI_PATH,
    # How long Unity may take, the import of the exports included, before the script ends it.
    [int]$TimeoutMinutes = 30
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "../UnityCommon.ps1")

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$projectPath = Join-Path $repositoryRoot "src\Client"
$method = "Mmorpg.Client.Editor.ArtExportFollowUp.ApplyFromCommandLine"
$marker = "[ArtFollowUp]"
# The marker Export-ArtAsset.ps1 leaves after an export, until a follow-up ran.
$pending = Join-Path $repositoryRoot "artifacts\art-build\unity\pending"

function Stop-Run([int]$ExitCode, [string]$Message) {
    # A follow-up that ran removes the marker an export left, so tools/art/Build-Art.ps1 runs it again
    # only after the next export or after a failure.
    if ($ExitCode -eq 0 -and (Test-Path -LiteralPath $pending -PathType Leaf)) {
        Remove-Item -LiteralPath $pending -Force
    }

    Write-Host "Result: $Message"
    exit $ExitCode
}

function Invoke-UnityCli([string[]]$Arguments) {
    # The exit code, the text, and the JSON answer of one Unity CLI call. Windows PowerShell 5.1 turns
    # redirected native stderr into errors, so they stay suspended while the CLI runs.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $records = @(& $UnityCliPath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    # The answer is the standard output; the error output only explains a failure.
    $stdout = @($records | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join "`n"
    $stderr = @($records | Where-Object { $_ -is [Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join "`n"
    $json = $null
    if ($exitCode -eq 0 -and $stdout.TrimStart().StartsWith("{")) {
        try { $json = $stdout | ConvertFrom-Json } catch { $json = $null }
    }

    return [pscustomobject]@{ ExitCode = $exitCode; Text = (@($stdout, $stderr) | Where-Object { $_ }) -join "`n"; Json = $json }
}

function Invoke-EditorCommand([string]$Name, [string[]]$Arguments = @()) {
    # Asks only the Editor of this project; 'unity status' would make every running Editor log an error.
    return Invoke-UnityCli (@("command", $Name) + $Arguments + @("--project-path", $projectPath, "--result-only", "--no-banner"))
}

function Wait-EditorIdle([string]$Activity) {
    # Waits until the Editor answers and neither compiles nor reloads, as tools/Run-UnityEditorTests.ps1
    # does; a request during a domain reload fails and counts as busy.
    $deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
    while ($true) {
        $state = (Invoke-EditorCommand "editor_status").Json
        if ($state -and -not $state.compiling -and -not $state.domainReloadInProgress -and @("ready", "playing") -contains $state.status) {
            return $state
        }

        if ([DateTime]::UtcNow -ge $deadline) {
            Stop-Run 1 "the Editor did not become idle $Activity within $TimeoutMinutes minutes. A modal dialog in the Editor blocks it."
        }

        Start-Sleep -Seconds 2
    }
}

function Invoke-InEditor {
    # The follow-up in the Editor that has the project open: out of Play Mode, with the exports imported
    # and the scripts compiled first.
    $state = Wait-EditorIdle "before the follow-up"
    if ($state.playMode -ne "stopped") {
        Stop-Run 2 "the Editor is in Play Mode; leave it and run the command again."
    }

    $null = Invoke-EditorCommand "eval" @("--code", "UnityEditor.AssetDatabase.Refresh(UnityEditor.ImportAssetOptions.ForceSynchronousImport); return 0;")
    $null = Wait-EditorIdle "after the refresh"
    $compile = Invoke-EditorCommand "eval" @("--code", "return UnityEditor.EditorUtility.scriptCompilationFailed;")
    if (-not $compile.Json -or -not $compile.Json.success) {
        Stop-Run 1 "the Editor could not report its compile state: $($compile.Text.Trim())"
    }

    if ($compile.Json.result -eq $true) {
        Stop-Run 1 "the scripts do not compile in the Editor; fix them, then run the command again."
    }

    # The code has no double quotes, which Windows PowerShell loses on a native command line.
    $code = "return string.Join(System.Environment.NewLine, Mmorpg.Client.Editor.ArtExportFollowUp.Apply().Lines());"
    $run = Invoke-EditorCommand "eval" @("--code", $code)
    if (-not $run.Json -or -not $run.Json.success) {
        Stop-Run 1 "the follow-up failed in the Editor:`n$($run.Text.Trim())"
    }

    foreach ($line in "$($run.Json.result)".Split("`n")) {
        if ($line.Trim()) {
            Write-Host $line.Trim()
        }
    }

    Stop-Run 0 "the follow-up ran in the Editor that has $projectPath open."
}

function Invoke-InBatchMode {
    # The follow-up in a headless Unity, which imports the exports and compiles the scripts as it opens
    # the project, with a time limit.
    $requiredVersion = Get-UnityRequiredVersion $projectPath
    $editorPath = Get-UnityEditorPath $UnityEditorPath $requiredVersion
    if ([string]::IsNullOrWhiteSpace($editorPath) -or -not (Test-Path -LiteralPath $editorPath)) {
        Stop-Run 2 "Unity Editor $requiredVersion was not found. Set UNITY_EDITOR_PATH to the Unity executable."
    }

    $logFolder = Join-Path $repositoryRoot "artifacts\art-build\unity"
    New-Item -ItemType Directory -Force -Path $logFolder | Out-Null
    $log = Join-Path $logFolder "follow-up.log"
    Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue
    $arguments = @("-batchmode", "-nographics", "-quit", "-projectPath", $projectPath, "-executeMethod", $method, "-logFile", $log)
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $editorPath
    $info.Arguments = ($arguments | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join " "
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    Write-Host "Running the follow-up in a headless Unity $requiredVersion; its log is $log."
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $info
    $started = $false
    $exitCode = $null
    try {
        $null = $process.Start()
        $started = $true
        $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
        # Waits in slices, so an interrupted script reaches the cleanup below.
        while (-not $process.WaitForExit(500)) {
            if ((Get-Date) -gt $deadline) {
                Write-Host "Unity took longer than $TimeoutMinutes minutes; ending it."
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
    }

    $lines = if (Test-Path -LiteralPath $log) { @(Get-Content -LiteralPath $log) } else { @() }
    $reported = @($lines | Where-Object { $_.StartsWith($marker) } | ForEach-Object { $_.Substring($marker.Length).Trim() })
    foreach ($line in @($reported | Where-Object { -not $_.StartsWith("FAILED:") -and $_ -ne "Done." })) {
        Write-Host $line
    }

    $seconds = $watch.Elapsed.TotalSeconds.ToString("0", [System.Globalization.CultureInfo]::InvariantCulture)
    if ($exitCode -eq 0 -and $reported -contains "Done.") {
        Stop-Run 0 "the follow-up ran in a headless Unity in $seconds s."
    }

    # The lines that say why: the follow-up's failure with its stack, compile errors, and a missing
    # method; else the end of the log.
    $failure = @($lines | Where-Object { $_ -match [regex]::Escape("$marker FAILED") -or $_ -match 'error CS\d{4}' -or
            $_ -match 'executeMethod' -or $_ -match '^\s+at ' -or $_ -match 'Exception' } | Select-Object -First 40)
    if ($failure.Count -eq 0) {
        $failure = @($lines | Select-Object -Last 40)
    }

    foreach ($line in $failure) {
        Write-Host "  $line"
    }

    $why = if ($null -eq $exitCode) { "did not end within $TimeoutMinutes minutes" } else { "ended with exit code $exitCode" }
    Stop-Run 1 "the follow-up failed: Unity $why after $seconds s; the lines above are from $log."
}

if (Test-UnityProjectOpen $projectPath) {
    # The project is open: in an Editor that answers the CLI, or in another Unity process.
    if ([string]::IsNullOrWhiteSpace($UnityCliPath)) {
        $command = Get-Command unity -ErrorAction SilentlyContinue
        $UnityCliPath = if ($command) { $command.Source } elseif ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA "Unity\bin\unity.exe" } else { "" }
    }

    if ([string]::IsNullOrWhiteSpace($UnityCliPath) -or -not (Test-Path -LiteralPath $UnityCliPath)) {
        Stop-Run 2 "a Unity process has $projectPath open, and the Unity CLI to reach it was not found; set UNITY_CLI_PATH."
    }

    $status = Invoke-EditorCommand "editor_status"
    if ($status.ExitCode -ne 0 -and "$($status.Text)" -match "EDITOR_NOT_RUNNING|NOT_A_PROJECT") {
        Stop-Run 2 "another Unity process has $projectPath open; let it end, then run the command again."
    }

    Invoke-InEditor
}

Invoke-InBatchMode
