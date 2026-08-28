#!/usr/bin/env pwsh
#
# Pin that only a release tag can name a version.
#
# GitVersion.detect asks git which tag sits on HEAD. With no filter it answers
# with whatever tag is there, and Config.init panics on a tag that is neither
# `tip` nor `vX.Y.Z` -- so `just sync-publish`, which tags every published
# snapshot `series/vN`, made the commit it had just published unbuildable.
#
# The filter is `--match v* --match tip --exclude */*`, and the reason it needs
# all three is not obvious. `--match` globs the tag name with refs/tags/
# stripped and does NOT stop at a slash: `v*` rejects `series/v2` only because
# that name starts with `s`. `vendor/v2`, `v-old/v2` and `verified/1.0` all sail
# straight through it. `--exclude */*` is what actually carries the namespace
# rule, and without it renaming the series namespace to anything starting with
# `v` silently turns a release build into a pre-release version string.
#
# So this runs real `git describe` against a throwaway repo, with the argument
# list read out of GitVersion.zig rather than copied here. A copy would keep
# passing after the source stopped matching it, which is the failure mode the
# check exists to prevent.
#
# It also runs the same table against three broken argument lists and requires
# every one to be caught. A check that cannot fail proves nothing, and these
# are the shapes the regression actually takes: the pre-fix bare `--tags`, the
# two `--match` patterns collapsed into one permissive pattern, and `--exclude`
# dropped so a namespaced `v` name gets through.
#
# No network, no desktop, no build. Exit 0 pass, 2 finding, 1 the check itself
# could not run.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Every `describe` in the table below is expected to fail for half the cases.
# This preference is $false by default today, but if a future pwsh flips it
# those expected failures become terminating errors and the check reports a
# harness break for a product that is fine.
$PSNativeCommandUseErrorActionPreference = $false

# `git -C` loses to these. A run that inherited them -- from a git hook, a
# `git` alias, `git rebase --exec` -- would aim every command here at the
# ambient repository instead of the fixture, including the tag deletions.
foreach ($name in @(
    'GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_COMMON_DIR',
    'GIT_OBJECT_DIRECTORY', 'GIT_CEILING_DIRECTORIES', 'GIT_NAMESPACE'
)) {
    Remove-Item "Env:$name" -ErrorAction SilentlyContinue
}

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$SourceFile = Join-Path $RepoRoot 'src/build/GitVersion.zig'
$script:FixtureDir = $null

# Nothing global leaks into the fixture: no signing, no hooks, no template.
$FixtureConfig = @(
    '-c', 'user.name=gitversion selftest',
    '-c', 'user.email=selftest@example.invalid',
    '-c', 'commit.gpgsign=false',
    '-c', 'tag.gpgsign=false',
    '-c', 'core.hooksPath='
)

function Stop-Harness {
    param([string]$Message)
    Write-Host "harness: $Message" -ForegroundColor Red
    exit 1
}

function Invoke-Git {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $output = & git @Arguments 2>&1
    return [pscustomobject]@{
        Code = $LASTEXITCODE
        Text = ($output | Out-String).Trim()
    }
}

# The argument list the product actually runs, taken from the Zig source.
#
# Anchored on the array literal rather than on the word "describe", because a
# "describe" in a comment would otherwise hijack the parse and get reported as
# a product finding rather than as this script failing to read the source.
function Get-DescribeArguments {
    if (-not (Test-Path -LiteralPath $SourceFile)) {
        Stop-Harness "GitVersion.zig not found at $SourceFile"
    }
    $text = Get-Content -LiteralPath $SourceFile -Raw
    $literals = @([regex]::Matches($text, '&\[_\]\[\]const u8\{[^}]*"describe"[^}]*\}'))
    if ($literals.Count -ne 1) {
        Stop-Harness "expected one describe argument list in GitVersion.zig, found $($literals.Count)"
    }
    $words = @([regex]::Matches($literals[0].Value, '"([^"]*)"') | ForEach-Object { $_.Groups[1].Value })

    # "git", "-C", <the path expression, whose only literal is its orelse
    # fallback>, "describe", ... A global option slipped in front of
    # "describe" would mean the product runs a command this script is not
    # reproducing, and a harness quietly testing a different command is worse
    # than no harness.
    $describeAt = [array]::IndexOf($words, 'describe')
    if ($words.Count -lt 5 -or $words[0] -ne 'git' -or $words[1] -ne '-C' -or $describeAt -ne 3) {
        Stop-Harness "unexpected describe invocation in GitVersion.zig: $($words -join ' ')"
    }
    if ($words -notcontains '--exact-match' -or $words -notcontains '--tags') {
        Stop-Harness "describe no longer runs --exact-match --tags: $($words -join ' ')"
    }
    return , [string[]]$words[$describeAt..($words.Count - 1)]
}

