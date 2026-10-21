#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$Wsl = Join-Path $env:WINDIR 'System32\wsl.exe'
if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
    $Wsl = Join-Path $env:WINDIR 'Sysnative\wsl.exe'
}
$Data = Join-Path $env:LOCALAPPDATA 'HP3052Scanner'
$Selection = Join-Path $Data 'ubuntu.json'

function Step([string]$Message) { Write-Host "`n$Message" -ForegroundColor Cyan }
function Fail([string]$Message) { throw $Message }
function AssertWindows([switch]$AllowMissingWsl) {
    if (-not [Environment]::Is64BitOperatingSystem -or [Environment]::OSVersion.Version.Build -lt 19041) {
        Fail 'Required: Windows 10 build 19041+ x64.'
    }
    if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') {
        Fail 'This package contains an amd64 Ubuntu image and requires an x64 PC.'
    }
    if (-not $AllowMissingWsl -and -not (Test-Path -LiteralPath $Wsl)) { Fail 'wsl.exe not found. Run INSTALL-UBUNTU.cmd to prepare WSL automatically.' }
}
function DecodeNativeOutput([byte[]]$Bytes) {
    if ($null -eq $Bytes -or $Bytes.Length -eq 0) { return '' }
    if ($Bytes.Length -ge 2 -and $Bytes[0] -eq 255 -and $Bytes[1] -eq 254) {
        return [Text.Encoding]::Unicode.GetString($Bytes, 2, $Bytes.Length - 2)
    }
    if ($Bytes.Length -ge 2 -and $Bytes[0] -eq 254 -and $Bytes[1] -eq 255) {
        return [Text.Encoding]::BigEndianUnicode.GetString($Bytes, 2, $Bytes.Length - 2)
    }
    # Older wsl.exe uses UTF-16LE without a BOM. Linux commands and WSL_UTF8
    # use UTF-8. Inspect raw bytes BEFORE PowerShell's native text conversion.
    $zeroEven = 0; $zeroOdd = 0
    for ($i = 0; $i -lt [Math]::Min($Bytes.Length, 1024); $i++) {
        if ($Bytes[$i] -eq 0) {
            if (($i % 2) -eq 0) { $zeroEven++ } else { $zeroOdd++ }
        }
    }
    if ($zeroOdd -gt 0 -and $zeroOdd -gt $zeroEven) { return [Text.Encoding]::Unicode.GetString($Bytes) }
    if ($zeroEven -gt 0 -and $zeroEven -gt $zeroOdd) { return [Text.Encoding]::BigEndianUnicode.GetString($Bytes) }
    try {
        $utf8 = New-Object Text.UTF8Encoding($false, $true)
        return $utf8.GetString($Bytes).TrimStart([char]0xFEFF)
    } catch {
        $oem = [Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage)
        return $oem.GetString($Bytes)
    }
}
function QuoteNativeArgument([string]$Value) {
    # WSL inbox parses management switches from the raw Windows command line.
    # Never quote simple switches ("--list" can become a Linux command).
    if ($Value -ne '' -and $Value -notmatch '[\s"]') { return $Value }
    # Quote only values that need it; escape embedded quotes/trailing backslashes.
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}
function WslCapture([string[]]$Parameters, [ValidateRange(1, 600)][int]$TimeoutSeconds = 45) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Wsl
    $start.Arguments = ($Parameters | ForEach-Object { QuoteNativeArgument $_ }) -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    $stdout = New-Object IO.MemoryStream
    $stderr = New-Object IO.MemoryStream
    try {
        if (-not $process.Start()) { Fail 'Cannot start wsl.exe.' }
        # Drain both pipes concurrently so stderr cannot deadlock stdout.
        $outTask = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $errTask = $process.StandardError.BaseStream.CopyToAsync($stderr)
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill(); $null = $process.WaitForExit(5000) } catch {}
            Fail "WSL check timed out after $TimeoutSeconds seconds. Restart WSL/Windows and retry."
        }
        $outTask.GetAwaiter().GetResult()
        $errTask.GetAwaiter().GetResult()
        $outputText = DecodeNativeOutput -Bytes $stdout.ToArray()
        $errorText = DecodeNativeOutput -Bytes $stderr.ToArray()
        $value = $outputText
        if ($errorText) { $value += "`n" + $errorText }
        return [pscustomobject]@{ Value = $value; Code = $process.ExitCode; Stdout = $outputText; Stderr = $errorText }
    } finally {
        $stdout.Dispose()
        $stderr.Dispose()
        $process.Dispose()
    }
}
function TestNoDistros($Result) {
    # Some Windows 10 versions return nonzero for an empty distribution list.
    # Accept only an explicit empty-list message corroborated by the user registry.
    if ($Result.Value -notmatch 'No installed distributions|has no installed distributions|Нет установленных дистрибутивов|не имеет установленных дистрибутивов') { return $false }
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss'
    if (Test-Path -LiteralPath $key) {
        $registered = @(Get-ChildItem -LiteralPath $key | ForEach-Object { Get-ItemProperty -LiteralPath $_.PSPath } | Where-Object { $_.DistributionName })
        if ($registered.Count -gt 0) { return $false }
    }
    return $true
}
function GetDistros {
    $r = WslCapture -Parameters @('--list', '--verbose')
    if (TestNoDistros $r) { Write-Host 'No WSL distributions installed. Continuing Ubuntu setup.'; return @() }
    if ($r.Code -ne 0) { Fail ("WSL did not return a usable distribution list. Details:`n" + $r.Value) }
    $found = @()
    foreach ($line in ($r.Stdout -split "`r?`n")) {
        # The state text is localized; only names and the numeric WSL version are used.
        if ($line -match '^\s*\*?\s*(\S.*?)\s{2,}.+?\s+([12])\s*$') {
            $found += [pscustomobject]@{ Name = $Matches[1].Trim(); WslVersion = [int]$Matches[2] }
        }
    }
    # Do not mistake unsupported output or a parsing failure for an empty installation.
    $quiet = WslCapture -Parameters @('--list', '--quiet')
    if ($quiet.Code -ne 0) { Fail ("Cannot verify the WSL distribution list.`n" + $quiet.Value) }
    $names = @($quiet.Stdout -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    foreach ($name in $names) {
        if (-not ($found | Where-Object { $_.Name -eq $name })) { Fail "Cannot determine WSL version for '$name'. No changes made." }
    }
    foreach ($entry in $found) {
        if ($entry.Name -notin $names) { Fail "Inconsistent WSL lists for '$($entry.Name)'. Retry without changing distributions." }
    }
    return $found
}
function GetUbuntuInfo($Entry) {
    $r = WslCapture -Parameters @('-d', $Entry.Name, '-u', 'root', '--exec', '/bin/cat', '/etc/os-release')
    if ($r.Code -ne 0) { Fail ("Cannot read /etc/os-release in '$($Entry.Name)'. No Ubuntu will be installed.`n" + $r.Value) }
    $idValue = ''; $versionValue = ''; $pretty = ''
    foreach ($line in ($r.Stdout -split "`r?`n")) {
        if ($line -match '^ID=(.*)$') { $idValue = $Matches[1].Trim('"', "'") }
        if ($line -match '^VERSION_ID=(.*)$') { $versionValue = $Matches[1].Trim('"', "'") }
        if ($line -match '^PRETTY_NAME=(.*)$') { $pretty = $Matches[1].Trim('"', "'") }
    }
    if (-not $idValue -or ($idValue -eq 'ubuntu' -and -not $versionValue)) { Fail "Incomplete /etc/os-release in '$($Entry.Name)'." }
    [pscustomobject]@{ Name = $Entry.Name; WslVersion = $Entry.WslVersion; Id = $idValue; Release = $versionValue; Pretty = $pretty }
}
function SelectUbuntu([string]$Requested, [switch]$AllowMissing) {
    $entries = @(GetDistros)
    if ($entries.Count) {
        Write-Host 'Registered WSL distributions:'
        foreach ($entry in $entries) { Write-Host ("  {0}: WSL{1}" -f $entry.Name, $entry.WslVersion) }
    }
    # Inspect actual releases, including distributions registered under custom names.
    $toInspect = $entries
    if ($Requested) {
        $toInspect = @($entries | Where-Object { $_.Name -eq $Requested })
        if ($toInspect.Count -ne 1) { Fail "Requested distribution '$Requested' is not registered. No changes made." }
    }
    $infos = @($toInspect | ForEach-Object { GetUbuntuInfo $_ })
    $ubuntus = @($infos | Where-Object { $_.Id -eq 'ubuntu' })
    foreach ($info in $ubuntus) { Write-Host ("Ubuntu found: {0} | {1} | WSL{2}" -f $info.Name, $info.Pretty, $info.WslVersion) }
    if ($Requested) {
        $match = @($infos | Where-Object { $_.Name -eq $Requested })
        if ($match.Count -ne 1) { Fail "Requested distribution '$Requested' is not registered. No changes made." }
        $chosen = $match[0]
    } else {
        $compatible = @($ubuntus | Where-Object { $_.Release -eq '22.04' -and $_.WslVersion -eq 1 })
        $preferred = @($compatible | Where-Object { $_.Name -eq 'Ubuntu-22.04' })
        if ($preferred.Count -eq 1) { $chosen = $preferred[0] }
        elseif ($compatible.Count -eq 1) { $chosen = $compatible[0] }
        elseif ($compatible.Count -gt 1) { Fail 'Multiple compatible Ubuntu distributions found. Run this PowerShell script with -Distro <name>.' }
        elseif ($ubuntus.Count -gt 0) { Fail 'Installed Ubuntu is incompatible. Required: Ubuntu 22.04 with WSL1. Existing distributions are preserved; no new Ubuntu installed.' }
        elseif ($AllowMissing) {
            if ($entries | Where-Object { $_.Name -eq 'Ubuntu-22.04' }) { Fail 'The name Ubuntu-22.04 is already occupied by another Linux distribution.' }
            return $null
        } else { Fail 'Ubuntu is not installed. Run INSTALL-UBUNTU.cmd first.' }
    }
    if ($chosen.Id -ne 'ubuntu' -or $chosen.Release -ne '22.04' -or $chosen.WslVersion -ne 1) {
        Fail "Incompatible distribution '$($chosen.Name)': $($chosen.Pretty), WSL$($chosen.WslVersion). Required: Ubuntu 22.04 with WSL1."
    }
    return $chosen
}
function RunLinux([string[]]$LinuxArgs) {
    & $Wsl -d $Distro -u root --exec @LinuxArgs
    if ($LASTEXITCODE -ne 0) { Fail ('Linux command failed (exit ' + $LASTEXITCODE + '): ' + ($LinuxArgs -join ' ')) }
}
