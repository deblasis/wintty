#!/usr/bin/env pwsh
#
# Pin that the build reads its tags from the HEAD commit only, and that the
# tag-candidate rule stays where the tests can see it.
#
# GitVersion.detect asks git for every tag on HEAD (`git tag --points-at
# HEAD`) and GitVersion.releaseTag decides which of them may name a version:
# an unnamespaced `v...` tag that equals the build.zig version, with `vt-*`,
# `tip`, namespaced tags and the fork's `w...` release tags all skipped.
# That rule has Zig unit tests (src/build/GitVersion.zig, rooted from
# src/build/test.zig). What this script guards is the git side: the command
# the product runs must report exactly the HEAD tags and nothing else.
#
# The retired sync flow tagged every published snapshot `series/vN`, and a
# build that read an ancestor's tag as its own version produced a release
# string no release had. `--points-at HEAD` is what makes that impossible,
# so this runs real git against a throwaway repo, with the argument list
# read out of GitVersion.zig rather than copied here. A copy would keep
# passing after the source stopped matching it, which is the failure mode
# the check exists to prevent.
#
# It also runs the same table against broken argument lists (no
# `--points-at`, and `--points-at HEAD~1`) and requires every one to be
# caught. A check that cannot fail proves nothing.
#
# No network, no desktop, no build. Exit 0 pass, 2 finding, 1 the check itself
# could not run.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Every git call here is expected to succeed, but a future pwsh that flips
# this preference would turn a plain non-zero exit into a terminating error
# and the check would report a harness break for a repository that is fine.
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

# The words of one `&[_][]const u8{ ... }` literal. The path expression is
# not a quoted literal in the 0.17 build API (`b.root.subPathOrDot()`), so it
# is represented as <root>; a quoted global option in that slot is still
# rejected by Get-TagArguments below.
function Get-ArgumentWords {
    param([Parameter(Mandatory)][string]$Literal)
    $body = $Literal.Substring($Literal.IndexOf('{') + 1)
    $body = $body.Substring(0, $body.LastIndexOf('}'))
    return , @(
        foreach ($element in $body -split ',') {
            $e = $element.Trim()
            if ($e -match '^"([^"]*)"$') {
                $Matches[1]
            } else {
                '<root>'
            }
        }
    )
}

# Commands that list refs. The tag list must come from the one
# `tag --points-at HEAD` invocation and nothing else: a nearest-tag fallback
# (`git describe --tags --abbrev=0`) added beside it would leave every layout
# below green while ancestor tags are consumed again, which is the exact
# release-string bug this harness was written for.
function Find-SecondRefListing {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$AllowedLiteral
    )
    $listers = @('tag', 'describe', 'for-each-ref', 'show-ref', 'name-rev')
    $found = @()
    foreach ($block in [regex]::Matches($Text, '&\[_\]\[\]const u8\{[^}]*\}')) {
        if ($block.Value -eq $AllowedLiteral) { continue }
        $words = Get-ArgumentWords $block.Value
        foreach ($word in $words) {
            if ($listers -contains $word) { $found += ($words -join ' ') }
        }
    }
    return , $found
}

# The argument list the product actually runs, taken from the Zig source.
#
# Anchored on the array literal rather than on the word "tag", because a
# "tag" in a comment would otherwise hijack the parse and get reported as
# a product finding rather than as this script failing to read the source.
function Get-TagArguments {
    if (-not (Test-Path -LiteralPath $SourceFile)) {
        Stop-Harness "GitVersion.zig not found at $SourceFile"
    }
    $text = Get-Content -LiteralPath $SourceFile -Raw
    $literals = @([regex]::Matches($text, '&\[_\]\[\]const u8\{[^}]*"--points-at"[^}]*\}'))
    if ($literals.Count -ne 1) {
        Stop-Harness "expected one --points-at argument list in GitVersion.zig, found $($literals.Count)"
    }
    # Split the array literal into its elements. The path expression is not
    # a quoted literal in the 0.17 build API (`b.root.subPathOrDot()`), so it
    # is represented as <root>; a quoted global option in that slot is still
    # rejected below.
    $words = Get-ArgumentWords $literals[0].Value

    # "git", "-C", <root>, "tag", "--points-at", "HEAD". A different shape
    # would mean the product runs a command this script is not reproducing,
    # and a harness quietly testing a different command is worse than none.
    if (
        $words.Count -ne 6 -or
        $words[0] -ne 'git' -or
        $words[1] -ne '-C' -or
        $words[2] -ne '<root>' -or
        $words[3] -ne 'tag' -or
        $words[4] -ne '--points-at' -or
        $words[5] -ne 'HEAD'
    ) {
        Stop-Harness "unexpected tag invocation in GitVersion.zig: $($words -join ' ')"
    }
    $extra = Find-SecondRefListing -Text $text -AllowedLiteral $literals[0].Value
    if ($extra.Count -gt 0) {
        Stop-Harness "a second ref-listing git invocation can feed GitVersion.detect: $($extra -join '; ')"
    }
    return , [string[]]$words[3..($words.Count - 1)]
}

