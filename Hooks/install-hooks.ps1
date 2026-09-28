<#
.SYNOPSIS
Wires HatchAI's hook into every agent CLI on this machine.

.DESCRIPTION
The one thing an install should run, and what HatchAI's own Settings button
runs. Forked from Claude Buddy's tools/install-hooks.ps1: a CLI that is not
installed is skipped and said so rather than treated as a failure, and each
CLI's own installer does the work.

Every one of them merges rather than overwrites, and recognises only its own
entries (by the filename HatchAIHook.ps1), so running this on a machine that
also has Claude Buddy's hooks leaves those exactly where they are, and running
it twice changes nothing the second time.

Unlike the source, this forwards the path options to the sub-installer that
understands each one, so the whole run can be pointed at scratch paths --
which is how the tests drive it, and the only way anyone should try it
without meaning to change their real configuration.

The last lines of output are "SUMMARY <CLI>: <outcome>", one per CLI, for the
app to show.

.PARAMETER ProfileDirs
Extra Claude Code config directories, separated by ';'.
#>
param(
    [switch] $Uninstall,
    [string] $SettingsPath = '',
    [string] $ProfileDirs = '',
    [string] $InstallDir = '',
    [string] $CodexHome = '',
    [string] $GrokHome = '',
    [string] $TempDir = ''
)

$ErrorActionPreference = 'Continue'

$here = $PSScriptRoot

function Test-ClaudeCode {
    $dir = if ($SettingsPath) { Split-Path -Parent $SettingsPath } else { Join-Path $env:USERPROFILE '.claude' }
    if (Test-Path -LiteralPath $dir) { return $true }
    return [bool](Get-Command claude -ErrorAction SilentlyContinue)
}

function Resolve-CodexHome {
    if ($CodexHome) { return $CodexHome }
    if ($env:CODEX_HOME) { return $env:CODEX_HOME }
    return Join-Path $env:USERPROFILE '.codex'
}

function Resolve-GrokHome {
    if ($GrokHome) { return $GrokHome }
    if ($env:GROK_HOME) { return $env:GROK_HOME }
    return Join-Path $env:USERPROFILE '.grok'
}

function Test-Codex {
    if (Test-Path -LiteralPath (Resolve-CodexHome)) { return $true }
    return [bool](Get-Command codex -ErrorAction SilentlyContinue)
}

function Test-Grok {
    if (Test-Path -LiteralPath (Resolve-GrokHome)) { return $true }
    return [bool](Get-Command grok -ErrorAction SilentlyContinue)
}

$summary = New-Object System.Collections.ArrayList
$failed = $false

function Invoke-One([string] $Label, [string] $Script, [hashtable] $Arguments) {
    $path = Join-Path $here $Script
    if (-not (Test-Path -LiteralPath $path)) {
        [void]$script:summary.Add("${Label}: failed (couldn't find $Script)")
        $script:failed = $true
        return
    }

    Write-Host "=== $Label"
    if ($Uninstall) { $Arguments['Uninstall'] = $true }

    try {
        $global:LASTEXITCODE = 0
        & $path @Arguments
        if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
            [void]$script:summary.Add("${Label}: failed (exit $LASTEXITCODE)")
            $script:failed = $true
        }
        else {
            [void]$script:summary.Add($(if ($Uninstall) { "${Label}: removed" } else { "${Label}: wired" }))
        }
    }
    catch {
        [void]$script:summary.Add("${Label}: failed ($($_.Exception.Message))")
        $script:failed = $true
    }
    Write-Host ''
}

if (Test-ClaudeCode) {
    $a = @{}
    if ($SettingsPath) { $a['SettingsPath'] = $SettingsPath }
    if ($InstallDir) { $a['InstallDir'] = $InstallDir }
    if ($TempDir) { $a['TempDir'] = $TempDir }
    if ($ProfileDirs) { $a['ProfileDir'] = @($ProfileDirs -split ';' | Where-Object { $_ }) }
    Invoke-One 'Claude Code' 'install-windows-hooks.ps1' $a
}
else { [void]$summary.Add('Claude Code: not installed, skipped') }

if (Test-Codex) {
    $a = @{ CodexHome = (Resolve-CodexHome) }
    if ($TempDir) { $a['TempDir'] = $TempDir }
    Invoke-One 'Codex' 'install-codex-hooks.ps1' $a
}
else { [void]$summary.Add('Codex: not installed, skipped') }

if (Test-Grok) {
    $a = @{ GrokHome = (Resolve-GrokHome) }
    if ($TempDir) { $a['TempDir'] = $TempDir }
    Invoke-One 'Grok Build' 'install-grok-hooks.ps1' $a
}
else { [void]$summary.Add('Grok Build: not installed, skipped') }

foreach ($line in $summary) { Write-Host "SUMMARY $line" }

if ($failed) { exit 1 }
exit 0
