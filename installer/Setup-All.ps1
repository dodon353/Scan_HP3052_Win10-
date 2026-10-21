#requires -Version 5.1
param([string]$Distro)
. (Join-Path $PSScriptRoot 'Wsl-Common.ps1')
$Root = Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Force -Path $Data | Out-Null
$Log = Join-Path $Data 'install.log'
$ExitCode = 0
try { Start-Transcript -Path $Log -Append | Out-Null } catch {}
function InstallLinux {
    Step 'Updating Ubuntu package lists...'
    RunLinux -LinuxArgs @('apt-get', 'update')
    Step 'Installing HPLIP, SANE and D-Bus...'
    RunLinux -LinuxArgs @('env', 'DEBIAN_FRONTEND=noninteractive', 'apt-get', 'install', '-y', '--no-install-recommends', 'hplip', 'libsane-hpaio', 'sane-utils', 'dbus')
    # Separate commands avoid fragile nested quoting through Windows PowerShell 5.1.
    RunLinux -LinuxArgs @('/usr/bin/which', 'hp-makeuri', 'scanimage')
    foreach ($package in @('hplip', 'libsane-hpaio', 'sane-utils', 'dbus')) {
        $r = WslCapture -Parameters @('-d', $Distro, '-u', 'root', '--exec', 'dpkg-query', '-s', $package)
        if ($r.Code -ne 0 -or $r.Value -notmatch '(?m)^Status: install ok installed\r?$') { Fail "Scanner package is not ready: $package`n$($r.Value)" }
    }
    RunLinux -LinuxArgs @('/bin/sh', '-c', 'service dbus start >/dev/null 2>&1 || true')
    Write-Host 'Scanner packages ready. No scan attempted.' -ForegroundColor Green
}
function CreateShortcuts([string]$Exe) {
    $shell = New-Object -ComObject WScript.Shell
    $destinations = @((Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\HP 3052 Scanner.lnk'), (Join-Path ([Environment]::GetFolderPath('Desktop')) 'HP 3052 Scanner.lnk'))
    foreach ($dest in $destinations) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
        $sc = $shell.CreateShortcut($dest)
        $sc.TargetPath = $Exe
        $sc.WorkingDirectory = Split-Path $Exe
        $sc.IconLocation = "$Exe,0"
        $sc.Save()
    }
}
try {
    Step 'HP 3052 Scanner setup (the Windows app is already built)'
    AssertWindows
    $exe = Join-Path $Root 'app\HP3052Scanner.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        Fail 'app\HP3052Scanner.exe is missing. Extract the entire program archive to a permanent folder, then run INSTALL.cmd again.'
    }
    if (-not $Distro -and (Test-Path -LiteralPath $Selection)) {
        try {
            $saved = Get-Content -LiteralPath $Selection -Raw -Encoding UTF8 | ConvertFrom-Json
            $Distro = [string]$saved.distro
            if ([string]::IsNullOrWhiteSpace($Distro)) { Fail 'Missing distro name in Ubuntu selection.' }
        } catch { Fail 'Ubuntu selection file is invalid. Run INSTALL-UBUNTU.cmd to verify Ubuntu and repair the selection file.' }
    }
    $ubuntu = SelectUbuntu -Requested $Distro
    $Distro = $ubuntu.Name
    InstallLinux
    Step 'Enter printer IPv4 address (no network search or test scan).'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Save-Scanner-IP.ps1') -Distro $Distro
    if ($LASTEXITCODE -ne 0) { Fail 'Printer address was not saved.' }
    CreateShortcuts $exe
    Step 'Setup completed. Use the HP 3052 Scanner shortcut.'
} catch {
    Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host ('Location: ' + $_.ScriptStackTrace) -ForegroundColor Yellow
    Write-Host "Log: $Log" -ForegroundColor Yellow
    $ExitCode = 1
} finally {
    try { Stop-Transcript | Out-Null } catch {}
}
exit $ExitCode
