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

foreach ($platform in @("EditMode", "PlayMode")) {
    $resultFile = Join-Path $ResultsPath "$platform-results.xml"
    $logFile = Join-Path $ResultsPath "$platform-unity.log"
    $arguments = @(
        "-batchmode",
        "-nographics",
        "-projectPath", $resolvedProjectPath,
        "-runTests",
        "-testPlatform", $platform,
        "-testResults", $resultFile,
        "-logFile", $logFile
    )

    $process = Start-Process -FilePath $UnityEditorPath -ArgumentList $arguments -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $logFile) {
            Get-Content -LiteralPath $logFile -Tail 100
        }

        throw "Unity $platform tests failed with exit code $($process.ExitCode)."
    }

    if (-not (Test-Path -LiteralPath $resultFile)) {
        throw "Unity $platform tests did not produce '$resultFile'."
    }
}

Write-Output "Unity EditMode and PlayMode tests passed with Unity $requiredVersion."
