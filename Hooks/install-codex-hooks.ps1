<#
.SYNOPSIS
Wires HatchAI's hook into Codex on Windows.

.DESCRIPTION
Forked from Claude Buddy's tools/install-codex-hooks.ps1, the sibling of
install-windows-hooks.ps1 for Codex. Three things differ from Claude Code, all
measured by Claude Buddy against a real Codex (its docs/codex-findings.md):

 1. The target is its own file, $CODEX_HOME\hooks.json, discovered
    automatically.
 2. Codex has no Notification event. PermissionRequest is the analogue; an
    event name Codex does not know is dropped silently.
 3. PermissionRequest is installed async, so the hook is structurally unable
    to deny the user's own approval.

It MERGES, never overwrites, by the same rule as the Claude Code installer
(hatchai-hooks-common.ps1): entries naming HatchAIHook.ps1 are ours, and
nothing else is touched.

Two changes from the source beyond the shared merge. The baked -TempDir is
trimmed of its trailing backslash, which the source's Codex installer forgot
(the Claude Code one did not), and which turns the argument into
`...\Temp"` with the rest of the command line swallowed. And the hook copy
lives in $CODEX_HOME\hatchai rather than claude-buddy.

.PARAMETER Uninstall
Remove just HatchAI's entries, leaving any other tool's hooks alone.
#>
param(
    [switch] $Uninstall,
    [string] $CodexHome = '',
    [string] $HooksPath = '',
    [string] $HookDir = '',
    [string] $TempDir = ''
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'hatchai-hooks-common.ps1')

if (-not $CodexHome) {
    $CodexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
}
if (-not $HookDir)   { $HookDir   = Join-Path $CodexHome 'hatchai' }
if (-not $HooksPath) { $HooksPath = Join-Path $CodexHome 'hooks.json' }

$source = Join-Path $PSScriptRoot 'HatchAIHook.ps1'
$installed = Join-Path $HookDir 'HatchAIHook.ps1'
$resolvedTempDir = Get-HatchAITempDir $TempDir

# PostToolUse has no counterpart in the Claude Code table: Codex fires
# PermissionRequest before deciding whether to ask, so a call that is then
# auto-approved would otherwise be left reading "waiting" until the turn ends.
$wanted = @(
    @{ Event = 'SessionStart';      Matcher = $null; State = 'idle';       Async = $false }
    @{ Event = 'UserPromptSubmit';  Matcher = $null; State = 'generating'; Async = $false }
    @{ Event = 'PreToolUse';        Matcher = '.*';  State = 'generating'; Async = $false }
    @{ Event = 'PermissionRequest'; Matcher = $null; State = 'waiting';    Async = $true  }
    @{ Event = 'PostToolUse';       Matcher = '.*';  State = 'generating'; Async = $false }
    @{ Event = 'Stop';              Matcher = $null; State = 'idle';       Async = $false }
    @{ Event = 'SessionEnd';        Matcher = $null; State = 'ended';      Async = $false }
) | ForEach-Object {
    $command = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$installed`" " +
               "-Agent codex -State $($_.State) -TempDir `"$resolvedTempDir`""

    # commandWindows as well as command: Codex accepts both, and one naming
    # only the POSIX form would do nothing here.
    $handler = [ordered]@{ type = 'command'; command = $command; commandWindows = $command }
    if ($_.Async) { $handler['async'] = $true }
    @{ Event = $_.Event; Matcher = $_.Matcher; Handler = $handler }
}

if ($Uninstall -and -not (Test-Path -LiteralPath $HooksPath)) {
    Write-Host "No $HooksPath, so nothing to remove."
    exit 0
}

if (-not $Uninstall) {
    Install-HatchAIHookScript $source $installed
}

Assert-InSandbox $HooksPath
$config = Read-HatchAIJson $HooksPath
$config = Merge-HatchAIHooks -Config $config -Wanted $wanted -Uninstall:$Uninstall
Write-HatchAIJson $HooksPath $config

if ($Uninstall) {
    Write-Host "Removed HatchAI hooks from $HooksPath"
    Write-Host "The installed hook script was left in place; delete $HookDir if you want it gone."
    exit 0
}

Write-Host "Wired $($wanted.Count) hook entries into $HooksPath"
Write-Host ''
# The step people get stuck on, and why this prints a paragraph rather than
# "done": Codex does not run a hook it has not been told to trust, and a
# hooks.json written by anything but Codex starts out untrusted.
Write-Host 'One more step, and nothing works without it:'
Write-Host ''
Write-Host '  Codex will not run a hook it has not been told to trust, and a hooks.json'
Write-Host '  written by anything other than Codex itself starts out untrusted. Start'
Write-Host '  Codex and accept the hook review it shows you, or run /hooks inside it'
Write-Host '  and trust the HatchAI entries.'
Write-Host ''
Write-Host '  Editing hooks.json later - including re-running this installer - changes'
Write-Host '  its hash and asks you again.'
Write-Host ''
Write-Host 'Then restart any running Codex sessions: hooks are read at session start.'
