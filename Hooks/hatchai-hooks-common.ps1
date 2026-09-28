# Shared by HatchAI's Windows hook installers: read a JSON config, take
# HatchAI's own hook entries out of it, put fresh ones in, write it back.
# Dot-sourced, never run on its own.
#
# Forked from the merge inside Claude Buddy's install-windows-hooks.ps1 and
# install-codex-hooks.ps1, which were one function each and near copies of
# each other. The shape is theirs -- strip our own entries wherever they
# appear, then append fresh ones, so a re-run converges instead of
# duplicating -- and so is the rule that makes it safe to share a file with
# other tools: an entry is ours if and only if its command names our hook
# script by filename. Claude Buddy's entries name ClaudeBuddyHook.ps1 and
# ours name HatchAIHook.ps1, so neither app's installer can ever match, strip
# or rewrite the other's.
#
# Four things are deliberately different from the source, each because the
# source could change something in a file that was not its own:
#
#  1. Arrays survive the round trip. The source's ConvertTo-HashtableDeep
#     ended in `return @(...)`, which PowerShell unrolls: a one-element array
#     came back as a bare value and an empty one as nothing. Measured against
#     the source under both engines -- {"allow":["Bash(ls)"],"deny":[]} came
#     back as {"allow":"Bash(ls)","deny":{}} from 5.1 and ...,"deny":null}
#     from pwsh. Its own hooks arrays survived only because the merge
#     reassigned every one of them; anything else in the file (a permissions
#     allowlist, most obviously) did not. `return ,$array` stops the unroll.
#  2. Key order survives too: [ordered] rather than @{}, so the file comes
#     back in the order its owner wrote it rather than hash order.
#  3. A group or event that held none of our entries is left exactly as it
#     was. The source dropped any group whose hook list was empty *after*
#     filtering, which includes one that was empty before and belonged to
#     somebody else.
#  4. -Depth 100 (the most 5.1 allows) rather than 20. ConvertTo-Json does
#     not fail past its depth, it silently writes the type name instead of
#     the object.
#
# And one addition: HATCHAI_INSTALLER_SANDBOX. When set, nothing is written
# outside that directory -- the installers throw before touching a file. The
# test suites set it, so a test that forgot to pass a scratch path fails
# instead of editing the developer's real settings.json.

$script:HatchAIMarker = 'HatchAIHook.ps1'

# PowerShell 5.1 has no ConvertFrom-Json -AsHashtable, and the powershell.exe
# Claude Code invokes on Windows is 5.1, so the object graph is converted by
# hand.
function ConvertTo-OrderedDeep($value) {
    if ($null -eq $value) { return $null }

    if ($value -is [System.Collections.IDictionary]) {
        $copy = [ordered]@{}
        foreach ($key in @($value.Keys)) { $copy[$key] = ConvertTo-OrderedDeep $value[$key] }
        return $copy
    }

    if ($value -is [System.Management.Automation.PSCustomObject]) {
        $copy = [ordered]@{}
        foreach ($property in $value.PSObject.Properties) {
            $copy[$property.Name] = ConvertTo-OrderedDeep $property.Value
        }
        return $copy
    }

    # Strings are enumerable; check them before the array branch.
    if ($value -is [string]) { return $value }

    if ($value -is [System.Collections.IEnumerable]) {
        $items = New-Object System.Collections.ArrayList
        foreach ($item in $value) { [void]$items.Add((ConvertTo-OrderedDeep $item)) }
        # The comma is the fix described in the header: without it an empty
        # array returns nothing and a one-element array returns its element.
        return ,$items.ToArray()
    }

    return $value
}

# Whether one hook handler is ours. `command` for Claude Code and Grok;
# Codex also has `commandWindows`, which the source's Codex installer checked
# too.
function Test-HatchAIHandler($handler) {
    if (-not ($handler -is [System.Collections.IDictionary])) { return $false }
    foreach ($field in 'command', 'commandWindows') {
        if ($handler.Contains($field) -and ("$($handler[$field])" -like "*$script:HatchAIMarker*")) { return $true }
    }
    return $false
}

