# Runs the checks that the changes of this branch can affect. Before a push it takes the place of
# the full quality gate in README.md, which CI runs for every pull request, so a task waits
# seconds or a few minutes instead of the whole gate. It speeds up the loop and never replaces CI.
#
# The changes are every file that differs from the merge base with -Base, committed or not, and
# every untracked file. The first rule in $rules that matches a file names the checks the file can
# affect. A .NET file selects the projects that contain it, through the project folder or an item
# that a project file links, and every project that references those projects, and their test
# projects run. A file that no rule matches runs the full gate; -VerifyRules, which CI runs, fails
# on such a file so that every new kind of file gets its rule.
#
# Exit codes: 0 every selected check passed, 1 a check failed, 2 the checks could not start.
param(
    [string]$Base = "origin/development",
    # Repository paths to check instead of the changes of the branch. With -Plan it shows what a
    # change to them would run.
    [string[]]$Path = @(),
    # Runs the full quality gate whatever changed.
    [switch]$All,
    # Prints the changed files and the checks they select, and runs nothing.
    [switch]$Plan,
    # Runs no test of Category=Integration, so PostgreSQL and Redis are not needed.
    [switch]$SkipIntegration,
    # Leaves the Unity suites to CI.
    [switch]$SkipUnity,
    # Fails when a tracked file matches no rule or a rule names an unknown project, and runs
    # nothing else.
    [switch]$VerifyRules
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# The checks a rule can name:
#   full             the full quality gate
#   none             nothing to check
#   owner            the .NET projects that contain the file, if any, and every project that
#                    references them; a non-code file of a production or tool project also runs
#                    every .NET test, because tests read the configuration of other projects
#   owner!           the same, and the full gate when no project contains the file
#   all-tests        every .NET test project
#   test:<project>   one .NET test project
#   unity            the Unity EditMode and PlayMode suites
#   art              tools/Verify-ArtAssets.ps1
#   deps             tools/Verify-DependencyPolicy.ps1
#   observability    tools/Verify-Observability.ps1
#   verify-content   the item catalog, world collision, and world actor verifiers
#   verify-protocol  the realtime protocol verifier
# A content compiler's verifier also runs whenever its tool project is selected, and every
# changed PowerShell script gets a syntax check.
$rules = @(
    @{ Pattern = '^(Directory\.Build\.(props|targets)|global\.json|Mmorpg\.slnx|\.editorconfig|\.gitattributes|tests/Directory\.Build\.props|tests/TestOutput/.+|tools/Test-Changes\.ps1)$'
        Checks = @("full"); Reason = "shared build, format, or test settings" },
    @{ Pattern = '^docs/SERVICE_FEATURES\.md$'
        Checks = @("test:Mmorpg.SimulationWorker.Tests"); Reason = "a realtime metric test reads it" },
    @{ Pattern = '\.md$'
        Checks = @("none"); Reason = "documentation" },
    @{ Pattern = '^(\.github/.+|\.gitignore|\.mcp\.json|\.env\.example|docker-compose\.yml|docker-compose\.stack-stress\.yml)$'
        Checks = @("none"); Reason = "read by no local check; CI runs its own workflow" },
    @{ Pattern = '^docker-compose\.test\.yml$'
        Checks = @("all-tests"); Reason = "the test containers" },
    @{ Pattern = '^(observability/.+|docker-compose\.observability\.yml|tools/Verify-Observability\.ps1)$'
        Checks = @("observability", "all-tests"); Reason = "monitoring configuration, whose metric names tests compare" },
    @{ Pattern = '^(content|tests/Fixtures|src/Shared/WorldData/Generated)/'
        Checks = @("owner", "all-tests", "unity", "verify-content"); Reason = "content or generated data that tests and the Unity Editor read" },
    @{ Pattern = '^src/Shared/GameProtocol/Schema~/'
        Checks = @("verify-protocol", "test:Mmorpg.Tools.Tests", "unity"); Reason = "the realtime protocol schema" },
    @{ Pattern = '^src/Shared/'
        Checks = @("owner", "unity"); Reason = "a shared Unity package, partly linked into .NET projects" },
    @{ Pattern = '^src/Client/Packages/'
        Checks = @("unity", "deps"); Reason = "the Unity packages" },
    @{ Pattern = '^src/Client/Assets/Art/'
        Checks = @("unity", "art"); Reason = "exported art" },
    @{ Pattern = '^src/Client/'
        Checks = @("owner", "unity"); Reason = "the Unity client" },
    @{ Pattern = '^art/'
        Checks = @("art"); Reason = "art sources and approvals" },
    @{ Pattern = '^tools/(blender/.+|[^/]*Art[^/]*\.ps1|Sync-[^/]+\.ps1)$'
        Checks = @("art"); Reason = "the art pipeline" },
    @{ Pattern = '^tools/Verify-DependencyPolicy\.ps1$'
        Checks = @("deps"); Reason = "the dependency policy" },
    @{ Pattern = '^tools/Run-Unity(Editor)?Tests\.ps1$'
        Checks = @("unity"); Reason = "a Unity test runner" },
    @{ Pattern = '^tools/[^/]+\.ps1$'
        Checks = @("none"); Reason = "a tool script" },
    @{ Pattern = '^tools/(ContentPipeline|ItemCatalogCompiler|WorldActorCompiler)/'
        Checks = @("owner!", "unity"); Reason = "a content compiler that the Unity Editor runs" },
    @{ Pattern = '^(src/Server|tools|tests)/'
        Checks = @("owner!"); Reason = "a .NET project" }
)

$verifiers = @(
    @{ Name = "item catalog"; Tool = "Mmorpg.Tools.ItemCatalogCompiler"; Token = "verify-content" },
    @{ Name = "world collision"; Tool = "Mmorpg.Tools.WorldCollisionCompiler"; Token = "verify-content" },
    @{ Name = "world actors"; Tool = "Mmorpg.Tools.WorldActorCompiler"; Token = "verify-content" },
    @{ Name = "realtime protocol"; Tool = "Mmorpg.Tools.ProtocolGenerator"; Token = "verify-protocol" }
)

# Windows PowerShell 5.1 turns native stderr into errors under "Stop" once it is redirected, so
# native commands run with errors suspended and their exit code decides. Output streams to the
# console.
function Invoke-Native([string]$FilePath, [string[]]$ArgumentList) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $FilePath @ArgumentList | Out-Host
        $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

# Returns the stdout lines of a native command and its exit code; stderr reaches the console.
function Get-NativeLines([string]$FilePath, [string[]]$ArgumentList) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $lines = @(& $FilePath @ArgumentList)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    [pscustomobject]@{ ExitCode = $exitCode; Lines = @($lines | Where-Object { $_ } | ForEach-Object { "$_" }) }
}

