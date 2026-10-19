# Waits until a pull request is ready to merge or needs work. An agent runs it once in the
# background after it opens or updates a pull request, instead of asking the user to watch CI.
# Ready means that GitHub reports the expected head commit, every check passed (including every
# check the development CI gate ruleset requires), the Codex review bot finished a review of the
# head commit, no review thread is unresolved, and GitHub reports the pull request mergeable.
# The bot reviews by itself only when a pull request opens, so for a later head the script asks
# it with an "@codex review" comment, at most twice per head. It returns as soon as there is
# something to fix, so the agent can start on a failure while other checks still run.
#
# Exit codes: 0 ready to merge, 1 a check failed, 2 unresolved review threads, 3 merge conflict,
# 4 pull request not open, 5 timed out, 6 not ready yet (only with -NoWait), 7 GitHub could not
# be read, 8 everything else is ready but the Codex bot did not review the head.
param(
    [Parameter(Mandatory = $true)]
    [int]$Number,
    # The head commit to judge. Defaults to the local HEAD when this checkout has the pull request
    # branch checked out, so a just-pushed fix is never judged by the previous run.
    [string]$HeadCommit,
    [int]$TimeoutMinutes = 120,
    [int]$PollSeconds = 30,
    # How long the Codex review bot may stay silent or unfinished after the pull request opened or
    # after a review request.
    [int]$CodexWaitMinutes = 20,
    # Count a head that the Codex bot did not review (exit code 8) as ready.
    [switch]$AllowMissingCodexReview,
    # Evaluate once and never post a review request.
    [switch]$NoWait
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PullRequestCommon.ps1")

$codexLogin = "chatgpt-codex-connector[bot]"
$codexRequestMarker = "wait-pull-request:codex-review-request"
$maximumCodexRequests = 2
$rulesetPath = Join-Path $PSScriptRoot "..\.github\rulesets\development-ci-gate.json"
$reviewThreadsQuery = 'query($owner: String!, $name: String!, $number: Int!, $after: String) { repository(owner: $owner, name: $name) { pullRequest(number: $number) { reviewThreads(first: 100, after: $after) { pageInfo { hasNextPage endCursor } nodes { isResolved path line comments(first: 1) { nodes { author { login } body url } } } } } } }'

$requiredChecks = @(
    (Get-Content -Raw -LiteralPath $rulesetPath | ConvertFrom-Json).rules |
        Where-Object { $_.type -eq "required_status_checks" } |
        ForEach-Object { $_.parameters.required_status_checks } |
        ForEach-Object { $_.context }
)

function ConvertTo-UtcDateTime($value) {
    if ($value -is [datetime]) {
        return $value.ToUniversalTime()
    }
    [DateTimeOffset]::Parse($value, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime
}

function Test-CodexWaitOver($since) {
    ([DateTime]::UtcNow - $since).TotalMinutes -ge $CodexWaitMinutes
}

function Get-ExpectedHead($headRefName) {
    if ($HeadCommit) {
        return $HeadCommit
    }
    $branch = Invoke-NativeCommand -FilePath "git" -ArgumentList @("rev-parse", "--abbrev-ref", "HEAD")
    if ($branch.ExitCode -ne 0 -or "$($branch.Output)" -ne $headRefName) {
        return $null
    }
    "$(Invoke-Git rev-parse HEAD)"
}

function Get-CheckResults($rollup) {
    $results = @{}
    foreach ($item in @($rollup | Where-Object { $_ })) {
        if ($item.__typename -eq "StatusContext") {
            $name = $item.context
            $url = $item.targetUrl
            if ($item.state -eq "SUCCESS") { $state = "passed" }
            elseif ($item.state -in @("FAILURE", "ERROR")) { $state = "failed" }
            else { $state = "pending" }
        }
        else {
            $name = $item.name
            $url = $item.detailsUrl
            if ($item.status -ne "COMPLETED") { $state = "pending" }
            elseif ($item.conclusion -in @("SUCCESS", "NEUTRAL", "SKIPPED")) { $state = "passed" }
            else { $state = "failed" }
        }
        $results[$name] = [pscustomobject]@{ Name = $name; State = $state; Url = $url }
    }
    foreach ($name in $requiredChecks) {
        if (-not $results.ContainsKey($name)) {
            $results[$name] = [pscustomobject]@{ Name = $name; State = "pending"; Url = $null }
        }
    }
    $results.Values | Sort-Object Name
}

# The Codex review state of the head commit: completed, pending, unrequested (a review request
# is due), or unavailable (the bot did not finish after the last allowed request).
function Get-CodexReview($pr) {
    $head = "$($pr.headRefOid)"
    $pages = Invoke-GitHubJson -ArgumentList @("api", "--paginate", "--slurp", "repos/{owner}/{repo}/issues/$Number/comments?per_page=100")
    $state = $null
    $stateUpdatedAt = $null
    $requestCount = 0
    $lastRequestAt = $null
    foreach ($comment in @($pages | ForEach-Object { $_ })) {
        if ($comment.user.login -eq $codexLogin -and $comment.body -match 'codex-review-state:v1 (?<state>\{.*?\}) -->') {
            $state = ($Matches["state"] | ConvertFrom-Json).code_review
            $stateUpdatedAt = ConvertTo-UtcDateTime $comment.updated_at
        }
        elseif ($comment.body -match "$codexRequestMarker head=(?<head>[0-9a-f]{40})" -and $Matches["head"] -eq $head) {
            $requestCount++
            $lastRequestAt = ConvertTo-UtcDateTime $comment.created_at
        }
    }

    $review = [pscustomobject]@{
        Status = $null
        Commit = $head.Substring(0, 7)
        HasUnresolvedFindings = $false
        RequestCount = $requestCount
    }
    $reviewsHead = $state -and "$($state.commit_sha)" -eq $head
    if ($reviewsHead) {
        $review.HasUnresolvedFindings = [bool]$state.has_unresolved_findings
    }
    if ($reviewsHead -and $state.status -eq "completed") {
        $review.Status = "completed"
    }
    elseif ($reviewsHead -and -not (Test-CodexWaitOver $stateUpdatedAt)) {
        $review.Status = "pending"
    }
    elseif ($lastRequestAt -and -not (Test-CodexWaitOver $lastRequestAt)) {
        $review.Status = "pending"
    }
    elseif (-not $state -and $requestCount -eq 0 -and -not (Test-CodexWaitOver (ConvertTo-UtcDateTime $pr.createdAt))) {
        # The automatic review of a newly opened pull request has not reported yet.
        $review.Status = "pending"
    }
    elseif ($requestCount -lt $maximumCodexRequests) {
        $review.Status = "unrequested"
    }
    else {
        $review.Status = "unavailable"
    }
    $review
}

function Request-CodexReview($head) {
    $bodyFile = Join-Path ([IO.Path]::GetTempPath()) "codex-review-request-$Number-$PID.txt"
    $body = "@codex review`n`n<!-- $codexRequestMarker head=$head -->`n"
    [IO.File]::WriteAllText($bodyFile, $body, (New-Object Text.UTF8Encoding $false))
    try {
        $result = Invoke-NativeCommand -FilePath "gh" -ArgumentList @("pr", "comment", "$Number", "--body-file", $bodyFile)
    }
    finally {
        Remove-Item -LiteralPath $bodyFile -ErrorAction SilentlyContinue
    }
    if ($result.ExitCode -ne 0) {
        throw "Requesting a Codex review failed with exit code $($result.ExitCode)."
    }
}

function Get-UnresolvedThreads {
    $threads = @()
    $after = $null
    do {
        $arguments = @(
            "api", "graphql",
            "-f", "query=$reviewThreadsQuery",
            "-F", "owner={owner}",
            "-F", "name={repo}",
            "-F", "number=$Number")
        if ($after) {
            $arguments += @("-f", "after=$after")
        }
        $connection = (Invoke-GitHubJson -ArgumentList $arguments).data.repository.pullRequest.reviewThreads
        $threads += @($connection.nodes | Where-Object { $_ -and -not $_.isResolved })
        $after = $connection.pageInfo.endCursor
    } while ($connection.pageInfo.hasNextPage)
    $threads
}

function Get-Readiness {
    $pr = Invoke-GitHubJson -ArgumentList @("pr", "view", "$Number", "--json", "url,state,baseRefName,headRefName,headRefOid,mergeable,createdAt,statusCheckRollup")
    $readiness = [pscustomobject]@{
        PullRequest = $pr
        Checks = @()
        Codex = $null
        Threads = @()
        ExitCode = $null
        Result = $null
        Pending = @()
    }
    if ($pr.state -ne "OPEN") {
        $readiness.ExitCode = 4
        $readiness.Result = "pull request is $($pr.state.ToLowerInvariant())"
        return $readiness
    }

    $expectedHead = Get-ExpectedHead $pr.headRefName
    if ($expectedHead -and $expectedHead -ne $pr.headRefOid) {
        $readiness.Pending = @("GitHub to report commit $($expectedHead.Substring(0, 7)) as the head (is it pushed?)")
        return $readiness
    }

    $readiness.Checks = @(Get-CheckResults $pr.statusCheckRollup)
    $readiness.Codex = Get-CodexReview $pr
    if ($readiness.Codex.Status -eq "unrequested" -and -not $NoWait) {
        Request-CodexReview $pr.headRefOid
        $readiness.Codex.Status = "pending"
        $readiness.Codex.RequestCount++
    }
    $readiness.Threads = @(Get-UnresolvedThreads)

    $failed = @($readiness.Checks | Where-Object { $_.State -eq "failed" })
    if ($failed.Count -gt 0) {
        $readiness.ExitCode = 1
        $readiness.Result = "failed checks: $(($failed | ForEach-Object { $_.Name }) -join ', ')"
        return $readiness
    }
    if ($pr.mergeable -eq "CONFLICTING") {
        $readiness.ExitCode = 3
        $readiness.Result = "merge conflict with $($pr.baseRefName)"
        return $readiness
    }
    # Codex posts every finding as a review thread, and its summary flag stays true after the
    # threads are resolved, so the threads decide.
    if ($readiness.Threads.Count -gt 0) {
        $readiness.ExitCode = 2
        $readiness.Result = "$($readiness.Threads.Count) unresolved review threads"
        return $readiness
    }

    $pending = @($readiness.Checks | Where-Object { $_.State -eq "pending" } | ForEach-Object { $_.Name })
    if ($readiness.Codex.Status -in @("pending", "unrequested")) { $pending += "Codex review of $($readiness.Codex.Commit)" }
    if ($pr.mergeable -ne "MERGEABLE") { $pending += "GitHub mergeability" }
    $readiness.Pending = $pending
    if ($pending.Count -gt 0) {
        return $readiness
    }
    if ($readiness.Codex.Status -eq "unavailable" -and -not $AllowMissingCodexReview) {
        $readiness.ExitCode = 8
        $readiness.Result = "the Codex bot did not review $($readiness.Codex.Commit) after $($readiness.Codex.RequestCount) requests"
        return $readiness
    }
    $readiness.ExitCode = 0
    $readiness.Result = "ready to merge"
    $readiness
}

function Write-Report($readiness) {
    $pr = $readiness.PullRequest
    Write-Output "Pull request #${Number}: $($pr.url)"
    Write-Output "Head: $("$($pr.headRefOid)".Substring(0, 7)) on $($pr.headRefName) into $($pr.baseRefName)"
    if ($readiness.Checks.Count -gt 0) {
        Write-Output "Checks:"
        foreach ($check in $readiness.Checks) {
            $line = "  {0,-7} {1}" -f $check.State, $check.Name
            if ($check.State -eq "failed" -and $check.Url) { $line += " ($($check.Url))" }
            Write-Output $line
        }
    }
    if ($readiness.Codex) {
        $codex = $readiness.Codex
        switch ($codex.Status) {
            "completed" {
                $findings = if ($codex.HasUnresolvedFindings) { "and reported findings" } else { "with no findings" }
                Write-Output "Codex review: completed on $($codex.Commit) $findings"
            }
            "unavailable" { Write-Output "Codex review: no finished review of $($codex.Commit) after $($codex.RequestCount) requests" }
            "unrequested" { Write-Output "Codex review: $($codex.Commit) is not reviewed yet; waiting without -NoWait requests a review" }
            default { Write-Output "Codex review: pending for $($codex.Commit)" }
        }
    }
    if ($readiness.Threads.Count -gt 0) {
        Write-Output "Unresolved review threads:"
        foreach ($thread in $readiness.Threads) {
            $comment = @($thread.comments.nodes)[0]
            # Drop the badge images and markup that the Codex bot puts in its first line.
            $firstLine = ("$($comment.body)".Split("`n")[0] -replace '</?sub>|!\[[^\]]*\]\([^)]*\)|\*\*', '').Trim()
            if ($firstLine.Length -gt 140) { $firstLine = $firstLine.Substring(0, 140) + "..." }
            Write-Output "  $($thread.path):$($thread.line) by $($comment.author.login): $firstLine"
            Write-Output "    $($comment.url)"
        }
    }
    if ($readiness.Result) {
        Write-Output "Result: $($readiness.Result)"
    }
    elseif ($readiness.Pending.Count -gt 0) {
        Write-Output "Result: waiting for $($readiness.Pending -join ', ')"
    }
}

$deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
$readFailures = 0
$lastWaitingLine = $null
while ($true) {
    try {
        $readiness = Get-Readiness
        $readFailures = 0
    }
    catch {
        $readFailures++
        if ($NoWait -or $readFailures -ge 5) {
            Write-Output "Result: GitHub could not be read: $($_.Exception.Message)"
            exit 7
        }
        Write-Output "$(Get-Date -Format HH:mm:ss) could not read GitHub, retrying: $($_.Exception.Message)"
        Start-Sleep -Seconds $PollSeconds
        continue
    }

    if ($null -ne $readiness.ExitCode) {
        Write-Report $readiness
        exit $readiness.ExitCode
    }
    if ($NoWait) {
        Write-Report $readiness
        exit 6
    }
    if ([DateTime]::UtcNow -ge $deadline) {
        Write-Report $readiness
        Write-Output "Result: timed out after $TimeoutMinutes minutes"
        exit 5
    }

    $waitingLine = "waiting for $($readiness.Pending -join ', ')"
    if ($waitingLine -ne $lastWaitingLine) {
        Write-Output "$(Get-Date -Format HH:mm:ss) $waitingLine"
        $lastWaitingLine = $waitingLine
    }
    Start-Sleep -Seconds $PollSeconds
}
