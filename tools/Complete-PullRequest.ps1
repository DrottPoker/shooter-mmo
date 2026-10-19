# Merges a ready pull request into development with a merge commit and cleans up after it, so a
# task ends with nothing left behind. It refuses to merge unless Wait-PullRequest.ps1 reports the
# pull request ready. After the merge it deletes the task branch on GitHub and locally (detaching
# the current worktree when it has the branch checked out), deletes every other local branch that
# is fully merged into origin/development and checked out in no worktree, fast-forwards the local
# development branch, and prunes stale worktree records.
#
# Exit codes: 0 merged and cleaned up, 1 not merged, 2 merged but the local development branch
# could not be fast-forwarded. Never discard local work to fix exit code 2; the report says why.
param(
    [Parameter(Mandatory = $true)]
    [int]$Number,
    # Merge commit subject in the repository style, for example
    # "Merge the neutral corpse closure notice".
    [Parameter(Mandatory = $true)]
    [string]$Subject,
    # Merge commit body. Defaults to "<pull request title> (#<number>)."
    [string]$Body
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PullRequestCommon.ps1")

function Get-FullPath($path) {
    [IO.Path]::GetFullPath($path).TrimEnd('\', '/')
}

function Get-Worktrees {
    $worktrees = @()
    $current = $null
    foreach ($line in (Invoke-Git worktree list --porcelain)) {
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

& (Join-Path $PSScriptRoot "Wait-PullRequest.ps1") -Number $Number -NoWait
if ($LASTEXITCODE -ne 0) {
    Write-Output "Pull request #$Number is not ready to merge, so nothing was merged."
    exit 1
}

$pr = Invoke-GitHubJson -ArgumentList @("pr", "view", "$Number", "--json", "title,url,baseRefName,headRefName,headRefOid")
if ($pr.baseRefName -ne "development") {
    Write-Output "Pull request #$Number targets $($pr.baseRefName), not development, so nothing was merged."
    exit 1
}
if (-not $Body) {
    $Body = "$($pr.title) (#$Number)."
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
$merged = Invoke-GitHubJson -ArgumentList @("pr", "view", "$Number", "--json", "state,mergeCommit")
if ($merge.ExitCode -ne 0 -or $merged.state -ne "MERGED") {
    Write-Output "Merging pull request #$Number failed (gh exit code $($merge.ExitCode), state $($merged.state)), so nothing was cleaned up."
    exit 1
}
$mergeCommit = "$($merged.mergeCommit.oid)".Substring(0, 7)
Write-Output "Merged pull request #$Number into development as ${mergeCommit}: $($pr.url)"

Invoke-Git fetch origin --prune --quiet | Out-Null
$head = $pr.headRefName

if (Test-Git ls-remote --exit-code --heads origin $head) {
    Invoke-Git push origin --delete --quiet $head | Out-Null
    Write-Output "Deleted remote branch $head"
}
else {
    Write-Output "Remote branch $head is already gone"
}

$currentRoot = Get-FullPath "$(Invoke-Git rev-parse --show-toplevel)"
$holder = Get-Worktrees | Where-Object { $_.Branch -eq $head } | Select-Object -First 1
if ($holder -and (Get-FullPath $holder.Path) -eq $currentRoot) {
    Invoke-Git switch --detach --quiet | Out-Null
    $holder = $null
}

$deletedBranches = @()
$keptBranches = @()
if (Test-Git show-ref --verify --quiet "refs/heads/$head") {
    if ($holder) {
        $keptBranches += "$head (checked out in $($holder.Path))"
    }
    elseif (Test-Git merge-base --is-ancestor $head origin/development) {
        Invoke-Git branch -D --quiet $head | Out-Null
        $deletedBranches += $head
    }
    else {
        $keptBranches += "$head (has commits that are not in origin/development)"
    }
}

$worktrees = Get-Worktrees
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

$exitCode = 0
$developmentHolder = $worktrees | Where-Object { $_.Branch -eq "development" } | Select-Object -First 1
if ($developmentHolder) {
    if (Test-Git -C $developmentHolder.Path merge --ff-only --quiet origin/development) {
        Write-Output "Local development fast-forwarded to $mergeCommit in $($developmentHolder.Path)"
    }
    else {
        Write-Output "Local development in $($developmentHolder.Path) could NOT be fast-forwarded (see the git message above). Show the user what blocks it with git status and ask before stashing anything."
        $exitCode = 2
    }
}
elseif (Test-Git fetch origin development:development --quiet) {
    Write-Output "Local development fast-forwarded to $mergeCommit (not checked out anywhere)"
}
else {
    Write-Output "Local development could NOT be fast-forwarded (see the git message above)."
    $exitCode = 2
}

Invoke-Git worktree prune | Out-Null
$mainRoot = Get-FullPath $worktrees[0].Path
$otherWorktrees = @($worktrees | Where-Object { (Get-FullPath $_.Path) -notin @($mainRoot, $currentRoot) })
if ($otherWorktrees.Count -gt 0) {
    Write-Output "Other worktrees (belong to other sessions or the app's reuse pool):"
    foreach ($worktree in $otherWorktrees) {
        $branch = if ($worktree.Branch) { $worktree.Branch } else { "detached" }
        Write-Output "  $($worktree.Path) ($branch)"
    }
}
exit $exitCode
