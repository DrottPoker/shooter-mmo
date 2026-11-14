# Shared helpers of the scripts that start local services as background processes and stop
# them again: tools/Test-LiveStack.ps1 and tools/Test-Load.ps1. Dot-source it after setting
# $StackStepPrefix, the tag in front of every progress line.

function Write-Step([string]$Message) {
    Write-Host "[$StackStepPrefix] $Message"
}

# Runs a native command with its output in a log file and returns its exit code. Windows
# PowerShell 5.1 turns redirected native stderr into errors under "Stop", so errors stay
# suspended while it runs and the exit code decides.
function Invoke-Logged([string]$FilePath, [string[]]$ArgumentList, [string]$LogPath) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $FilePath @ArgumentList *> $LogPath
        $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

function Get-NativeLines([string]$FilePath, [string[]]$ArgumentList) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $lines = @(& $FilePath @ArgumentList 2>$null)
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Lines = @($lines | Where-Object { $_ } | ForEach-Object { "$_".Trim() }) }
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

# Reads a file that a running process still writes, without locking it.
function Read-SharedText([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return ""
    }

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        (New-Object IO.StreamReader($stream)).ReadToEnd()
    }
    finally {
        $stream.Dispose()
    }
}

function Write-LogTail([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        Get-Content -LiteralPath $Path -Tail 30 | ForEach-Object { Write-Host "  $_" }
    }
}

# Lets this script's process, and so every process it starts from now on, handle Ctrl+C. A
# process inherits whether it ignores Ctrl+C from the process that starts it, and a script started
# from Git Bash or from a host that starts it in a new process group ignores Ctrl+C. Its services
# would then ignore the Ctrl+C that Stop-StackProcess sends, wait the full 30 seconds, and be
# ended without their shutdown.
function Enable-ControlC {
    if (-not ("StackConsoleInput" -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class StackConsoleInput
{
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
}
'@
    }

    [void][StackConsoleInput]::SetConsoleCtrlHandler([IntPtr]::Zero, $false)
}

# Starts a process in its own hidden console, so it can get Ctrl+C on its own, with its output
# in <LogRoot>/<Name>.log and <Name>.err.log and the given environment variables set for it only.
function Start-StackProcess([string]$Name, [string]$FilePath, [string[]]$ArgumentList, [hashtable]$Environment, [string]$LogRoot, [string]$WorkingDirectory) {
    Enable-ControlC
    $previous = @{}
    foreach ($key in $Environment.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $Environment[$key])
    }

    try {
        $startArguments = @{
            FilePath = $FilePath
            WorkingDirectory = $WorkingDirectory
            WindowStyle = "Hidden"
            PassThru = $true
            RedirectStandardOutput = (Join-Path $LogRoot "$Name.log")
            RedirectStandardError = (Join-Path $LogRoot "$Name.err.log")
        }
        if ($ArgumentList.Count -gt 0) {
            # Start-Process joins the arguments with spaces, so one with a space is quoted.
            $startArguments.ArgumentList = @($ArgumentList | ForEach-Object {
                if ($_ -match '\s' -and -not ($_.StartsWith('"') -and $_.EndsWith('"'))) { '"' + $_ + '"' } else { $_ }
            })
        }

        Start-Process @startArguments
    }
    finally {
        foreach ($key in $previous.Keys) {
            [Environment]::SetEnvironmentVariable($key, $previous[$key])
        }
    }
}

# Sends Ctrl+C to the console of a process that Start-StackProcess started. A helper process
# attaches to that console, because attaching detaches a process from its own console.
function Send-ControlC([int]$ProcessId) {
    $helper = @'
using System;
using System.Runtime.InteropServices;
public static class ConsoleControl
{
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
'@
    $command = "Add-Type -TypeDefinition @'`n$helper`n'@`n" +
        "[void][ConsoleControl]::FreeConsole()`n" +
        "if (-not [ConsoleControl]::AttachConsole($ProcessId)) { exit 1 }`n" +
        "[void][ConsoleControl]::SetConsoleCtrlHandler([IntPtr]::Zero, `$true)`n" +
        "if (-not [ConsoleControl]::GenerateConsoleCtrlEvent(0, 0)) { exit 2 }`n" +
        "exit 0"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $sender = Start-Process -FilePath "powershell.exe" `
        -ArgumentList "-NoProfile", "-NonInteractive", "-EncodedCommand", $encoded `
        -WindowStyle Hidden -Wait -PassThru
    $sender.ExitCode -eq 0
}

# Stops a process with Ctrl+C, as its terminal would, and ends it when it does not stop in time.
function Stop-StackProcess($Process, [string]$Name) {
    if ($null -eq $Process -or $Process.HasExited) {
        return
    }

    $watch = [Diagnostics.Stopwatch]::StartNew()
    if (-not (Send-ControlC $Process.Id)) {
        Write-Step "Ctrl+C could not be sent to $Name; ending the process."
    }
    elseif ($Process.WaitForExit(30000)) {
        Write-Step ("$Name stopped in {0:N1} s." -f $watch.Elapsed.TotalSeconds)
        return
    }
    else {
        Write-Step "$Name did not stop on Ctrl+C within 30 seconds; ending the process."
    }

    Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    $null = $Process.WaitForExit(10000)
}

function Test-HttpReady([string]$Url) {
    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2
        $response.StatusCode -eq 200
    }
    catch {
        $false
    }
}

function Test-ShardOnline([string]$GameServiceUrl, [string]$ShardId) {
    try {
        $shards = @(Invoke-RestMethod -Uri "$GameServiceUrl/api/shards" -TimeoutSec 2)
        @($shards | Where-Object { $_.id -eq $ShardId -and $_.isOnline }).Count -eq 1
    }
    catch {
        $false
    }
}

# Waits until the condition holds; fails when the process ended or the time ran out, and then
# shows the tail of its log.
function Wait-Until([scriptblock]$Condition, [string]$What, $Process, [string]$LogPath, [int]$TimeoutSeconds, [string]$LogRoot) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not (& $Condition)) {
        if ($Process.HasExited) {
            Write-LogTail $LogPath
            throw "$What failed: the process ended; its log is in '$LogRoot'."
        }

        if ([DateTime]::UtcNow -ge $deadline) {
            Write-LogTail $LogPath
            throw "$What did not happen within $TimeoutSeconds seconds; the logs are in '$LogRoot'."
        }

        Start-Sleep -Milliseconds 500
    }
}

# The process that listens on a TCP port, or null.
function Get-TcpListener([int]$Port) {
    Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
}

# The process that holds a UDP port, or null.
function Get-UdpListener([int]$Port) {
    Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue | Select-Object -First 1
}