function Get-GitLines([string[]]$ArgumentList) {
    $result = Get-NativeLines "git" (@("-c", "core.quotepath=off") + $ArgumentList)
    if ($result.ExitCode -ne 0) {
        throw "git $($ArgumentList -join ' ') failed with exit code $($result.ExitCode)."
    }
    $result.Lines
}

function Resolve-RepositoryPath([string]$Directory, [string]$RelativePath) {
    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($part in (("$Directory/" + $RelativePath.Replace('\', '/')) -split '/')) {
        if ($part -eq "" -or $part -eq ".") { continue }
        if ($part -eq "..") {
            if ($parts.Count -gt 0) { $parts.RemoveAt($parts.Count - 1) }
            continue
        }
        $parts.Add($part)
    }
    $parts -join '/'
}

# A linked item with a wildcard covers the folder before its first wildcard segment.
function Get-LinkScope([string]$Path) {
    $fixed = New-Object System.Collections.Generic.List[string]
    foreach ($segment in ($Path -split '/')) {
        if ($segment -match '[*?]') {
            return (($fixed -join '/') + '/')
        }
        $fixed.Add($segment)
    }
    $Path
}

function Get-Projects {
    $projects = @{}
    foreach ($path in (Get-GitLines @("ls-files", "*.csproj"))) {
        $name = [IO.Path]::GetFileNameWithoutExtension($path)
        $directory = $path.Substring(0, $path.LastIndexOf('/'))
        [xml]$xml = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot $path)
        $references = @($xml.SelectNodes("//ProjectReference") |
            ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Include.Replace('\', '/')) })
        # Imported MSBuild files shape the build like the project file itself; imports through
        # MSBuild properties are the shared props that the full gate covers.
        $codeLinks = @(@($xml.SelectNodes("//Compile[@Include]") |
                ForEach-Object { Get-LinkScope (Resolve-RepositoryPath $directory $_.Include) }) +
            @($xml.SelectNodes("//Import[@Project]") | Where-Object { $_.Project -notmatch '\$\(' } |
                ForEach-Object { Resolve-RepositoryPath $directory $_.Project }))
        $dataLinks = @($xml.SelectNodes("//Content[@Include] | //None[@Include] | //EmbeddedResource[@Include]") |
            ForEach-Object { Get-LinkScope (Resolve-RepositoryPath $directory $_.Include) })
        $projects[$name] = [pscustomobject]@{
            Name = $name
            Path = $path
            Directory = "$directory/"
            References = $references
            CodeLinks = $codeLinks
            DataLinks = $dataLinks
            IsTest = $name.EndsWith(".Tests")
        }
    }
    $projects
}

function Test-InScope([string]$Path, [string]$Scope) {
    if ($Scope.EndsWith('/')) { return $Path.StartsWith($Scope, [StringComparison]::Ordinal) }
    [string]::Equals($Path, $Scope, [StringComparison]::Ordinal)
}

# The projects that contain a file: Code when they compile it, Data when they only copy it.
function Get-Owners($Projects, [string]$Path) {
    $code = New-Object System.Collections.Generic.List[string]
    $data = New-Object System.Collections.Generic.List[string]
    foreach ($project in $Projects.Values) {
        if ($Path.StartsWith($project.Directory, [StringComparison]::Ordinal) -or
            @($project.CodeLinks | Where-Object { Test-InScope $Path $_ }).Count -gt 0) {
            $code.Add($project.Name)
        }
        elseif (@($project.DataLinks | Where-Object { Test-InScope $Path $_ }).Count -gt 0) {
            $data.Add($project.Name)
        }
    }
    [pscustomobject]@{ Code = @($code); Data = @($data) }
}

function Find-Rule([string]$Path) {
    foreach ($rule in $rules) {
        if ($Path -match $rule.Pattern) {
            return $rule
        }
    }
    $null
}

# Every project that references one of the given projects, directly or through others.
function Get-Dependents($Projects, [string[]]$Names) {
    $selected = New-Object 'System.Collections.Generic.HashSet[string]'
    $queue = New-Object System.Collections.Generic.Queue[string]
    foreach ($name in $Names) {
        if ($selected.Add($name)) { $queue.Enqueue($name) }
    }
    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()
        foreach ($project in $Projects.Values) {
            if ($project.References -contains $current -and $selected.Add($project.Name)) {
                $queue.Enqueue($project.Name)
            }
        }
    }
    @($selected)
}

function Get-Selection($Projects, [string[]]$Files) {
    $selection = [pscustomobject]@{
        Full = [bool]$All
        Lines = New-Object System.Collections.Generic.List[string]
        Tokens = New-Object 'System.Collections.Generic.HashSet[string]'
        Owners = New-Object 'System.Collections.Generic.HashSet[string]'
        FormatFiles = New-Object System.Collections.Generic.List[string]
        Scripts = New-Object System.Collections.Generic.List[string]
    }
    foreach ($file in $Files) {
        if ($file.EndsWith(".ps1") -and (Test-Path -LiteralPath (Join-Path $repositoryRoot $file))) {
            $selection.Scripts.Add($file)
        }
        $rule = Find-Rule $file
        if (-not $rule) {
            $selection.Full = $true
            $selection.Lines.Add("  $file -> full gate (no rule matches this file)")
            continue
        }
        $checks = New-Object System.Collections.Generic.List[string]
        foreach ($token in $rule.Checks) {
            if ($token -eq "full") {
                $selection.Full = $true
            }
            if ($token -notlike "owner*") {
                $null = $selection.Tokens.Add($token)
                $checks.Add($token)
                continue
            }
            $owners = Get-Owners $Projects $file
            $names = @($owners.Code) + @($owners.Data)
            if ($names.Count -eq 0) {
                if ($token -eq "owner!") {
                    $selection.Full = $true
                    $checks.Add("full (no project contains it)")
                }
                continue
            }
            foreach ($name in $names) { $null = $selection.Owners.Add($name) }
            $checks.Add("projects " + ($names -join ", "))
            $extension = [IO.Path]::GetExtension($file).ToLowerInvariant()
            if ($extension -eq ".cs" -and @($owners.Code).Count -gt 0) {
                if (Test-Path -LiteralPath (Join-Path $repositoryRoot $file)) { $selection.FormatFiles.Add($file) }
            }
            elseif ($extension -in @(".csproj", ".props", ".targets") -or $file.EndsWith("packages.lock.json")) {
                $null = $selection.Tokens.Add("deps")
                $checks.Add("deps")
            }
            elseif (@($owners.Code | Where-Object { -not $Projects[$_].IsTest -and $file.StartsWith($Projects[$_].Directory, [StringComparison]::Ordinal) }).Count -gt 0) {
                # Tests read the configuration files of other projects, such as appsettings.json.
                $null = $selection.Tokens.Add("all-tests")
                $checks.Add("all-tests")
            }
        }
        if ($checks.Count -eq 0) { $checks.Add("nothing") }
        $selection.Lines.Add("  $file -> $($checks -join '; ') ($($rule.Reason))")
    }
    $selection
}

# Every tracked file must match a rule, a rule that needs a containing project must find one, and
# every project a rule or verifier names must exist.
function Test-Rules($Projects) {
    $problems = New-Object System.Collections.Generic.List[string]
    $tracked = @(Get-GitLines @("ls-files"))
    foreach ($file in $tracked) {
        $rule = Find-Rule $file
        if (-not $rule) {
            $problems.Add("no rule matches $file")
        }
        elseif ($rule.Checks -contains "owner!") {
            $owners = Get-Owners $Projects $file
            if (@($owners.Code).Count + @($owners.Data).Count -eq 0) {
                $problems.Add("no .NET project contains $file, so a change to it would run the full gate; give it a rule")
            }
        }
    }
    foreach ($rule in $rules) {
        foreach ($token in @($rule.Checks | Where-Object { $_ -like "test:*" })) {
            if (-not $Projects.ContainsKey($token.Substring(5))) { $problems.Add("rule $($rule.Pattern) names unknown project $($token.Substring(5))") }
        }
    }
    foreach ($verifier in $verifiers) {
        if (-not $Projects.ContainsKey($verifier.Tool)) { $problems.Add("verifier $($verifier.Name) names unknown project $($verifier.Tool)") }
    }
    if ($problems.Count -gt 0) {
        $problems | ForEach-Object { Write-Host "  $_" }
        Write-Host "  $($problems.Count) problems in the test selection rules of tools/Test-Changes.ps1"
        return $false
    }
    Write-Host "  Every one of $($tracked.Count) tracked files matches a test selection rule."
    $true
}

function New-SolutionFilter($Projects, [string[]]$Names, [string]$Label) {
    $path = Join-Path ([IO.Path]::GetTempPath()) "mmorpg-test-changes-$PID-$Label.slnf"
    $content = [ordered]@{
        solution = [ordered]@{
            path = (Join-Path $repositoryRoot "Mmorpg.slnx")
            projects = @($Names | Sort-Object | ForEach-Object { $Projects[$_].Path.Replace('/', '\') })
        }
    }
    [IO.File]::WriteAllText($path, ($content | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding $false))
    $path
}

function Find-EnvFile {
    $local = Join-Path $repositoryRoot ".env"
    if (Test-Path -LiteralPath $local) { return $local }
    # A worktree has no .env of its own; the main checkout holds it.
    $commonDirectory = @(Get-GitLines @("rev-parse", "--path-format=absolute", "--git-common-dir"))[0]
    $main = Join-Path (Split-Path -Parent $commonDirectory) ".env"
    if (Test-Path -LiteralPath $main) { return $main }
    $null
}

function Read-EnvValue([string]$EnvFile, [string]$Name) {
    foreach ($line in (Get-Content -LiteralPath $EnvFile)) {
        if ($line -match "^\s*$Name\s*=(.*)$") { return $Matches[1].Trim() }
    }
    $null
}

# Points the tests at PostgreSQL and Redis. Values that are already set are used as they are;
# otherwise they come from .env, and the test containers start unless they run already. Returns
# what Restore-TestServices needs to undo.
function Initialize-TestServices {
    $state = [pscustomobject]@{ Variables = @{}; EnvFile = $null; Started = $false }
    $names = @("MMORPG_TEST_POSTGRES", "MMORPG_TEST_REDIS")
    $missing = @($names | Where-Object { [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_)) })
    if ($missing.Count -eq 0) { return $state }

    $envFile = Find-EnvFile
    if (-not $envFile) {
        throw "$($missing -join ' and ') are not set and no .env file was found. Set them as README.md shows, or pass -SkipIntegration."
    }
    foreach ($name in $missing) {
        $value = Read-EnvValue $envFile $name
        if ([string]::IsNullOrWhiteSpace($value)) {
            throw "$name is neither set nor in $envFile. Add it as .env.example shows, or pass -SkipIntegration."
        }
        $state.Variables[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $value)
    }
    if ("$env:MMORPG_TEST_POSTGRES" -match '(?i)Host\s*=\s*localhost') {
        Write-Host "Warning: MMORPG_TEST_POSTGRES uses Host=localhost; with Host=127.0.0.1 every new connection is about two seconds faster."
    }

    $state.EnvFile = $envFile
    $compose = @("compose", "--env-file", $envFile, "-f", (Join-Path $repositoryRoot "docker-compose.test.yml"))
    $running = Get-NativeLines "docker" ($compose + @("ps", "--status", "running", "--services"))
    if ($running.ExitCode -ne 0) {
        throw "Docker is not available for the test containers. Start Docker Desktop, or pass -SkipIntegration."
    }
    if (-not ($running.Lines -contains "postgres" -and $running.Lines -contains "redis")) {
        Write-Host "Starting the PostgreSQL and Redis test containers."
        if ((Invoke-Native "docker" ($compose + @("up", "-d", "--wait"))) -ne 0) {
            throw "The test containers did not start."
        }
        $state.Started = $true
    }
    $state
}

function Restore-TestServices($State) {
    if (-not $State) { return }
    if ($State.Started) {
        Write-Host "Stopping the test containers."
        $null = Invoke-Native "docker" @("compose", "--env-file", $State.EnvFile, "-f", (Join-Path $repositoryRoot "docker-compose.test.yml"), "down")
    }
    foreach ($name in $State.Variables.Keys) {
        [Environment]::SetEnvironmentVariable($name, $State.Variables[$name])
    }
}

# Runs a script of this folder in a PowerShell process of its own, as CI does, so its exit code,
# working directory, and errors stay apart from this script.
function Invoke-Script([string]$Name, [string[]]$ArgumentList = @()) {
    $shell = (Get-Process -Id $PID).Path
    Invoke-Native $shell (@("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $PSScriptRoot $Name)) + $ArgumentList)
}

$results = New-Object System.Collections.Generic.List[object]
# Runs one check; the last value its action returns must be $true for the check to pass.
function Invoke-Check([string]$Name, [scriptblock]$Action) {
    Write-Host ""
    Write-Host "== $Name"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $passed = $false
    try {
        $output = @(& $Action)
        $passed = $output.Count -gt 0 -and $output[$output.Count - 1] -eq $true
    }
    catch {
        Write-Host "  $($_.Exception.Message)"
    }
    $results.Add([pscustomobject]@{ Name = $Name; Passed = $passed; Seconds = $watch.Elapsed.TotalSeconds })
    $passed
}

function Skip-Check([string]$Name, [string]$Reason) {
    $results.Add([pscustomobject]@{ Name = $Name; Passed = $null; Seconds = 0; Reason = $Reason })
}

Push-Location $repositoryRoot
$temporaryFiles = New-Object System.Collections.Generic.List[string]
$services = $null
try {
    try {
        $projects = Get-Projects
    }
    catch {
        Write-Host "Result: the project files could not be read: $($_.Exception.Message)"
        exit 2
    }

    if ($VerifyRules) {
        if (Test-Rules $projects) {
            Write-Host "Result: the test selection rules cover every tracked file"
            exit 0
        }
        Write-Host "Result: fix the test selection rules in tools/Test-Changes.ps1"
        exit 1
    }

    $paths = @($Path | ForEach-Object { "$_".Split(",") } | ForEach-Object { $_.Trim().Replace('\', '/') -replace '^\./', '' } | Where-Object { $_ })
    if ($paths.Count -gt 0) {
        $files = @($paths | Sort-Object -Unique)
        $source = "Given paths"
    }
    else {
        try {
            $mergeBase = @(Get-GitLines @("merge-base", "HEAD", $Base))[0]
            $files = @(@(Get-GitLines @("diff", "--name-only", "--no-renames", $mergeBase)) +
                @(Get-GitLines @("ls-files", "--others", "--exclude-standard")) | Sort-Object -Unique)
        }
        catch {
            Write-Host "Result: the changes against $Base could not be read: $($_.Exception.Message)"
            exit 2
        }
        $source = "Changes against $Base since $($mergeBase.Substring(0, 7))"
    }

    $selection = Get-Selection $projects $files
    if ($All) {
        Write-Host "Running the full quality gate (-All)."
    }
    elseif ($files.Count -eq 0) {
        Write-Host "Result: nothing changed against $Base, so there is nothing to check"
        exit 0
    }
    else {
        Write-Host "${source}: $($files.Count) files"
        $selection.Lines | ForEach-Object { Write-Host $_ }
    }

    $full = $selection.Full
    $tokens = $selection.Tokens
    $affected = @(Get-Dependents $projects @($selection.Owners))
    $testNames = @(if ($full -or $tokens.Contains("all-tests")) {
            $projects.Values | Where-Object { $_.IsTest } | ForEach-Object { $_.Name } | Sort-Object
        }
        else {
            @($affected | Where-Object { $projects[$_].IsTest }) +
            @($tokens | Where-Object { $_ -like "test:*" } | ForEach-Object { $_.Substring(5) }) | Sort-Object -Unique
        })
    $selectedVerifiers = @($verifiers | Where-Object { $full -or $tokens.Contains($_.Token) -or $affected -contains $_.Tool })
    # The test projects with PostgreSQL or Redis tests, which need the test services.
    $integrationProjects = @(Get-GitLines @("grep", "-l", "-E", "\[(PostgresIntegrationFact|RedisIntegrationFact)", "--", "tests") |
        ForEach-Object { $file = $_; $projects.Values | Where-Object { $_.IsTest -and $file.StartsWith($_.Directory, [StringComparison]::Ordinal) } } |
        ForEach-Object { $_.Name } | Sort-Object -Unique)
    $buildNames = @(@($affected) + @($testNames) + @($selectedVerifiers | ForEach-Object { $_.Tool }) | Sort-Object -Unique)
    $runUnity = $full -or $tokens.Contains("unity")
    $runDotNet = $full -or $buildNames.Count -gt 0
    $scripts = @(@(if ($full) { Get-GitLines @("ls-files", "*.ps1") }) + @($selection.Scripts) | Sort-Object -Unique)

    Write-Host ""
    Write-Host "Selected checks:"
    if ($full) { Write-Host "  the full quality gate, including the test selection rules" }
    if ($full) { Write-Host "  PowerShell syntax of every script" }
    elseif ($scripts.Count -gt 0) { Write-Host "  PowerShell syntax of the changed scripts: $($scripts -join ', ')" }
    if ($full -or $tokens.Contains("deps")) { Write-Host "  dependency policy" }
    if ($full -or $tokens.Contains("art")) { Write-Host "  art assets" }
    if ($full -or $tokens.Contains("observability")) { Write-Host "  observability configuration" }
    if ($runDotNet) {
        $buildScope = if ($full) { "the solution" } else { "projects: $($buildNames -join ', ')" }
        Write-Host "  restore and build of $buildScope"
        if ($full) { Write-Host "  format of the solution" }
        elseif ($selection.FormatFiles.Count -gt 0) { Write-Host "  format of the changed C# files: $($selection.FormatFiles -join ', ')" }
    }
    foreach ($verifier in $selectedVerifiers) { Write-Host "  $($verifier.Name) verifier" }
    $integrationNames = @($testNames | Where-Object { $integrationProjects -contains $_ })
    if ($testNames.Count -gt 0) {
        Write-Host "  .NET tests: $($testNames -join ', ')"
        if ($integrationNames.Count -gt 0) {
            $scope = if ($SkipIntegration) { "skipped by -SkipIntegration; CI runs them" } else { "with PostgreSQL and Redis" }
            Write-Host "  integration tests in $($integrationNames -join ', ') ($scope)"
        }
    }
    if ($runUnity) {
        $unityScope = if ($SkipUnity) { " (skipped by -SkipUnity; CI runs them)" } else { "" }
        Write-Host "  Unity EditMode and PlayMode tests$unityScope"
    }
    if (-not ($full -or $scripts.Count -gt 0 -or $runDotNet -or $runUnity -or $tokens.Contains("deps") -or $tokens.Contains("art") -or $tokens.Contains("observability"))) {
        Write-Host "  none; no change can affect a check"
    }
    if ($Plan) {
        exit 0
    }

    if ($scripts.Count -gt 0) {
        $null = Invoke-Check "PowerShell syntax" {
            $broken = 0
            foreach ($scriptPath in $scripts) {
                $parsedTokens = $null
                $errors = $null
                $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repositoryRoot $scriptPath), [ref]$parsedTokens, [ref]$errors)
                foreach ($parseError in @($errors)) {
                    Write-Host "  ${scriptPath}:$($parseError.Extent.StartLineNumber): $($parseError.Message)"
                    $broken++
                }
            }
            $broken -eq 0
        }
    }
    if ($full) {
        $null = Invoke-Check "Test selection rules" { Test-Rules $projects }
    }
    if ($full -or $tokens.Contains("deps")) {
        $null = Invoke-Check "Dependency policy" { (Invoke-Script "Verify-DependencyPolicy.ps1") -eq 0 }
    }
    if ($full -or $tokens.Contains("art")) {
        $null = Invoke-Check "Art assets" { (Invoke-Script "Verify-ArtAssets.ps1") -eq 0 }
    }
    if ($full -or $tokens.Contains("observability")) {
        $null = Invoke-Check "Observability configuration" { (Invoke-Script "Verify-Observability.ps1") -eq 0 }
    }

    if ($runDotNet) {
        $buildTarget = if ($full) { Join-Path $repositoryRoot "Mmorpg.slnx" } else { New-SolutionFilter $projects $buildNames "build" }
        if (-not $full) { $temporaryFiles.Add($buildTarget) }
        $built = Invoke-Check "Restore" { (Invoke-Native "dotnet" @("restore", $buildTarget, "--locked-mode")) -eq 0 }
        if ($built) {
            if ($full -or $selection.FormatFiles.Count -gt 0) {
                $null = Invoke-Check "Format" {
                    $arguments = @("format", $buildTarget, "--verify-no-changes", "--no-restore")
                    if (-not $full) { $arguments += @("--include") + @($selection.FormatFiles) }
                    (Invoke-Native "dotnet" $arguments) -eq 0
                }
            }
            $built = Invoke-Check "Build" { (Invoke-Native "dotnet" @("build", $buildTarget, "--configuration", "Release", "--no-restore")) -eq 0 }
        }
        foreach ($verifier in $selectedVerifiers) {
            $name = "Verify $($verifier.name)"
            if (-not $built) { Skip-Check $name "the build failed"; continue }
            $projectPath = $projects[$verifier.Tool].Path
            $null = Invoke-Check $name { (Invoke-Native "dotnet" @("run", "--project", $projectPath, "--configuration", "Release", "--no-build", "--", "--verify")) -eq 0 }
        }
        if ($testNames.Count -gt 0) {
            if (-not $built) {
                Skip-Check ".NET tests" "the build failed"
            }
            else {
                $null = Invoke-Check ".NET tests" {
                    if (-not $SkipIntegration -and $integrationNames.Count -gt 0) { $script:services = Initialize-TestServices }
                    $testTarget = New-SolutionFilter $projects $testNames "tests"
                    $temporaryFiles.Add($testTarget)
                    foreach ($name in $testNames) {
                        Remove-Item -LiteralPath (Join-Path $repositoryRoot "TestResults\$name\test-results.trx") -ErrorAction SilentlyContinue
                    }
                    $arguments = @("test", $testTarget, "--configuration", "Release", "--no-build", "--logger", "trx;LogFileName=test-results.trx")
                    if ($SkipIntegration) { $arguments += @("--filter", "Category!=Integration") }
                    $passed = (Invoke-Native "dotnet" $arguments) -eq 0
                    # A skipped integration test means a missing service, so it fails like in CI.
                    foreach ($name in $testNames) {
                        if ((Invoke-Script "Verify-TestResults.ps1" @("-ResultsDirectory", (Join-Path $repositoryRoot "TestResults\$name"))) -ne 0) {
                            $passed = $false
                        }
                    }
                    $passed
                }
            }
        }
    }

    if ($runUnity -and -not $SkipUnity) {
        $null = Invoke-Check "Unity tests" {
            $exitCode = Invoke-Script "Run-UnityEditorTests.ps1"
            if ($exitCode -eq 3) {
                Write-Host "  No Editor has this checkout open, so the suites run headless; a checkout without a Library imports it first."
                $exitCode = Invoke-Script "Run-UnityTests.ps1"
            }
            $exitCode -eq 0
        }
    }
}
finally {
    Restore-TestServices $services
    foreach ($file in $temporaryFiles) { Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
    Pop-Location
}

if ($results.Count -eq 0) {
    Write-Host "Result: no check is needed for these changes"
    exit 0
}
Write-Host ""
Write-Host "Summary:"
foreach ($result in $results) {
    $state = if ($null -eq $result.Passed) { "skipped" } elseif ($result.Passed) { "passed" } else { "FAILED" }
    $detail = if ($null -eq $result.Passed) { "($($result.Reason))" } else { "{0,5:N0} s" -f $result.Seconds }
    Write-Host ("  {0,-7} {1,-30} {2}" -f $state, $result.Name, $detail)
}
$failedChecks = @($results | Where-Object { $_.Passed -ne $true })
if ($failedChecks.Count -gt 0) {
    Write-Host "Result: $($failedChecks.Count) of $($results.Count) checks failed or did not run"
    exit 1
}
Write-Host "Result: every one of $($results.Count) selected checks passed"
exit 0