# The guard above has to be able to fail: a describe fallback added beside
# the tag invocation is the edit it exists to catch, and it must be reported
# from the same helper the real read uses.
$mutantText = Get-Content -LiteralPath $SourceFile -Raw
$mutantText += @'
const _fallback = &[_][]const u8{ "git", "-C", b.root.subPathOrDot(), "describe", "--tags", "--abbrev=0" };
'@
$mutantAllowed = [regex]::Matches($mutantText, '&\[_\]\[\]const u8\{[^}]*"--points-at"[^}]*\}')[0].Value
$mutantHits = Find-SecondRefListing -Text $mutantText -AllowedLiteral $mutantAllowed
if ($mutantHits.Count -lt 1 -or ($mutantHits -join ' ') -notmatch 'describe') {
    Stop-Harness "the second-invocation guard did not catch a describe fallback: $($mutantHits -join '; ')"
}
Write-Host 'ok second ref-listing invocation is refused'

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

# Expect the exact set of tags the command reports for HEAD. `On` selects the
# commit the tag lands on, so the ancestor case checks `--points-at HEAD`
# behaviorally instead of just looking for the flag in the argument list.
$Cases = @(
    @{ Name = 'no tag at all'; Tags = @(); Expect = @() }
    @{ Name = 'annotated release tag on head'; Tags = @(@{ Name = 'v1.3.1'; Annotated = $true; On = 'head' }); Expect = @('v1.3.1') }
    @{ Name = 'lightweight release tag on head'; Tags = @(@{ Name = 'v1.2.3'; Annotated = $false; On = 'head' }); Expect = @('v1.2.3') }
    @{ Name = 'release tag on an ancestor only'; Tags = @(@{ Name = 'v1.0.0'; Annotated = $true; On = 'parent' }); Expect = @() }
    @{ Name = 'namespaced tag on head is reported'; Tags = @(@{ Name = 'series/v2'; Annotated = $true; On = 'head' }); Expect = @('series/v2') }
    @{ Name = 'several tags on one commit'; Tags = @(@{ Name = 'series/v2'; Annotated = $true; On = 'head' }, @{ Name = 'v1.3.1'; Annotated = $true; On = 'head' }); Expect = @('series/v2', 'v1.3.1') }
    @{ Name = 'ancestor tag does not join a head tag'; Tags = @(@{ Name = 'v1.0.0'; Annotated = $true; On = 'parent' }, @{ Name = 'v1.3.1'; Annotated = $true; On = 'head' }); Expect = @('v1.3.1') }
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
            $got = @($result.Text -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Sort-Object)
            $want = @($case.Expect | Sort-Object)
            if (($got -join '|') -ne ($want -join '|')) {
                $shownGot = if ($got.Count -eq 0) { '<none>' } else { $got -join ', ' }
                $shownWant = if ($want.Count -eq 0) { '<none>' } else { $want -join ', ' }
                $failures[$list.Name].Add("$($case.Name): got $shownGot, want $shownWant")
            }
        }
    }
    return $failures
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Stop-Harness 'git is not on PATH'
}

$tagArgs = Get-TagArguments
Write-Host "tag args from GitVersion.zig: git $($tagArgs -join ' ')"

$PRODUCT = 'GitVersion.zig'
$ArgumentLists = @(
    @{ Name = $PRODUCT; Arguments = $tagArgs }
    @{ Name = 'no --points-at (an ancestor tag joins)'; Arguments = [string[]]@('tag', '--list') }
    @{ Name = '--points-at HEAD~1 (the ancestor commit, not HEAD)'; Arguments = [string[]]@('tag', '--points-at', 'HEAD~1') }
)

try {
    $repo = New-FixtureRepo
    $failures = Invoke-CaseTable -Repo $repo -CaseTable $Cases -ArgumentLists $ArgumentLists

    $real = $failures[$PRODUCT]
    if ($real.Count -gt 0) {
        Write-Host "FAIL: the tag command does not report exactly the HEAD tags" -ForegroundColor Red
        foreach ($f in $real) { Write-Host "  $f" -ForegroundColor Red }
        exit 2
    }
    Write-Host "ok   $($Cases.Count) tag layouts report as expected"

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