function New-FixtureRepo {
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("gitversion-selftest-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    # Recorded before the first command that could fail, so the finally block
    # can still clean up a half-built fixture.
    $script:FixtureDir = $dir

    $init = Invoke-Git @('-c', 'init.templateDir=', 'init', '--quiet', '--initial-branch=main', $dir)
    if ($init.Code -ne 0) { Stop-Harness "git init failed: $($init.Text)" }
    foreach ($subject in @('base', 'head')) {
        $commit = Invoke-Git (@('-C', $dir) + $FixtureConfig + @('commit', '--quiet', '--allow-empty', '--no-verify', '-m', $subject))
        if ($commit.Code -ne 0) { Stop-Harness "git commit failed: $($commit.Text)" }
    }
    return $dir
}

function Clear-FixtureTags {
    param([Parameter(Mandatory)][string]$Repo)
    # This is the only tag deletion in the script, and it deletes every tag it
    # finds, so it must never be pointed anywhere but the fixture this process
    # created.
    if ($Repo -ne $script:FixtureDir) {
        Stop-Harness "refusing to delete tags outside the fixture repo"
    }
    $list = Invoke-Git @('-C', $Repo, 'tag', '--list')
    foreach ($tag in ($list.Text -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        $del = Invoke-Git @('-C', $Repo, 'tag', '-d', $tag)
        if ($del.Code -ne 0) { Stop-Harness "could not clear fixture tag ${tag}: $($del.Text)" }
    }
}

# Expect $null where no tag may name a version: describe must fail rather than
# hand back a string Config.init would then panic on. `On` selects the commit
# the tag lands on, so the ancestor case checks --exact-match behaviorally
# instead of just looking for the flag in the argument list.
$Cases = @(
    @{ Name = 'published series snapshot'; Tags = @(@{ Name = 'series/v2'; Annotated = $true; On = 'head' }); Expect = $null }
    @{ Name = 'series namespace renamed to a v name'; Tags = @(@{ Name = 'vendor/v2'; Annotated = $true; On = 'head' }); Expect = $null }
    @{ Name = 'lightweight namespaced tag'; Tags = @(@{ Name = 'backup/v1.0.0'; Annotated = $false; On = 'head' }); Expect = $null }
    @{ Name = 'unnamespaced non-release tag'; Tags = @(@{ Name = 'pre-marker-scrub'; Annotated = $false; On = 'head' }); Expect = $null }
    @{ Name = 'no tag at all'; Tags = @(); Expect = $null }
    @{ Name = 'release tag on an ancestor only'; Tags = @(@{ Name = 'v1.0.0'; Annotated = $true; On = 'parent' }); Expect = $null }
    @{ Name = 'annotated release tag'; Tags = @(@{ Name = 'v1.3.1'; Annotated = $true; On = 'head' }); Expect = 'v1.3.1' }
    @{ Name = 'lightweight release tag'; Tags = @(@{ Name = 'v1.2.3'; Annotated = $false; On = 'head' }); Expect = 'v1.2.3' }
    @{ Name = 'tip'; Tags = @(@{ Name = 'tip'; Annotated = $false; On = 'head' }); Expect = 'tip' }
    @{ Name = 'release and series on one commit'; Tags = @(@{ Name = 'series/v2'; Annotated = $true; On = 'head' }, @{ Name = 'v1.3.1'; Annotated = $true; On = 'head' }); Expect = 'v1.3.1' }
)

# Every argument list is run against each tag layout while that layout is set
# up, so the fixture is torn down and rebuilt once per case rather than once
# per case per argument list.
function Invoke-CaseTable {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][object[]]$CaseTable,
        [Parameter(Mandatory)][object[]]$ArgumentLists
    )
    $failures = @{}
    foreach ($list in $ArgumentLists) { $failures[$list.Name] = New-Object System.Collections.Generic.List[string] }

    foreach ($case in $CaseTable) {
        Clear-FixtureTags -Repo $Repo
        foreach ($tag in $case.Tags) {
            $target = if ($tag.On -eq 'parent') { 'HEAD~1' } else { 'HEAD' }
            $create = if ($tag.Annotated) {
                Invoke-Git (@('-C', $Repo) + $FixtureConfig + @('tag', '-a', $tag.Name, '-m', 'fixture tag', $target))
            } else {
                Invoke-Git (@('-C', $Repo) + $FixtureConfig + @('tag', $tag.Name, $target))
            }
            if ($create.Code -ne 0) { Stop-Harness "could not create fixture tag $($tag.Name): $($create.Text)" }
        }

        foreach ($list in $ArgumentLists) {
            $result = Invoke-Git (@('-C', $Repo) + $list.Arguments)
            # GitVersion.detect treats a non-zero describe as "no tag";
            # anything else is the string it hands to Config.init.
            $got = if ($result.Code -ne 0) { $null } else { $result.Text }
            if ($got -ne $case.Expect) {
                $shownGot = if ($null -eq $got) { '<no tag>' } else { $got }
                $shownWant = if ($null -eq $case.Expect) { '<no tag>' } else { $case.Expect }
                $failures[$list.Name].Add("$($case.Name): got $shownGot, want $shownWant")
            }
        }
    }
    return $failures
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Stop-Harness 'git is not on PATH'
}

