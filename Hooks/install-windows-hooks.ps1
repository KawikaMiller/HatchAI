<#
.SYNOPSIS
Wires HatchAI's hook into Claude Code on Windows.

.DESCRIPTION
The hook is how HatchAI learns what sessions are doing: Claude Code runs it on
session start, prompt submit, tool use, stop and session end, and it writes a
small status file per session that HatchAI reads. Without it wired into
settings.json nothing happens -- no error, just a buddy that never reacts.

Forked from Claude Buddy's tools/install-windows-hooks.ps1. The events, the
command line and the baked-in temp path are the source's; the merge lives in
hatchai-hooks-common.ps1, whose header lists what it does differently and why.

It MERGES, never overwrites. Everything already in settings.json stays --
model, permissions, status line, and every other tool's hooks, Claude Buddy's
included. Entries are recognised as HatchAI's by the script filename
HatchAIHook.ps1 in their command, stripped, and added fresh, so running this
twice leaves exactly one set.

Not ported from the source: WSL distro wiring (-Wsl and friends). HatchAI has
never been run against a WSL session, and wiring a Linux settings.json from
here is a larger surface than this app has any evidence about.

  .\install-windows-hooks.ps1                       # ~/.claude/settings.json
  .\install-windows-hooks.ps1 -Uninstall            # remove just HatchAI's entries
  .\install-windows-hooks.ps1 -ProfileDir .claude-work   # + a second account
  .\install-windows-hooks.ps1 -SettingsPath C:\scratch\settings.json -InstallDir C:\scratch\hook

.PARAMETER SettingsPath
The Claude Code settings.json to wire. Defaults to the real one.

.PARAMETER InstallDir
Where the hook script is copied, so the wiring keeps working if HatchAI is
moved or deleted. Defaults to %LOCALAPPDATA%\HatchAI.

.PARAMETER ProfileDir
Extra Claude Code config directories (a CLAUDE_CONFIG_DIR account), wired in
addition to -SettingsPath. A bare name is taken under %USERPROFILE%.

.PARAMETER TempDir
The temp root baked into each command. Defaults to this machine's temp path.
#>
[CmdletBinding()]
param(
    [switch] $Uninstall,
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'HatchAI'),
    [string] $SettingsPath = (Join-Path $env:USERPROFILE '.claude\settings.json'),
    [string[]] $ProfileDir = @(),
    [string] $TempDir = ''
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'hatchai-hooks-common.ps1')

# `-File` hands a string[] parameter one string however it is written, so a
# caller outside PowerShell passes several profiles as "a;b".
$ProfileDir = @($ProfileDir | ForEach-Object { "$_" -split ';' } | Where-Object { $_ })

$source = Join-Path $PSScriptRoot 'HatchAIHook.ps1'
$installed = Join-Path $InstallDir 'HatchAIHook.ps1'
$resolvedTempDir = Get-HatchAITempDir $TempDir

# Which Claude Code events drive which state. Notification carries a matcher
# because only some notifications mean "Claude needs you".
$wanted = @(
    @{ Event = 'SessionStart';     Matcher = $null;                  State = 'idle' },
    @{ Event = 'UserPromptSubmit'; Matcher = $null;                  State = 'generating' },
    @{ Event = 'PreToolUse';       Matcher = '.*';                   State = 'generating' },
    @{ Event = 'Stop';             Matcher = $null;                  State = 'idle' },
    @{ Event = 'SessionEnd';       Matcher = $null;                  State = 'ended' },
    @{ Event = 'Notification';     Matcher = 'permission_prompt';    State = 'waiting' },
    @{ Event = 'Notification';     Matcher = 'elicitation_dialog';   State = 'waiting' },
    @{ Event = 'Notification';     Matcher = 'elicitation_complete'; State = 'generating' }
) | ForEach-Object {
    $command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$installed`" -State $($_.State) -TempDir `"$resolvedTempDir`""
    @{ Event = $_.Event; Matcher = $_.Matcher; Handler = [ordered]@{ type = 'command'; command = $command } }
}

function Set-OneSettingsFile([string] $Path) {
    if ($Uninstall -and -not (Test-Path -LiteralPath $Path)) { return }  # nothing to remove

    Assert-InSandbox $Path
    $config = Read-HatchAIJson $Path
    $config = Merge-HatchAIHooks -Config $config -Wanted $wanted -Uninstall:$Uninstall
    Write-HatchAIJson $Path $config

    if ($Uninstall) { Write-Host "Removed HatchAI hooks from $Path" }
    else { Write-Host "Wired $($wanted.Count) hook entries into $Path" }
}

if (-not $Uninstall) {
    Install-HatchAIHookScript $source $installed
}

Set-OneSettingsFile $SettingsPath

foreach ($entry in $ProfileDir) {
    if (-not $entry) { continue }
    $extraPath = if ([System.IO.Path]::IsPathRooted($entry)) {
        Join-Path $entry 'settings.json'
    }
    else {
        Join-Path (Join-Path $env:USERPROFILE $entry) 'settings.json'
    }
    Write-Host "--- Additional Claude Code profile: $entry ---"
    Set-OneSettingsFile $extraPath
}

if ($Uninstall) {
    Write-Host 'The installed hook script was left in place; delete it if you want it gone.'
}
else {
    Write-Host 'Restart any running Claude Code sessions: hooks are read at session start.'
}
