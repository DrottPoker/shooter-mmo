# Waits until a pull request is ready to merge or needs work. An agent runs it once in the
# background after it opens or updates a pull request, instead of asking the user to watch CI.
# Ready means that GitHub sees the local head commit, every check passed (including every check
# the development CI gate ruleset requires), the Codex review bot finished a review of the head
# commit, no review thread is unresolved, and GitHub reports the pull request mergeable. The bot
# reviews by itself only when a pull request opens, so for a later head the script asks it with
# an "@codex review" comment. It returns as soon as there is something to fix, so the agent can
# start on a failure while other checks still run.
#
# Exit codes: 0 ready to merge, 1 a check failed, 2 unresolved review threads,
# 3 merge conflict, 4 pull request not open, 5 timed out, 6 not ready yet (only with -NoWait),
# 7 GitHub could not be read.
param(
    [Parameter(Mandatory = $true)]
    [int]$Number,
    [int]$TimeoutMinutes = 120,
    [int]$PollSeconds = 30,
    # How long the Codex review bot may stay silent or unfinished before it counts as unavailable.
    [int]$CodexWaitMinutes = 20,
    [switch]$NoWait
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PullRequestCommon.ps1")

$codexLogin = "chatgpt-codex-connector[bot]"
$codexRequestMarker = "wait-pull-request:codex-review-request"
$rulesetPath = Join-Path $PSScriptRoot "..\.github\rulesets\development-ci-gate.json"
$reviewThreadsQuery = 'query($owner: String!, $name: String!, $number: Int!) { repository(owner: $owner, name: $name) { pullRequest(number: $number) { reviewThreads(first: 100) { nodes { isResolved path line comments(first: 1) { nodes { author { login } body url } } } } } } }'

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

# The commit GitHub must report as the pull request head: the local HEAD when this checkout has
# the pull request branch checked out, so a just-pushed fix is never judged by the previous run.
function Get-ExpectedHead($headRefName) {
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
    @($results.Values | Sort-Object Name)
}

# The Codex review state of the head commit. The bot reviews a pull request by itself only when
# it opens, so a later head needs an "@codex review" request, which this script posts once per
# head and marks with the head commit. Status is completed, pending, unrequested, or unavailable
# (the bot stayed silent or unfinished for CodexWaitMinutes).
function Get-CodexReview($pr) {
    $head = "$($pr.headRefOid)"
    $comments = Invoke-GitHubJson -ArgumentList @("api", "repos/{owner}/{repo}/issues/$Number/comments?per_page=100")
    $state = $null
    $stateUpdatedAt = $null
    $requestedAt = $null
    foreach ($comment in @($comments)) {
        if ($comment.user.login -eq $codexLogin -and $comment.body -match 'codex-review-state:v1 (?<state>\{.*?\}) -->') {
            $state = ($Matches["state"] | ConvertFrom-Json).code_review
            $stateUpdatedAt = ConvertTo-UtcDateTime $comment.updated_at
        }
        elseif ($comment.body -match "$codexRequestMarker head=(?<head>[0-9a-f]{40})" -and $Matches["head"] -eq $head) {
            $requestedAt = ConvertTo-UtcDateTime $comment.created_at
        }
    }

    $review = [pscustomobject]@{
        Status = $null
        Commit = $head.Substring(0, 7)
        HasUnresolvedFindings = $false
    }
    $now = [DateTime]::UtcNow
    if ($state -and "$($state.commit_sha)" -eq $head) {
        $review.HasUnresolvedFindings = [bool]$state.has_unresolved_findings
        if ($state.status -eq "completed") { $review.Status = "completed" }
        elseif (($now - $stateUpdatedAt).TotalMinutes -ge $CodexWaitMinutes) { $review.Status = "unavailable" }
        else { $review.Status = "pending" }
    }
    elseif ($requestedAt) {
        $review.Status = if (($now - $requestedAt).TotalMinutes -ge $CodexWaitMinutes) { "unavailable" } else { "pending" }
    }
    elseif (-not $state -and ($now - (ConvertTo-UtcDateTime $pr.createdAt)).TotalMinutes -lt $CodexWaitMinutes) {
        # The automatic review of a newly opened pull request has not reported yet.
        $review.Status = "pending"
    }
    else {
        $review.Status = "unrequested"
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
    $data = Invoke-GitHubJson -ArgumentList @(
        "api", "graphql",
        "-f", "query=$reviewThreadsQuery",
        "-F", "owner={owner}",
        "-F", "name={repo}",
        "-F", "number=$Number")
    @($data.data.repository.pullRequest.reviewThreads.nodes | Where-Object { $_ -and -not $_.isResolved })
}

function Get-Readiness {
    $pr = Invoke-GitHubJson -ArgumentList @("pr", "view", "$Number", "--json", "url,state,baseRefName,headRefName,headRefOid,mergeable,createdAt,statusCheckRollup")
    $readiness = [pscustomobject]@{
        PullRequest = $pr
        ExpectedHead = $null
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

    $readiness.ExpectedHead = Get-ExpectedHead $pr.headRefName
    if ($readiness.ExpectedHead -and $readiness.ExpectedHead -ne $pr.headRefOid) {
        $readiness.Pending = @("GitHub to see commit $($readiness.ExpectedHead.Substring(0, 7)) (is it pushed?)")
        return $readiness
    }

    $readiness.Checks = Get-CheckResults $pr.statusCheckRollup
    $readiness.Codex = Get-CodexReview $pr
    if ($readiness.Codex.Status -eq "unrequested" -and -not $NoWait) {
        Request-CodexReview $pr.headRefOid
        $readiness.Codex.Status = "pending"
    }
    $readiness.Threads = Get-UnresolvedThreads

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
    # Codex posts every finding as a review thread, and its summary flag can lag behind threads
    # that were already resolved, so the threads decide.
    if ($readiness.Threads.Count -gt 0) {
        $readiness.ExitCode = 2
        $readiness.Result = "$($readiness.Threads.Count) unresolved review threads"
        return $readiness
    }

    $pending = @($readiness.Checks | Where-Object { $_.State -eq "pending" } | ForEach-Object { $_.Name })
    if ($readiness.Codex.Status -in @("pending", "unrequested")) { $pending += "Codex review of $($readiness.Codex.Commit)" }
    if ($pr.mergeable -ne "MERGEABLE") { $pending += "GitHub mergeability" }
    $readiness.Pending = $pending
    if ($pending.Count -eq 0) {
        $readiness.ExitCode = 0
        $readiness.Result = "ready to merge"
    }
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
                $findings = if ($codex.HasUnresolvedFindings) { "with unresolved findings" } else { "with no unresolved findings" }
                Write-Output "Codex review: completed on $($codex.Commit) $findings"
            }
            "unavailable" { Write-Output "Codex review: no finished review of $($codex.Commit) after $CodexWaitMinutes minutes; continuing without it" }
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
