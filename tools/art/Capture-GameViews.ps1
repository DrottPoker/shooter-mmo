# Captures the game views from the game itself: the outfits of tools/art/views/standard-set.json on the
# bodies, in the poses and from the cameras the set names, drawn by the Unity client in Play Mode in the
# Editor, logged in against the local stack and standing in Development World 2, with the game's own
# meshes, shaders, light, and cloth. -All captures every view, -Outfit, -Body, -Pose, and -View, whole view
# names, choose some, as for tools/art/Render-ArtViews.ps1. It starts the local stack when it does not
# run, uses the Unity Editor that has this checkout's project open or opens one, enters Play Mode from
# LoginMenu, signs in to the capture's own account, capture_views, which the first run registers, with
# its character and enters the World without a click,
# runs Mmorpg.Client.Editor.GameViewCapture for the chosen views, leaves Play Mode, and stops what it
# started. It writes artifacts/art-views/game/<view>.png, one sheet per outfit and body,
# artifacts/art-views/game/<Outfit>_<Body>.png, with each view's Blender look beside the game's picture,
# and capture.json with what the capture measured, and lists the views whose silhouettes overlap the
# Blender view's less than -Overlap and the poses whose bone heads lie farther from the Blender view's
# than -BoneBudget millimeters on average. -AfterCapture names a C# file that eval_file runs in the same Play
# Mode once the views are captured, such as pictures of one's own with GameViewCapture.Picture. A
# worktree without .env gets a copy of the main checkout's for the run, which the script deletes again.
# Nothing is committed from a capture. See docs/ART_PIPELINE.md, Capturing The Views From The Game.
#
# Exit codes: 0 every chosen view was captured, 1 an error, or a view could not be captured.
param(
    [switch]$All,
    [string[]]$Outfit = @(),
    [string[]]$Body = @(),
    [string[]]$Pose = @(),
    [string[]]$View = @(),
    # A C# file that eval_file runs in the Play Mode of the capture once the views are captured.
    [string]$AfterCapture,
    # The overlap below which a view is listed.
    [double]$Overlap = 0.85,
    # The mean distance of a pose's bone heads from the Blender view's, in millimeters, above which the
    # pose is listed.
    [double]$BoneBudget = 10.0,
    # The longest a pose waits for its cloth to sleep, in seconds of play.
    [double]$SettleSeconds = 3.0,
    # The longest each wait takes: the stack, the Editor, Play Mode, a step of the capture.
    [int]$TimeoutMinutes = 10,
    [string]$UnityCliPath = $env:UNITY_CLI_PATH
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")
. (Join-Path $PSScriptRoot "ArtViews.ps1")
$StackStepPrefix = "game-views"
. (Join-Path $PSScriptRoot "../LocalStackCommon.ps1")
. (Join-Path $PSScriptRoot "../UnityCommon.ps1")

$repositoryRoot = $script:ArtRepositoryRoot
$projectPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "src\Client")).TrimEnd('\', '/')
$blenderFolder = Join-Path $repositoryRoot "artifacts\art-views"
$outputFolder = Join-Path $blenderFolder "game"
$runFolder = Join-Path ([IO.Path]::GetTempPath()) ("shooter-mmo-game-views\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$gameServiceUrl = "http://localhost:5000"
$gameServicePort = 5000
$shardId = "local-shard-1"
$udpPort = 27015
$loginScene = "Assets/Scenes/LoginMenu.unity"
$gameServiceDll = Join-Path $repositoryRoot "src\Server\GameService\bin\Release\net10.0\Mmorpg.GameService.dll"
$workerDll = Join-Path $repositoryRoot "src\Server\SimulationWorker\bin\Release\net10.0\Mmorpg.SimulationWorker.dll"
# A capture that writes nothing for this long has stopped, and an eval request that a domain reload
# dropped is sent again for this long.
$stallSeconds = 180
$retrySeconds = 90
$culture = [Globalization.CultureInfo]::InvariantCulture

function Get-Deadline {
    return [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
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

function Get-EditorState {
    return (Invoke-EditorCommand "editor_status").Json
}

function Test-EditorRunning {
    # An Editor that is busy, as during a domain reload, still counts as running.
    $status = Invoke-EditorCommand "editor_status"
    return $status.ExitCode -eq 0 -or "$($status.Text)" -notmatch "EDITOR_NOT_RUNNING|NOT_A_PROJECT"
}

function Wait-EditorState([scriptblock]$Condition, [string]$What) {
    # Waits until the Editor answers in a state that holds the condition; a request during a domain
    # reload fails and counts as not yet.
    $deadline = Get-Deadline
    $last = $null
    while ($true) {
        $state = Get-EditorState
        if ($state -and -not $state.compiling -and -not $state.domainReloadInProgress -and (& $Condition $state)) {
            return $state
        }

        $last = if ($state) { "status $($state.status), compiling $($state.compiling), play mode $($state.playMode)" } else { "no answer" }
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "The Editor did not reach $What within $TimeoutMinutes minutes; last state: $last. A modal dialog in the Editor blocks it."
        }

        Start-Sleep -Seconds 2
    }
}

function Write-EvalFile([string]$Name, [string]$Code) {
    # The code of one eval_file request, in the run folder; eval_file runs it as a method body.
    $path = Join-Path $runFolder "$Name.cs"
    [IO.File]::WriteAllText($path, $Code, (New-Object Text.UTF8Encoding($false)))
    return $path
}

function Invoke-EditorCode([string]$Name, [string]$Code, [switch]$Retry) {
    # Runs C# in the Editor through eval_file and returns its result. With -Retry a request that a
    # domain reload or the start of Play Mode dropped is sent again for up to $retrySeconds.
    $path = Write-EvalFile $Name $Code
    $deadline = [DateTime]::UtcNow.AddSeconds($retrySeconds)
    while ($true) {
        $answer = Invoke-EditorCommand "eval_file" @("--file", $path)
        if ($answer.Json -and $answer.Json.success) {
            return "$($answer.Json.result)"
        }

        if (-not $Retry -or [DateTime]::UtcNow -ge $deadline) {
            throw "The Editor could not run $Name`: $("$($answer.Text)".Trim())"
        }

        Start-Sleep -Seconds 2
    }
}

function Get-MainCheckout {
    # The main checkout of the repository, the parent of Git's common folder, or $null without Git.
    try {
        $common = @(& git -C $repositoryRoot rev-parse --path-format=absolute --git-common-dir 2>$null) | Select-Object -First 1
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($common)) {
            return $null
        }

        return [IO.Path]::GetFullPath((Split-Path -Parent $common)).TrimEnd('\', '/')
    }
    catch {
        return $null
    }
}

function Use-LocalStack {
    # Uses the local stack when GameService answers and lists the default shard online, and starts what
    # does not run otherwise: the containers, GameService, and the default worker, from Release builds.
    if ((Test-HttpReady "$gameServiceUrl/health/ready") -and (Test-ShardOnline $gameServiceUrl $shardId)) {
        Write-Step "Using the local stack that runs: GameService and $shardId."
        return
    }

    if (Get-TcpListener $gameServicePort) {
        throw "A process listens on port $gameServicePort, but no GameService there lists $shardId online; start the default worker, or stop that process, then capture again."
    }

    if (Get-UdpListener $udpPort) {
        throw "A process uses UDP port $udpPort while GameService does not run; stop it, then capture again."
    }

    # A worktree has no .env: a copy of the main checkout's serves the run, and Compose uses the main
    # checkout's containers under its project name.
    $main = Get-MainCheckout
    $envPath = Join-Path $repositoryRoot ".env"
    if (-not (Test-Path -LiteralPath $envPath)) {
        $mainEnv = if ($main) { Join-Path $main ".env" } else { $null }
        if ($null -eq $mainEnv -or $main -eq $repositoryRoot -or -not (Test-Path -LiteralPath $mainEnv)) {
            throw "The repository has no .env file; create it from .env.example as docs/LOCAL_DEVELOPMENT.md describes."
        }

        Copy-Item -LiteralPath $mainEnv -Destination $envPath
        $script:envCopy = $envPath
        Write-Step "Copied the main checkout's .env into this worktree for the run."
    }

    if ($main -and $main -ne $repositoryRoot -and [string]::IsNullOrWhiteSpace($env:COMPOSE_PROJECT_NAME)) {
        $env:COMPOSE_PROJECT_NAME = (Split-Path -Leaf $main).ToLowerInvariant()
        $script:composeNameSet = $true
    }

    $composeServices = Get-NativeLines "docker" @("compose", "config", "--services")
    $runningServices = Get-NativeLines "docker" @("compose", "ps", "--status", "running", "--services")
    if ($composeServices.ExitCode -ne 0 -or $runningServices.ExitCode -ne 0) {
        throw "Docker Compose cannot read docker-compose.yml or list its containers; is Docker running?"
    }

    $script:containersToStop = @($composeServices.Lines | Where-Object { $runningServices.Lines -notcontains $_ })
    if ((Invoke-Logged "docker" @("compose", "up", "-d", "--wait") (Join-Path $runFolder "docker-compose-up.log")) -ne 0) {
        Write-LogTail (Join-Path $runFolder "docker-compose-up.log")
        throw "docker compose up failed."
    }

    # The builds leave no MSBuild node or compiler server running after them: node reuse is off in
    # their environment, and the build servers are off.
    Write-Step "Building GameService and SimulationWorker."
    $nodeReuse = [Environment]::GetEnvironmentVariable("MSBUILDDISABLENODEREUSE")
    [Environment]::SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1")
    try {
        foreach ($build in @("src\Server\GameService\Mmorpg.GameService.csproj", "src\Server\SimulationWorker\Mmorpg.SimulationWorker.csproj")) {
            $log = Join-Path $runFolder ("build-" + [IO.Path]::GetFileNameWithoutExtension($build) + ".log")
            if ((Invoke-Logged "dotnet" @("build", $build, "-c", "Release", "-nologo", "-v", "q", "--disable-build-servers", "-nodeReuse:false") $log) -ne 0) {
                Write-LogTail $log
                throw "Building $build failed."
            }
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", $nodeReuse)
    }

    Write-Step "Starting GameService."
    $script:gameService = Start-StackProcess "game-service" "dotnet" @("`"$gameServiceDll`"") @{
        ASPNETCORE_ENVIRONMENT = "Development"
        ASPNETCORE_URLS = $gameServiceUrl
    } $runFolder $repositoryRoot
    Wait-Until { Test-HttpReady "$gameServiceUrl/health/ready" } "GameService readiness" $script:gameService (Join-Path $runFolder "game-service.log") ($TimeoutMinutes * 60) $runFolder

    Write-Step "Starting the default worker."
    $script:worker = Start-StackProcess "simulation-worker" "dotnet" @("`"$workerDll`"") @{ DOTNET_ENVIRONMENT = "Development" } $runFolder $repositoryRoot
    $workerLog = Join-Path $runFolder "simulation-worker.log"
    Wait-Until { (Read-SharedText $workerLog) -match "Realtime transport listening on UDP port $udpPort" } "The worker's UDP listener" $script:worker $workerLog ($TimeoutMinutes * 60) $runFolder
    Wait-Until { Test-ShardOnline $gameServiceUrl $shardId } "The registration of $shardId" $script:worker $workerLog ($TimeoutMinutes * 60) $runFolder
}

function Stop-LocalStack {
    # Stops what the run started, the worker first, and the containers it started.
    Stop-StackProcess $script:worker "SimulationWorker"
    Stop-StackProcess $script:gameService "GameService"
    if ($script:containersToStop.Count -gt 0) {
        $nowRunning = Get-NativeLines "docker" @("compose", "ps", "--status", "running", "--services")
        $toStop = @($script:containersToStop | Where-Object { $nowRunning.Lines -contains $_ })
        if ($toStop.Count -gt 0) {
            $null = Invoke-Logged "docker" (@("compose", "stop") + $toStop) (Join-Path $runFolder "docker-compose-stop.log")
            Write-Step "Stopped the containers $($toStop -join ', ')."
        }
    }

    if ($script:composeNameSet) {
        $env:COMPOSE_PROJECT_NAME = $null
    }

    if ($null -ne $script:envCopy -and (Test-Path -LiteralPath $script:envCopy)) {
        Remove-Item -LiteralPath $script:envCopy -Force
        Write-Step "Deleted the copy of .env."
    }
}

function Open-Editor {
    # Uses the Editor that has the project open, or opens one and waits until it answers.
    if (Test-EditorRunning) {
        Write-Step "Using the Unity Editor that has $projectPath open."
        return
    }

    if (Test-UnityProjectOpen $projectPath) {
        throw "Another Unity process has $projectPath open and does not answer the Unity CLI; let it end, then capture again."
    }

    # The CLI's own wait for a ready Editor asks with a command the Pipeline package does not know,
    # which logs an error, so the run asks only this project's Editor for its state instead.
    Write-Step "Opening a Unity Editor on $projectPath."
    $opened = Invoke-UnityCli @("open", $projectPath, "--no-banner")
    $script:editorOpened = $true
    if ($opened.ExitCode -ne 0) {
        throw "The Unity Editor did not open: $("$($opened.Text)".Trim())"
    }

    $null = Wait-EditorState { param($s) $s.status -eq "ready" } "a ready state after it opened"
}

function Close-Editor {
    # Closes the Editor the run opened; the CLI's answer to an Editor that exits is no answer.
    $null = Invoke-EditorCommand "eval_file" @("--file", (Write-EvalFile "close" "UnityEditor.EditorApplication.Exit(0); return 0;"))
    $deadline = Get-Deadline
    while (Test-UnityProjectOpen $projectPath) {
        if ([DateTime]::UtcNow -ge $deadline) {
            Write-Step "The Editor the run opened did not close within $TimeoutMinutes minutes; close it by hand."
            return
        }

        Start-Sleep -Seconds 2
    }

    Write-Step "Closed the Unity Editor the run opened."
}

function Update-EditorScripts {
    # Imports changed files and compiles the scripts; scripts that do not compile stop the run.
    $null = Invoke-EditorCommand "eval" @("--code", "UnityEditor.AssetDatabase.Refresh(UnityEditor.ImportAssetOptions.ForceSynchronousImport); return 0;")
    $null = Wait-EditorState { param($s) $s.status -eq "ready" } "an idle state after the refresh"
    $failed = Invoke-EditorCode "compile-state" "return UnityEditor.EditorUtility.scriptCompilationFailed;" -Retry
    if ($failed -eq "True") {
        throw "The scripts do not compile in the Editor; fix them, then capture again."
    }
}

function Get-StatusFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    try {
        return [IO.File]::ReadAllText($Path) | ConvertFrom-Json
    }
    catch {
        # The capture writes it now.
        return $null
    }
}

function Wait-Capture([string]$StatusPath) {
    # Follows the capture's status until it is done or failed, with a limit on a capture that writes
    # nothing new.
    $lastWrite = [DateTime]::UtcNow
    $lastText = $null
    $phase = $null
    $bodies = 0
    while ($true) {
        Start-Sleep -Seconds 2
        $text = if (Test-Path -LiteralPath $StatusPath) { try { [IO.File]::ReadAllText($StatusPath) } catch { $lastText } } else { $null }
        if ($text -ne $lastText) {
            $lastText = $text
            $lastWrite = [DateTime]::UtcNow
        }

        $status = Get-StatusFile $StatusPath
        if ($null -ne $status) {
            if ($status.phase -ne $phase) {
                $phase = $status.phase
                Write-Step "Capture: $phase ($([int]$status.done) of $([int]$status.total) views)."
            }

            foreach ($done in @($status.bodies | Select-Object -Skip $bodies | Where-Object { [double]$_.seconds -gt 0 })) {
                $bodies++
                Write-Step ([string]::Format($culture, "{0} on {1}: {2:0.0} s, put together in {3:0.0} s{4}.", $done.outfit, $done.label, [double]$done.seconds,
                        [double]$done.buildSeconds, $(if ($done.missing) { "; does not draw $($done.missing)" } else { "" })))
            }

            if ($status.state -ne "running") {
                return $status
            }
        }

        if (([DateTime]::UtcNow - $lastWrite).TotalSeconds -gt $stallSeconds) {
            throw "The capture wrote nothing for $stallSeconds s in the phase $phase; see the Editor's console."
        }
    }
}

function Read-SheetBitmap([string]$Path) {
    # A picture read into memory, so no file stays open while a sheet is drawn.
    $stream = New-Object IO.MemoryStream (, [IO.File]::ReadAllBytes($Path))
    try {
        $source = New-Object Drawing.Bitmap $stream
        try {
            return New-Object Drawing.Bitmap $source
        }
        finally {
            $source.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Write-GameViewSheet([string]$Path, $Entries) {
    # One sheet of the views of an outfit on a body: per view the Blender look and the game's picture
    # side by side, half size, under a line with its pose, camera, and overlap; a view without its
    # Blender look says so and shows the game's picture alone.
    Add-Type -AssemblyName System.Drawing
    $cellWidth, $cellHeight, $labelHeight = 256, 384, 20
    $columns = [Math]::Min(6, $Entries.Count)
    $rows = [Math]::Ceiling($Entries.Count / $columns)
    $sheet = New-Object Drawing.Bitmap ([int]($columns * 2 * $cellWidth)), ([int]($rows * ($cellHeight + $labelHeight)))
    $graphics = [Drawing.Graphics]::FromImage($sheet)
    $font = New-Object Drawing.Font "Segoe UI", 9
    try {
        $graphics.Clear([Drawing.Color]::FromArgb(40, 40, 40))
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear
        for ($index = 0; $index -lt $Entries.Count; $index++) {
            $entry = $Entries[$index]
            $x = ($index % $columns) * 2 * $cellWidth
            $y = [Math]::Floor($index / $columns) * ($cellHeight + $labelHeight)
            $blender = Join-Path $blenderFolder "$($entry.Name).png"
            $has = Test-Path -LiteralPath $blender
            $measure = if ([double]$entry.Overlap -ge 0) { "overlap " + ([double]$entry.Overlap).ToString("0.000", $culture) } else { "no overlap" }
            if ($null -ne $entry.Bones) {
                $measure += [string]::Format($culture, "   bones {0:0.0}/{1:0} mm", [double]$entry.Bones.boneMean, [double]$entry.Bones.boneLargest)
            }

            $label = "$($entry.Pose) $($entry.Camera)   Blender | game   $measure" + $(if ($has) { "" } else { "   no Blender view" })
            $graphics.DrawString($label, $font, [Drawing.Brushes]::White, [float]($x + 4), [float]($y + 2))
            if ($has) {
                $look = Read-SheetBitmap $blender
                try {
                    $graphics.DrawImage($look, (New-Object Drawing.Rectangle ([int]$x), ([int]($y + $labelHeight)), $cellWidth, $cellHeight))
                }
                finally {
                    $look.Dispose()
                }
            }

            $game = Read-SheetBitmap $entry.Picture
            try {
                $graphics.DrawImage($game, (New-Object Drawing.Rectangle ([int]($x + $cellWidth)), ([int]($y + $labelHeight)), $cellWidth, $cellHeight))
            }
            finally {
                $game.Dispose()
            }
        }

        $temporary = Join-Path (Split-Path -Parent $Path) (".{0}.{1}.tmp.png" -f (Split-Path -Leaf $Path), $PID)
        $sheet.Save($temporary, [Drawing.Imaging.ImageFormat]::Png)
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    }
    finally {
        $font.Dispose()
        $graphics.Dispose()
        $sheet.Dispose()
    }
}

function Measure-GameViewBones($Views, $Status) {
    # Per pose of an outfit on a body, how far the bone heads of the Blender view lie from the game's, in
    # millimeters: the mean and the largest over every bone but the cloth chains, whose cloth the game's
    # springs move, and in a pose of the Action layer the fingers, which its mask leaves to Idle. The
    # Blender bones are the numbers tools/art/Render-ArtViews.ps1 copies beside the pictures, of any view
    # of the pose. A pose gets boneMean, boneLargest, and boneFarthest, or none without them.
    $byPose = @{}
    foreach ($view in $Views) {
        $key = "$($view.Outfit)|$($view.Label)|$($view.Pose.Name)"
        if (-not $byPose.ContainsKey($key)) {
            $byPose[$key] = New-Object Collections.Generic.List[object]
        }

        $byPose[$key].Add($view)
    }

    $skeletons = @{}
    foreach ($pose in @($Status.poses)) {
        $poseViews = $byPose["$($pose.outfit)|$($pose.label)|$($pose.pose)"]
        $body = @($Status.bodies | Where-Object { $_.outfit -ceq $pose.outfit -and $_.label -ceq $pose.label }) | Select-Object -First 1
        $names = @(if ($null -ne $body) { $body.bones })
        $positions = @($pose.bonePositions)
        if ($null -eq $poseViews -or $names.Count -eq 0 -or $positions.Count -ne 3 * $names.Count) {
            continue
        }

        $numbers = $null
        foreach ($view in $poseViews) {
            $path = Join-Path $blenderFolder "$($view.Name).json"
            $read = if (Test-Path -LiteralPath $path) { try { [IO.File]::ReadAllText($path) | ConvertFrom-Json } catch { $null } } else { $null }
            if ($null -ne $read -and $null -ne $read.bones) {
                $numbers = $read
                break
            }
        }

        if ($null -eq $numbers) {
            continue
        }

        $skeletonPath = $poseViews[0].Body.SkeletonPath
        if (-not $skeletons.ContainsKey($skeletonPath)) {
            $json = Get-Content -Raw -LiteralPath $skeletonPath | ConvertFrom-Json
            $chains = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
            $fingers = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
            foreach ($bone in @($json.bones)) {
                if ($null -ne $bone.chain) {
                    $null = $chains.Add([string]$bone.name)
                }

                if ([string]$bone.human -cmatch '^(Left|Right)(Thumb|Index|Middle|Ring|Little)') {
                    $null = $fingers.Add([string]$bone.name)
                }
            }

            $skeletons[$skeletonPath] = [pscustomobject]@{ Chains = $chains; Fingers = $fingers }
        }

        $skeleton = $skeletons[$skeletonPath]
        $action = ([string]$pose.shown).StartsWith("Action.")
        $sum, $count, $largest, $farthest = 0.0, 0, -1.0, ""
        for ($index = 0; $index -lt $names.Count; $index++) {
            $name = [string]$names[$index]
            $property = $numbers.bones.PSObject.Properties[$name]
            if ($skeleton.Chains.Contains($name) -or ($action -and $skeleton.Fingers.Contains($name)) -or $null -eq $property) {
                continue
            }

            $blender = @($property.Value)
            $dx = [double]$positions[3 * $index] - [double]$blender[0]
            $dy = [double]$positions[3 * $index + 1] - [double]$blender[1]
            $dz = [double]$positions[3 * $index + 2] - [double]$blender[2]
            $distance = [Math]::Sqrt($dx * $dx + $dy * $dy + $dz * $dz) * 1000.0
            $sum += $distance
            $count++
            if ($distance -gt $largest) {
                $largest, $farthest = $distance, $name
            }
        }

        if ($count -gt 0) {
            $pose | Add-Member -NotePropertyName boneMean -NotePropertyValue ($sum / $count) -Force
            $pose | Add-Member -NotePropertyName boneLargest -NotePropertyValue $largest -Force
            $pose | Add-Member -NotePropertyName boneFarthest -NotePropertyValue $farthest -Force
        }
    }
}

function Write-Seconds([string]$Phase, [Diagnostics.Stopwatch]$Watch) {
    $script:times.Add([pscustomobject]@{ Phase = $Phase; Seconds = $Watch.Elapsed.TotalSeconds })
    $Watch.Restart()
}

function Invoke-GameViewCapture {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $filtered = (Split-ArtViewList $Outfit).Count + (Split-ArtViewList $Body).Count + (Split-ArtViewList $Pose).Count + (Split-ArtViewList $View).Count -gt 0
    if (-not ($All -or $filtered)) {
        throw "Name -All, or choose views with -Outfit, -Body, -Pose, or -View."
    }

    if ($AfterCapture -and -not (Test-Path -LiteralPath $AfterCapture -PathType Leaf)) {
        throw "-AfterCapture names no file: $AfterCapture."
    }

    if ([string]::IsNullOrWhiteSpace($UnityCliPath)) {
        $command = Get-Command unity -ErrorAction SilentlyContinue
        $script:UnityCliPath = if ($command) { $command.Source } elseif ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA "Unity\bin\unity.exe" } else { "" }
    }

    if ([string]::IsNullOrWhiteSpace($UnityCliPath) -or -not (Test-Path -LiteralPath $UnityCliPath)) {
        throw "The Unity CLI was not found; install it as docs/LOCAL_DEVELOPMENT.md describes, or set UNITY_CLI_PATH."
    }

    $views = @(Select-ArtViews @(Read-ArtViewSet) $Outfit $Body $Pose $View)
    New-Item -ItemType Directory -Force -Path $runFolder, $outputFolder | Out-Null
    Write-Step "Capturing $($views.Count) view(s) from the game; the run's logs are in $runFolder."
    $jobViews = @($views | ForEach-Object {
            [ordered]@{
                name = $_.Name
                outfit = $_.Outfit
                model = Split-Path -Leaf $_.Body.RelativeDirectory
                label = $_.Label
                garments = @($_.Garments | ForEach-Object { $_.Texture.Name })
                paints = @($_.Named)
                pose = $_.Pose.Name
                clip = $(if ($null -ne $_.Pose.Clip) { $_.Pose.Clip } else { "" })
                frame = $(if ($null -ne $_.Pose.Frame) { [int]$_.Pose.Frame } else { 0 })
                yaw = $_.Camera.Yaw
                pitch = $_.Camera.Pitch
                framing = $_.Camera.Frame
                picture = Join-Path $outputFolder "$($_.Name).png"
                blenderClasses = Join-Path $blenderFolder "$($_.Name).classes.png"
            }
        })
    $statusPath = Join-Path $runFolder "status.json"
    $job = [ordered]@{ status = $statusPath; shard = $shardId; settleSeconds = $SettleSeconds; views = $jobViews }
    $jobPath = Join-Path $runFolder "job.json"
    [IO.File]::WriteAllText($jobPath, ($job | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
    Write-Seconds "views" $watch

    Use-LocalStack
    Write-Seconds "stack" $watch
    $script:treeBefore = Get-WorkingTreeState $repositoryRoot
    Open-Editor
    Write-Seconds "editor" $watch
    $state = Wait-EditorState { param($s) $true } "an answer"
    if ($state.playMode -ne "stopped") {
        Write-Step "Leaving the Editor's Play Mode."
        $null = Invoke-EditorCommand "editor_stop"
        $null = Wait-EditorState { param($s) $s.playMode -eq "stopped" } "the end of Play Mode"
    }

    Update-EditorScripts
    Write-Seconds "compile" $watch

    # Play Mode starts from LoginMenu, whatever scene the Editor shows; the start scene goes back after.
    $code = "var previous = UnityEditor.SceneManagement.EditorSceneManager.playModeStartScene; " +
        "var login = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditor.SceneAsset>(`"$loginScene`"); " +
        "if (login == null) { return `"error: no $loginScene`"; } " +
        "UnityEditor.SceneManagement.EditorSceneManager.playModeStartScene = login; " +
        "return previous != null ? UnityEditor.AssetDatabase.GetAssetPath(previous) : `"`";"
    $script:previousStartScene = Invoke-EditorCode "start-scene" $code
    if ($script:previousStartScene.StartsWith("error:")) {
        $script:previousStartScene = $null
        throw "The project has no $loginScene."
    }

    $script:startSceneSet = $true
    Write-Step "Entering Play Mode from LoginMenu."
    $null = Invoke-EditorCode "enter-play-mode" "UnityEditor.EditorApplication.EnterPlaymode(); return 0;"
    $null = Wait-EditorState { param($s) $s.playMode -eq "playing" } "Play Mode"
    Write-Seconds "play mode" $watch

    $started = Invoke-EditorCode "start-capture" "return Mmorpg.Client.Editor.GameViewCapture.Start(@`"$jobPath`");" -Retry
    if ($started -ne "started" -and $started -ne "running") {
        throw "The capture did not start: $started"
    }

    $status = Wait-Capture $statusPath
    if ($status.account) {
        Write-Step "The capture $($status.account)."
    }

    foreach ($phase in @($status.phases)) {
        $script:times.Add([pscustomobject]@{ Phase = "  $($phase.name)"; Seconds = [double]$phase.seconds })
    }

    Write-Seconds "capture" $watch
    if ($status.state -eq "done" -and $AfterCapture) {
        Write-Step "Running $AfterCapture in the capture's Play Mode."
        $after = Invoke-EditorCode "after-capture" ([IO.File]::ReadAllText($AfterCapture)) -Retry
        Write-Step "It answered: $after"
        Write-Seconds "after capture" $watch
    }

    Write-Step "Leaving Play Mode."
    $null = Invoke-EditorCommand "editor_stop"
    $null = Wait-EditorState { param($s) $s.playMode -eq "stopped" } "the end of Play Mode"
    Write-Seconds "leave play mode" $watch
    if ($status.state -eq "done") {
        Measure-GameViewBones $views $status
    }

    [IO.File]::WriteAllText((Join-Path $outputFolder "capture.json"), ($status | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
    if ($status.state -ne "done") {
        throw "The capture failed in the phase $($status.phase): $($status.message)"
    }

    $boneOf = @{}
    foreach ($pose in @($status.poses | Where-Object { $null -ne $_.PSObject.Properties["boneMean"] })) {
        $boneOf["$($pose.outfit)|$($pose.label)|$($pose.pose)"] = $pose
    }

    # The sheets, one per outfit and body, of the views captured.
    $byName = @{}
    foreach ($result in @($status.views)) {
        $byName[[string]$result.name] = $result
    }

    $entries = @($views | Where-Object { $byName.ContainsKey($_.Name) -and (Test-Path -LiteralPath (Join-Path $outputFolder "$($_.Name).png")) } | ForEach-Object {
            [pscustomobject]@{
                Name = $_.Name
                Group = "$($_.Outfit)_$($_.Label)"
                Pose = $_.Pose.Name
                Camera = $_.Camera.Name
                Picture = Join-Path $outputFolder "$($_.Name).png"
                Overlap = [double]$byName[$_.Name].overlap
                Blocked = [bool]$byName[$_.Name].blocked
                Bones = $boneOf["$($_.Outfit)|$($_.Label)|$($_.Pose.Name)"]
            }
        })
    foreach ($group in @($entries | Group-Object Group)) {
        Write-GameViewSheet (Join-Path $outputFolder "$($group.Name).png") @($group.Group)
    }

    Write-Seconds "sheets" $watch
    $missing = @($views | Where-Object { -not $byName.ContainsKey($_.Name) })
    $withoutBlender = @($entries | Where-Object { $_.Overlap -lt 0 })
    $low = @($entries | Where-Object { $_.Overlap -ge 0 -and $_.Overlap -lt $Overlap } | Sort-Object Overlap)
    $blocked = @($entries | Where-Object { $_.Blocked })
    $undressed = @($status.bodies | Where-Object { $_.missing })
    $awake = @($status.poses | Where-Object { $_.cloth -and -not $_.still })
    $beside = @($status.poses | Where-Object { $_.cloth -and $_.still -and -not $_.slept })
    $measuredPoses = @($status.poses | Where-Object { $null -ne $_.PSObject.Properties["boneMean"] })
    $unmeasured = @($status.poses).Count - $measuredPoses.Count
    $overBudget = @($measuredPoses | Where-Object { [double]$_.boneMean -gt $BoneBudget } | Sort-Object { [double]$_.boneMean } -Descending)
    Write-Host ([string]::Format($culture, "{0} of {1} view(s) captured, {2} of {3} pose(s) over the bone budget of {4:0.#} mm; the pictures and sheets are in artifacts/art-views/game.",
            $entries.Count, $views.Count, $overBudget.Count, $measuredPoses.Count, $BoneBudget))
    if ($withoutBlender.Count -gt 0) {
        Write-Host "$($withoutBlender.Count) view(s) have no Blender view in artifacts/art-views to compare with; run tools/art/Render-ArtViews.ps1 for them."
    }

    if ($unmeasured -gt 0) {
        Write-Host "$unmeasured pose(s) have no Blender bones in artifacts/art-views to compare with; run tools/art/Render-ArtViews.ps1 for their views."
    }

    Write-Host ("Silhouettes that overlap the Blender view's less than {0}: {1}" -f $Overlap.ToString("0.00", $culture), $low.Count)
    foreach ($entry in $low) {
        Write-Host ("  {0}: {1}" -f $entry.Name, $entry.Overlap.ToString("0.000", $culture))
    }

    # The bones per body: the mean of its poses' means, the pose with the largest mean, and the farthest bone.
    foreach ($group in @($measuredPoses | Group-Object label)) {
        $worst = @($group.Group | Sort-Object { [double]$_.boneMean } -Descending)[0]
        $farthest = @($group.Group | Sort-Object { [double]$_.boneLargest } -Descending)[0]
        Write-Host ([string]::Format($culture, "Bones on {0}: {1} pose(s), {2:0.0} mm from the Blender view's on average; the largest mean {3:0.0} mm in {4} {5}, the farthest bone {6:0.0} mm, {7} in {8} {9}.",
                $group.Name, $group.Count, ($group.Group | ForEach-Object { [double]$_.boneMean } | Measure-Object -Average).Average,
                [double]$worst.boneMean, $worst.outfit, $worst.pose, [double]$farthest.boneLargest, $farthest.boneFarthest, $farthest.outfit, $farthest.pose))
    }

    Write-Host ([string]::Format($culture, "Poses whose bones lie more than {0:0.#} mm from the Blender view's on average: {1}", $BoneBudget, $overBudget.Count))
    foreach ($pose in $overBudget) {
        Write-Host ([string]::Format($culture, "  {0} on {1}, {2}: {3:0.0} mm, the farthest bone {4:0.0} mm, {5}", $pose.outfit, $pose.label, $pose.pose,
                [double]$pose.boneMean, [double]$pose.boneLargest, $pose.boneFarthest))
    }

    foreach ($entry in $blocked) {
        Write-Host "  $($entry.Name): something of the World stands between the camera and the body."
    }

    foreach ($pose in $awake) {
        Write-Host ("  {0} on {1}, {2}: the cloth had not come to rest after {3} s." -f $pose.outfit, $pose.label, $pose.pose,
            $SettleSeconds.ToString("0.0", $culture))
    }

    if ($beside.Count -gt 0) {
        $farthest = ($beside | Measure-Object -Property clothFromTarget -Maximum).Maximum
        Write-Host ("{0} pose(s) of cloth came to rest beside the target pose, where gravity holds it as the body leans, at most {1} mm from it." -f
            $beside.Count, ([double]$farthest).ToString("0", $culture))
    }

    foreach ($body in $undressed) {
        Write-Host "  $($body.outfit) on $($body.label) does not draw $($body.missing), which a player's body in its look draws."
    }

    if ($missing.Count -gt 0 -or $undressed.Count -gt 0) {
        throw "$($missing.Count) view(s) were not captured and $($undressed.Count) outfit(s) were not drawn whole."
    }
}

New-Item -ItemType Directory -Force -Path $runFolder | Out-Null
$script:times = New-Object Collections.Generic.List[object]
$script:envCopy = $null
$script:composeNameSet = $false
$script:containersToStop = @()
$script:gameService = $null
$script:worker = $null
$script:editorOpened = $false
$script:startSceneSet = $false
$script:previousStartScene = $null
$script:treeBefore = $null
$total = [Diagnostics.Stopwatch]::StartNew()
$exitCode = 0
Push-Location $repositoryRoot
try {
    Invoke-GameViewCapture
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    $exitCode = 1
}
finally {
    $cleanup = [Diagnostics.Stopwatch]::StartNew()
    try {
        if ($script:startSceneSet -and (Test-EditorRunning)) {
            $state = Get-EditorState
            if ($state -and $state.playMode -ne "stopped") {
                $null = Invoke-EditorCommand "editor_stop"
                $null = Wait-EditorState { param($s) $s.playMode -eq "stopped" } "the end of Play Mode"
            }

            $scene = if ($script:previousStartScene) { "UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditor.SceneAsset>(`"$($script:previousStartScene)`")" } else { "null" }
            $null = Invoke-EditorCode "restore-start-scene" "UnityEditor.SceneManagement.EditorSceneManager.playModeStartScene = $scene; return 0;" -Retry
        }

        if ($script:editorOpened) {
            Close-Editor
        }

        if ($null -ne $script:treeBefore) {
            foreach ($line in @(Restore-UnityOwnedFiles $script:treeBefore $repositoryRoot)) {
                Write-Step $line
            }
        }
    }
    catch {
        Write-Host "The Editor could not be put back: $($_.Exception.Message)" -ForegroundColor Red
        $exitCode = 1
    }

    Stop-LocalStack
    Pop-Location
    Write-Seconds "clean up" $cleanup
}

foreach ($time in $script:times) {
    Write-Host ("  {0}: {1} s" -f $time.Phase, $time.Seconds.ToString("0.0", $culture))
}

Write-Host ("Capture-GameViews took {0} s." -f $total.Elapsed.TotalSeconds.ToString("0.0", $culture))
exit $exitCode
