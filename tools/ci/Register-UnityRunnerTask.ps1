# Registers the scheduled task that keeps the self-hosted Unity runner running, as
# docs/LOCAL_DEVELOPMENT.md describes under Unity CI Runner. The task runs the runner's run.cmd
# under the current Windows account, only while that account is signed in, because Unity needs
# its Unity CLI sign-in and Personal license. It starts the runner without a window at sign-in,
# after wake from sleep, and on a short repeating check, so a runner that exits for any reason
# comes back on its own. The Task Scheduler service starts it, so it does not belong to the
# process tree of a terminal or an app that may close.
#
# Run it from a normal PowerShell window. It is safe to run again: it updates the task, removes
# the old Startup folder shortcut, and starts the task. A listener that runs outside the task,
# for example one started from a terminal, is stopped first while it is idle, because two
# listeners for one runner conflict. It must run on Windows PowerShell 5.1.
[CmdletBinding()]
param(
    [string]$RunnerRoot = "C:\actions-runner",
    [string]$TaskName = "GitHub Actions Unity runner",
    [ValidateRange(1, 60)]
    [int]$CheckIntervalMinutes = 5
)

$ErrorActionPreference = "Stop"

$RunnerRoot = (Resolve-Path -LiteralPath $RunnerRoot).ProviderPath.TrimEnd("\")
$runCmd = Join-Path $RunnerRoot "run.cmd"
if (-not (Test-Path -LiteralPath $runCmd) -or -not (Test-Path -LiteralPath (Join-Path $RunnerRoot ".runner"))) {
    throw "No configured runner in '$RunnerRoot'. Register it first as docs/LOCAL_DEVELOPMENT.md describes."
}

function Get-RunnerProcess {
    param([string]$Name)

    # Callers wrap the result in @(): Windows PowerShell 5.1 has no Count on a single CimInstance.
    $prefix = $RunnerRoot + "\"
    Get-CimInstance Win32_Process -Filter "Name = '$Name'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) }
}

function ConvertTo-XmlText {
    param([string]$Value)

    [System.Security.SecurityElement]::Escape($Value)
}

# A listener started outside the task would make the task's listener fail with a session conflict.
$existingTask = Get-ScheduledTask -TaskPath "\" -TaskName $TaskName -ErrorAction SilentlyContinue
if (-not $existingTask -or $existingTask.State -ne "Running") {
    $outsideListeners = @(Get-RunnerProcess "Runner.Listener.exe")
    if ($outsideListeners.Count -gt 0) {
        if (@(Get-RunnerProcess "Runner.Worker.exe").Count -gt 0) {
            throw "The runner is running a job outside the task. Run this script again when the job has finished."
        }

        Write-Output "Stopping the idle runner listener that runs outside the task."
        $outsideListeners | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    }
}

$systemDirectory = [Environment]::GetFolderPath("System")
$command = Join-Path $systemDirectory "conhost.exe"
$arguments = "--headless " + (Join-Path $systemDirectory "cmd.exe") + " /d /c `"$runCmd`""
$userSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$startBoundary = (Get-Date).ToString("yyyy-MM-dd'T'HH:mm:ss", [System.Globalization.CultureInfo]::InvariantCulture)
$wakeQuery = "<QueryList><Query Id=`"0`" Path=`"System`"><Select Path=`"System`">" +
    "*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]</Select></Query></QueryList>"
$description = "Keeps the self-hosted GitHub Actions runner in $RunnerRoot running. " +
    "Registered by tools/ci/Register-UnityRunnerTask.ps1 in DrottPoker/shooter-mmo."

# conhost --headless gives run.cmd a console without a window, so no window can be closed or
# paused by a click. IgnoreNew keeps one runner; the repeating trigger restarts it after it
# exits. Priority 4 is normal priority; the Task Scheduler default of 7 slows Unity down.
$taskXml = @"
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>$(ConvertTo-XmlText $description)</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>$userSid</UserId>
    </LogonTrigger>
    <EventTrigger>
      <Enabled>true</Enabled>
      <Subscription>$(ConvertTo-XmlText $wakeQuery)</Subscription>
    </EventTrigger>
    <TimeTrigger>
      <Enabled>true</Enabled>
      <StartBoundary>$startBoundary</StartBoundary>
      <Repetition>
        <Interval>PT$($CheckIntervalMinutes)M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$userSid</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$(ConvertTo-XmlText $command)</Command>
      <Arguments>$(ConvertTo-XmlText $arguments)</Arguments>
      <WorkingDirectory>$(ConvertTo-XmlText $RunnerRoot)</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"@

Register-ScheduledTask -TaskPath "\" -TaskName $TaskName -Xml $taskXml -Force | Out-Null
Write-Output "Registered the scheduled task '$TaskName'."

$shortcut = Join-Path ([Environment]::GetFolderPath("Startup")) "GitHub Actions runner.lnk"
if (Test-Path -LiteralPath $shortcut) {
    Remove-Item -LiteralPath $shortcut
    Write-Output "Removed the Startup folder shortcut that the task replaces."
}

Start-ScheduledTask -TaskPath "\" -TaskName $TaskName
$deadline = (Get-Date).AddSeconds(30)
while (@(Get-RunnerProcess "Runner.Listener.exe").Count -eq 0) {
    if ((Get-Date) -gt $deadline) {
        throw "The task started, but no runner listener is running. Check Get-ScheduledTaskInfo -TaskName '$TaskName' and $RunnerRoot\_diag."
    }

    Start-Sleep -Seconds 1
}

Write-Output "The runner listener is running. GitHub shows the runner online once it has connected."
