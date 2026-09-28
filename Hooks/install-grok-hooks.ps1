<#
.SYNOPSIS
Wires HatchAI's hook into Grok Build on Windows.

.DESCRIPTION
Forked from Claude Buddy's tools/install-grok-hooks.ps1. Grok discovers global
hooks from $GROK_HOME\hooks\*.json and always trusts them, so HatchAI gets a
file of its own there -- hooks\hatchai.json, beside Claude Buddy's
claude-buddy.json if that exists -- and nothing anybody else wrote is ever
opened, let alone merged. Re-running rewrites only that one file.

One addition over the source: -TempDir is baked into each command, as the
Claude Code and Codex installers already do, rather than left for the hook to
work out from an environment that may not have TEMP.

Grok also reads Claude Code's settings.json as a compatibility layer, so a
machine wired for both will run the hook twice per Grok event; the hook sees
Grok's environment and labels both writes as Grok. That is Claude Buddy's
behaviour too.

.PARAMETER Uninstall
Remove just HatchAI's hooks file.
#>
param(
    [switch] $Uninstall,
    [string] $GrokHome = '',
    [string] $HookDir = '',
    [string] $HooksFile = '',
    [string] $TempDir = ''
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'hatchai-hooks-common.ps1')

if (-not $GrokHome) {
    $GrokHome = if ($env:GROK_HOME) { $env:GROK_HOME } else { Join-Path $env:USERPROFILE '.grok' }
}
if (-not $HookDir)   { $HookDir   = Join-Path $GrokHome 'hatchai' }
if (-not $HooksFile) { $HooksFile = Join-Path $GrokHome 'hooks\hatchai.json' }

$source = Join-Path $PSScriptRoot 'HatchAIHook.ps1'
$installed = Join-Path $HookDir 'HatchAIHook.ps1'
$resolvedTempDir = Get-HatchAITempDir $TempDir

if ($Uninstall) {
    if (Test-Path -LiteralPath $HooksFile) {
        Assert-InSandbox $HooksFile
        Remove-Item -LiteralPath $HooksFile -Force
    }
    Write-Host "Removed HatchAI hooks from $HooksFile."
    exit 0
}

Install-HatchAIHookScript $source $installed

function New-Handler([string] $State) {
    [ordered]@{
        type = 'command'
        command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$installed`" -Agent grok -State $State -TempDir `"$resolvedTempDir`""
        # Grok's default for observe hooks is 5 seconds.
        timeout = 15
    }
}

$config = [ordered]@{
    hooks = [ordered]@{
        SessionStart     = @([ordered]@{ hooks = @(New-Handler 'idle') })
        UserPromptSubmit = @([ordered]@{ hooks = @(New-Handler 'generating') })
        PreToolUse       = @([ordered]@{ matcher = '.*'; hooks = @(New-Handler 'generating') })
        Notification     = @([ordered]@{ matcher = 'permission_prompt'; hooks = @(New-Handler 'waiting') })
        Stop             = @([ordered]@{ hooks = @(New-Handler 'idle') })
        SessionEnd       = @([ordered]@{ hooks = @(New-Handler 'ended') })
    }
}

Write-HatchAIJson $HooksFile $config
Write-Host "Wired HatchAI hooks into $HooksFile"
Write-Host 'Restart any running Grok sessions: hooks are read at session start.'
