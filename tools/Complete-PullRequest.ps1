# Merges a ready pull request into development with a merge commit and cleans up after it, so a
# task ends with nothing left behind. It refuses to merge unless Wait-PullRequest.ps1 reports the
# pull request ready. After the merge it deletes the task branch on GitHub and locally (switching
# the main checkout to development, or detaching a task worktree, when it has the branch checked
# out), deletes every other local branch that is fully merged into origin/development and checked
# out in no worktree, fast-forwards the local development branch, and prunes stale worktree
# records. Run it again on an already merged pull request to finish a cleanup that failed.
#
# Exit codes: 0 merged and cleaned up, 1 not merged, 2 merged but the cleanup is incomplete: the
# remote branch moved after the merge, the task branch had to stay (uncommitted changes, another
# worktree, or commits that are not in development), or the local development branch does not
# match origin/development. The report says why; never discard local work to fix it. Pull
# requests from main, development, a fork, or an unusual branch name are refused.
param(
    [Parameter(Mandatory = $true)]
    [int]$Number,
    # Merge commit subject in the repository style, for example
    # "Merge the neutral corpse closure notice". Required unless the pull request is merged.
    [string]$Subject,
    # Merge commit body. Defaults to "<pull request title> (#<number>)."
    [string]$Body,
    # Merge although the Codex bot did not review the head (Wait-PullRequest.ps1 exit code 8).
    [switch]$AllowMissingCodexReview
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PullRequestCommon.ps1")

function Get-FullPath($path) {
    [IO.Path]::GetFullPath($path).TrimEnd('\', '/')
}

function Get-Worktrees {
    $worktrees = @()
    $current = $null
    foreach ($line in @(Invoke-Git worktree list --porcelain)) {
        if ($line.StartsWith("worktree ")) {
            $current = [pscustomobject]@{ Path = $line.Substring(9); Branch = $null }
            $worktrees += $current
        }
        elseif ($line.StartsWith("branch refs/heads/") -and $current) {
            $current.Branch = $line.Substring(18)
        }
    }
    $worktrees
}

# The head is read once and pinned: readiness is judged for this commit, and the merge only
# succeeds while the pull request head is still this commit. A pull request that is already
# merged goes straight to the cleanup, so a rerun finishes a cleanup that failed halfway.
$pr = Invoke-GitHubJson -ArgumentList @("pr", "view", "$Number", "--json", "title,url,state,baseRefName,headRefName,headRefOid,isCrossRepository,mergeCommit")
if ($pr.baseRefName -ne "development") {
    Write-Output "Pull request #$Number targets $($pr.baseRefName), not development, so nothing was merged."
    exit 1
}
# The cleanup deletes the head branch on origin, so the head must be a task branch of this
# repository: never a long-lived branch, never a fork, and with a name that native arguments
# carry unchanged.
if ($pr.isCrossRepository) {
    Write-Output "Pull request #$Number comes from a fork. This script only completes task branches of this repository, so nothing was merged or deleted."
    exit 1
}
if ($pr.headRefName -in @("main", "development")) {
    Write-Output "Pull request #$Number comes from the long-lived branch $($pr.headRefName). This script only completes task branches, so nothing was merged or deleted."
    exit 1
}
if ($pr.headRefName -notmatch '^[A-Za-z0-9._/-]+$') {
    Write-Output "Pull request #$Number comes from a branch name with characters other than letters, digits, '.', '_', '/', and '-', so nothing was merged or deleted."
    exit 1
}
if ($Subject -and $Subject.Contains('"')) {
    Write-Output "The merge commit subject must not contain double quotes, so nothing was merged."
    exit 1
}

if ($pr.state -eq "MERGED") {
    $mergeCommit = "$($pr.mergeCommit.oid)".Substring(0, 7)
    Write-Output "Pull request #$Number is already merged into development as $mergeCommit; finishing the cleanup"
}
elseif ($pr.state -ne "OPEN") {
    Write-Output "Pull request #$Number is $($pr.state.ToLowerInvariant()), so nothing was merged."
    exit 1
}
else {
    if (-not $Subject) {
        Write-Output "A merge commit subject (-Subject) is required to merge pull request #$Number."
        exit 1
    }
    $waitArguments = @{ Number = $Number; HeadCommit = $pr.headRefOid; NoWait = $true }
    if ($AllowMissingCodexReview) {
        $waitArguments.AllowMissingCodexReview = $true
    }
    & (Join-Path $PSScriptRoot "Wait-PullRequest.ps1") @waitArguments
    if ($LASTEXITCODE -ne 0) {
        Write-Output "Pull request #$Number is not ready to merge, so nothing was merged."
        exit 1
    }

    if (-not $Body) {
        $Body = "$($pr.title) (#$Number)."
    }

    # --match-head-commit pins the head but not the base, so the base is read again right before
    # the merge and checked again after it.
    $latest = Invoke-GitHubJson -ArgumentList @("pr", "view", "$Number", "--json", "state,baseRefName,headRefOid")
    if ($latest.state -ne "OPEN" -or $latest.baseRefName -ne "development" -or $latest.headRefOid -ne $pr.headRefOid) {
        Write-Output "Pull request #$Number changed during the readiness check (state $($latest.state), base $($latest.baseRefName), head $("$($latest.headRefOid)".Substring(0, 7))), so nothing was merged."
        exit 1
    }

    # The body goes through a file because native arguments cannot carry double quotes here.
    $bodyFile = Join-Path ([IO.Path]::GetTempPath()) "merge-body-$Number-$PID.txt"
    [IO.File]::WriteAllText($bodyFile, $Body, (New-Object Text.UTF8Encoding $false))
    try {
        $merge = Invoke-NativeCommand -FilePath "gh" -ArgumentList @(
            "pr", "merge", "$Number", "--merge",
            "--subject", $Subject,
            "--body-file", $bodyFile,
            "--match-head-commit", $pr.headRefOid)
    }
    finally {
        Remove-Item -LiteralPath $bodyFile -ErrorAction SilentlyContinue
    }
    $merged = Invoke-GitHubJson -ArgumentList @("pr", "view", "$Number", "--json", "state,baseRefName,mergeCommit")
    if ($merge.ExitCode -ne 0 -or $merged.state -ne "MERGED") {
        Write-Output "Merging pull request #$Number failed (gh exit code $($merge.ExitCode), state $($merged.state)), so nothing was cleaned up."
        exit 1
    }
    if ($merged.baseRefName -ne "development") {
        Write-Output "Pull request #$Number was retargeted and merged into $($merged.baseRefName), not development. Nothing was cleaned up; tell the user."
        exit 1
    }
    $mergeCommit = "$($merged.mergeCommit.oid)".Substring(0, 7)
    Write-Output "Merged pull request #$Number into development as ${mergeCommit}: $($pr.url)"
}

Invoke-Git fetch origin --prune --quiet | Out-Null
$head = $pr.headRefName
$mergedHead = "$($pr.headRefOid)"
$exitCode = 0

# Delete the remote branch only while it still points to the merged head, so commits pushed to
# a reused branch name after the merge are never deleted.
$remoteTip = @(Invoke-Git ls-remote origin "refs/heads/$head")
if ($remoteTip.Count -eq 0) {
    Write-Output "Remote branch $head is already gone"
}
elseif (($remoteTip[0] -split '\s+')[0] -ne $mergedHead) {
    Write-Output "Kept remote branch $head because it no longer points to the merged head $($mergedHead.Substring(0, 7))"
    $exitCode = 2
}
else {
    Invoke-Git push --quiet "--force-with-lease=refs/heads/${head}:$mergedHead" origin --delete $head | Out-Null
    Write-Output "Deleted remote branch $head"
}

$currentRoot = Get-FullPath "$(Invoke-Git rev-parse --show-toplevel)"
$worktrees = @(Get-Worktrees)
$mainRoot = Get-FullPath $worktrees[0].Path
$holder = $worktrees | Where-Object { $_.Branch -eq $head } | Select-Object -First 1
$holderIsDirty = $false
if ($holder -and (Get-FullPath $holder.Path) -eq $currentRoot) {
    if (@(Invoke-Git status --porcelain).Count -gt 0) {
        # Leaving uncommitted work on a detached HEAD without its branch would hide it.
        $holderIsDirty = $true
    }
    else {
        # The main checkout returns to development, which is fast-forwarded below. A task
        # worktree is detached instead, because development is checked out in the main checkout.
        $developmentHeld = @($worktrees | Where-Object { $_.Branch -eq "development" }).Count -gt 0
        if ($currentRoot -eq $mainRoot -and -not $developmentHeld -and (Test-Git switch --quiet development)) {
            Write-Output "Switched the main checkout to development"
        }
        else {
            Invoke-Git switch --detach --quiet | Out-Null
        }
        $holder = $null
    }
}

$deletedBranches = @()
$keptBranches = @()
if (Test-Git show-ref --verify --quiet "refs/heads/$head") {
    if ($holderIsDirty) {
        $keptBranches += "$head because this checkout has uncommitted changes; move them to a new branch for their own pull request, then run this script again"
    }
    elseif ($holder) {
        $keptBranches += "$head because it is checked out in $($holder.Path)"
    }
    elseif (Test-Git merge-base --is-ancestor $head origin/development) {
        Invoke-Git branch -D --quiet $head | Out-Null
        $deletedBranches += $head
    }
    else {
        $keptBranches += "$head because it has commits that are not in origin/development; merge them through another pull request or move them to another branch"
    }
}
# Any task branch that stays behind means the cleanup is not finished.
if ($keptBranches.Count -gt 0) {
    $exitCode = 2
}

$worktrees = @(Get-Worktrees)
$checkedOut = @($worktrees | ForEach-Object { $_.Branch } | Where-Object { $_ })
foreach ($branch in @(Invoke-Git for-each-ref '--format=%(refname:short)' refs/heads)) {
    if ($branch -in @("main", "development") -or $branch -in $checkedOut) {
        continue
    }
    if (Test-Git merge-base --is-ancestor $branch origin/development) {
        Invoke-Git branch -D --quiet $branch | Out-Null
        $deletedBranches += $branch
    }
}
if ($deletedBranches.Count -gt 0) {
    Write-Output "Deleted local branches: $($deletedBranches -join ', ')"
}
foreach ($branch in $keptBranches) {
    Write-Output "Kept local branch $branch"
}

$developmentTip = "$(Invoke-Git rev-parse --short origin/development)"
$developmentHolder = $worktrees | Where-Object { $_.Branch -eq "development" } | Select-Object -First 1
if ($developmentHolder) {
    $developmentUpdated = Test-Git -C $developmentHolder.Path merge --ff-only --quiet origin/development
    $developmentPlace = "in $($developmentHolder.Path)"
}
else {
    $developmentUpdated = Test-Git fetch origin development:development --quiet
    $developmentPlace = "(not checked out anywhere)"
}
# A fast-forward to an ancestor succeeds without moving the branch, so compare the tips too.
if ($developmentUpdated -and "$(Invoke-Git rev-parse development)" -eq "$(Invoke-Git rev-parse origin/development)") {
    Write-Output "Local development matches origin/development ($developmentTip) $developmentPlace"
}
elseif ($developmentUpdated) {
    Write-Output "Local development $developmentPlace has commits that are not in origin/development. Show the user and ask what to do with them."
    $exitCode = 2
}
else {
    Write-Output "Local development $developmentPlace could NOT be fast-forwarded (see the git message above). Show the user what blocks it with git status and ask before stashing anything."
    $exitCode = 2
}

Invoke-Git worktree prune | Out-Null
$otherWorktrees = @($worktrees | Where-Object { (Get-FullPath $_.Path) -notin @($mainRoot, $currentRoot) })
if ($otherWorktrees.Count -gt 0) {
    Write-Output "Other worktrees (belong to other sessions or the app's reuse pool):"
    foreach ($worktree in $otherWorktrees) {
        $branch = if ($worktree.Branch) { $worktree.Branch } else { "detached" }
        Write-Output "  $($worktree.Path) ($branch)"
    }
}
exit $exitCode