# The merge itself, over an already-parsed config. Pure: no disk, no
# environment. $Wanted is a list of @{ Event; Matcher; Handler } where
# Handler is the complete hook object to add. Returns the config.
function Merge-HatchAIHooks {
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Config,
        [object[]] $Wanted = @(),
        [switch] $Uninstall
    )

    $hadHooksKey = $Config.Contains('hooks')
    if (-not $hadHooksKey -or $null -eq $Config['hooks']) {
        $Config['hooks'] = [ordered]@{}
    }

    $hooks = $Config['hooks']
    if (-not ($hooks -is [System.Collections.IDictionary])) {
        throw "The file's `"hooks`" value is not an object, so it is not safe to edit. Nothing was changed."
    }

    $removedAny = $false

    # $event is an automatic variable in PowerShell; a loop variable by that
    # name would shadow it.
    foreach ($eventName in @($hooks.Keys)) {
        $groups = $hooks[$eventName]
        if (-not ($groups -is [System.Array])) { continue }  # not a list: not ours to judge

        $kept = New-Object System.Collections.ArrayList
        $changed = $false

        foreach ($group in $groups) {
            if (-not ($group -is [System.Collections.IDictionary]) -or -not ($group['hooks'] -is [System.Array])) {
                [void]$kept.Add($group)
                continue
            }

            $inner = New-Object System.Collections.ArrayList
            $ours = 0
            foreach ($handler in $group['hooks']) {
                if (Test-HatchAIHandler $handler) { $ours++ } else { [void]$inner.Add($handler) }
            }

            if ($ours -eq 0) { [void]$kept.Add($group); continue }

            $changed = $true
            if ($inner.Count -gt 0) {
                $group['hooks'] = $inner.ToArray()
                [void]$kept.Add($group)
            }
        }

        if ($changed) {
            $removedAny = $true
            if ($kept.Count -gt 0) { $hooks[$eventName] = $kept.ToArray() } else { $hooks.Remove($eventName) }
        }
    }

    if (-not $Uninstall) {
        foreach ($entry in $Wanted) {
            $group = [ordered]@{}
            if ($entry.Matcher) { $group['matcher'] = $entry.Matcher }
            $group['hooks'] = @($entry.Handler)

            # A value somebody wrote as a bare object rather than a list is
            # kept, as the first item of the list ours is appended to.
            $existing = @()
            if ($hooks.Contains($entry.Event) -and $null -ne $hooks[$entry.Event]) {
                $existing = @($hooks[$entry.Event])
            }
            $hooks[$entry.Event] = @($existing) + @($group)
        }
    }

    # An empty hooks object this run emptied, or invented, is noise; one the
    # owner wrote as {} stays.
    if ($hooks.Count -eq 0 -and ($removedAny -or -not $hadHooksKey)) {
        $Config.Remove('hooks')
    }

    return $Config
}

function Assert-InSandbox([string] $Path) {
    $sandbox = $env:HATCHAI_INSTALLER_SANDBOX
    if ([string]::IsNullOrWhiteSpace($sandbox)) { return }

    $full = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetFullPath($sandbox).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "HATCHAI_INSTALLER_SANDBOX is set to '$sandbox' and '$full' is outside it. Refusing to write."
    }
}

# Reads a JSON config into an ordered graph. An absent or blank file is an
# empty object, which is normal on a fresh install.
function Read-HatchAIJson([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return [ordered]@{} }

    $json = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($json)) { return [ordered]@{} }

    $parsed = ConvertTo-OrderedDeep ($json | ConvertFrom-Json)
    if (-not ($parsed -is [System.Collections.IDictionary])) {
        throw "$Path does not hold a JSON object, so it is not safe to edit. Nothing was changed."
    }
    return $parsed
}

# Writes a config back: a backup of what was there, then the new text to a
# temp file beside it, parsed back to prove it is JSON, then swapped in. An
# interrupted run leaves the old file or the new one, never half of either.
# UTF-8 without a BOM, because System.Text.Json -- which Claude Code and
# HatchAI both use -- rejects a leading BOM, and 5.1's Set-Content adds one.
function Write-HatchAIJson([string] $Path, [System.Collections.IDictionary] $Config) {
    Assert-InSandbox $Path

    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $out = $Config | ConvertTo-Json -Depth 100

    # A re-run that changes nothing writes nothing -- not even a backup, which
    # would otherwise overwrite the one the first run made. For Codex this is
    # more than tidiness: it hashes hooks.json and asks for trust again when
    # the hash changes.
    if ((Test-Path -LiteralPath $Path) -and
        ([System.IO.File]::ReadAllText($Path) -ceq $out)) {
        Write-Host "$Path already has these hooks; left unchanged."
        return
    }

    if (Test-Path -LiteralPath $Path) {
        $backup = "$Path.hatchai-backup"
        Assert-InSandbox $backup
        Copy-Item -LiteralPath $Path -Destination $backup -Force
        Write-Host "Backed up $Path to $backup"
    }

    $tmp = "$Path.hatchai-tmp"
    Assert-InSandbox $tmp
    [System.IO.File]::WriteAllText($tmp, $out, (New-Object System.Text.UTF8Encoding($false)))

    try {
        $null = Get-Content -LiteralPath $tmp -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        throw "Refusing to write: the merged file did not parse back as JSON. $Path is unchanged."
    }

    if (Test-Path -LiteralPath $Path) {
        [System.IO.File]::Replace($tmp, $Path, [NullString]::Value)
    }
    else {
        [System.IO.File]::Move($tmp, $Path)
    }
}

# Copies the hook script to where the wired commands will point.
function Install-HatchAIHookScript([string] $Source, [string] $Destination) {
    if (-not (Test-Path -LiteralPath $Source)) {
        throw "Can't find HatchAIHook.ps1 next to the installer ($Source)."
    }
    Assert-InSandbox $Destination
    New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    Write-Host "Hook installed: $Destination"
}

# The temp root a hook should write under, computed here in the installer's
# own full environment and baked into every command as a literal, for the
# reason Claude Buddy's installer gives: a hook launched through an interop
# shell cannot be trusted to have TEMP set. TrimEnd matters -- a trailing
# backslash before a closing quote reads as an escaped quote, and the
# argument swallows the rest of the command line. (Claude Buddy's Codex
# installer omits the trim; measured, its -TempDir "...\Temp\" arrives in the
# script as ...\Temp" with nothing after it parsed.)
function Get-HatchAITempDir([string] $Override) {
    if ($Override) { return $Override.TrimEnd('\') }
    return ([System.IO.Path]::GetTempPath()).TrimEnd('\')
}