$describeArgs = Get-DescribeArguments
Write-Host "describe args from GitVersion.zig: git $($describeArgs -join ' ')"

$PRODUCT = 'GitVersion.zig'
$ArgumentLists = @(
    @{ Name = $PRODUCT; Arguments = $describeArgs }
    @{ Name = 'no --match filter (the pre-fix form)'; Arguments = [string[]]@('describe', '--exact-match', '--tags') }
    @{ Name = 'both --match patterns collapsed into one'; Arguments = [string[]]@('describe', '--exact-match', '--tags', '--match', '*') }
    @{ Name = 'no --exclude (a namespaced v name gets through)'; Arguments = [string[]]@('describe', '--exact-match', '--tags', '--match', 'v*', '--match', 'tip') }
)

try {
    $repo = New-FixtureRepo
    $failures = Invoke-CaseTable -Repo $repo -CaseTable $Cases -ArgumentLists $ArgumentLists

    $real = $failures[$PRODUCT]
    if ($real.Count -gt 0) {
        Write-Host "FAIL: a tag that is not a release names a version" -ForegroundColor Red
        foreach ($f in $real) { Write-Host "  $f" -ForegroundColor Red }
        exit 2
    }
    Write-Host "ok   $($Cases.Count) tag layouts resolve as expected"

    $survivors = New-Object System.Collections.Generic.List[string]
    foreach ($list in $ArgumentLists) {
        if ($list.Name -eq $PRODUCT) { continue }
        if ($failures[$list.Name].Count -eq 0) { $survivors.Add($list.Name) }
    }
    if ($survivors.Count -gt 0) {
        Write-Host "FAIL: the table accepts argument lists it must reject" -ForegroundColor Red
        foreach ($s in $survivors) { Write-Host "  survived: $s" -ForegroundColor Red }
        exit 2
    }
    Write-Host "ok   $($ArgumentLists.Count - 1) broken argument lists are caught"
}
finally {
    if ($script:FixtureDir -and (Test-Path -LiteralPath $script:FixtureDir)) {
        Remove-Item -LiteralPath $script:FixtureDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "gitversion selftest: pass" -ForegroundColor Green
exit 0
