# Fails unless every test in the TRX files below the results directory passed. A skipped
# infrastructure test means a missing service or MMORPG_TEST_* variable, so a test that did
# not run counts as a failure too.
param(
    [string]$ResultsDirectory = "TestResults",
    [string]$FileName = "test-results.trx"
)

$ErrorActionPreference = "Stop"

$results = @(Get-ChildItem -Path $ResultsDirectory -Recurse -Filter $FileName)
if ($results.Count -eq 0) {
    throw "No test result files named $FileName were written below $ResultsDirectory."
}

$testCount = 0
$notPassed = foreach ($result in $results) {
    [xml]$trx = Get-Content -Raw -LiteralPath $result.FullName
    $unitTestResults = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_ })
    $testCount += $unitTestResults.Count
    $unitTestResults |
        Where-Object { $_.outcome -ne "Passed" } |
        ForEach-Object { "$($_.testName): $($_.outcome)" }
}
if ($notPassed) {
    throw "Tests did not run or pass:`n$($notPassed -join "`n")"
}

Write-Output "All $testCount tests in $($results.Count) test result files passed."
