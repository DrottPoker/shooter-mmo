param(
    [string]$UnityEditorPath = $env:UNITY_EDITOR_PATH,
    [string]$ProjectPath = (Join-Path $PSScriptRoot "..\shooter-mmorpg-unity-client"),
    [string]$ResultsPath = (Join-Path $PSScriptRoot "..\TestResults\Unity")
)

$ErrorActionPreference = "Stop"
$requiredVersion = ((Get-Content (Join-Path $ProjectPath "ProjectSettings\ProjectVersion.txt")) |
    Where-Object { $_ -match '^m_EditorVersion: ' }) -replace '^m_EditorVersion: ', ''

if ([string]::IsNullOrWhiteSpace($UnityEditorPath)) {
    if ($IsWindows -or $env:OS -eq "Windows_NT") {
        $UnityEditorPath = "C:\Program Files\Unity\Hub\Editor\$requiredVersion\Editor\Unity.exe"
    }
}

if ([string]::IsNullOrWhiteSpace($UnityEditorPath) -or -not (Test-Path -LiteralPath $UnityEditorPath)) {
    throw "Unity Editor $requiredVersion was not found. Set UNITY_EDITOR_PATH to the Unity executable."
}

$resolvedProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
New-Item -ItemType Directory -Force -Path $ResultsPath | Out-Null

function ConvertTo-ProcessArgument {
    param([string]$Value)

    if ($Value.Contains('"')) {
        throw "Process arguments cannot contain double quotes."
    }

    if ($Value -match '\s') {
        return '"' + $Value + '"'
    }

    return $Value
}

foreach ($platform in @("EditMode", "PlayMode")) {
    $resultFile = Join-Path $ResultsPath "$platform-results.xml"
    $logFile = Join-Path $ResultsPath "$platform-unity.log"
    Remove-Item -LiteralPath $resultFile, $logFile -Force -ErrorAction SilentlyContinue
    $arguments = @(
        "-batchmode",
        "-nographics",
        "-projectPath", $resolvedProjectPath,
        "-runTests",
        "-testPlatform", $platform,
        "-testResults", $resultFile,
        "-logFile", $logFile
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $UnityEditorPath
    $startInfo.Arguments = ($arguments | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' '
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Unity $platform tests could not start the Unity process."
        }

        $process.WaitForExit()
        $exitCode = $process.ExitCode
    }
    finally {
        $process.Dispose()
    }

    if ($exitCode -ne 0) {
        if (Test-Path -LiteralPath $logFile) {
            Get-Content -LiteralPath $logFile -Tail 100
        }

        throw "Unity $platform tests failed with exit code $exitCode."
    }

    if (-not (Test-Path -LiteralPath $resultFile)) {
        throw "Unity $platform tests did not produce '$resultFile'."
    }

    try {
        [xml]$testResults = Get-Content -LiteralPath $resultFile -Raw
        $testRun = $testResults.'test-run'
    }
    catch {
        throw "Unity $platform tests produced an invalid result file '$resultFile': $($_.Exception.Message)"
    }

    if ($null -eq $testRun -or $testRun.result -ne "Passed" -or [int]$testRun.failed -ne 0) {
        throw "Unity $platform test results did not report a complete passing suite."
    }

    Write-Output "Unity $platform tests passed: $($testRun.passed)/$($testRun.total)."
}

Write-Output "Unity EditMode and PlayMode tests passed with Unity $requiredVersion."
