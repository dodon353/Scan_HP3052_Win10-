#requires -Version 5.1
param([string]$Distro, [switch]$EnableFeatureOnly)
. (Join-Path $PSScriptRoot 'Wsl-Common.ps1')
New-Item -ItemType Directory -Force -Path $Data | Out-Null
$Log = Join-Path $Data 'install-ubuntu.log'
$ExitCode = 0
try { Start-Transcript -Path $Log -Append | Out-Null } catch {}
function IsAdmin {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function RepairWslExecutable {
    # Only used when the Windows feature is enabled but its launcher is absent.
    # Restore Windows component files using servicing tools, never a third-party EXE.
    Step 'Restoring missing Windows WSL files. Windows Update access may be required...'
    $repair = Repair-WindowsImage -Online -RestoreHealth -NoRestart
    if ($repair.RestartNeeded) { return 3010 }
    $systemDirectory = Split-Path -Parent $Wsl
    $sfc = Join-Path $systemDirectory 'sfc.exe'
    if (-not (Test-Path -LiteralPath $sfc)) { Fail 'Windows System File Checker (sfc.exe) was not found. Cannot restore wsl.exe.' }
    # SFC expects the real system-file path even when this is a 32-bit process.
    $scanPath = Join-Path $env:WINDIR 'System32\wsl.exe'
    $scanArgument = QuoteNativeArgument ('/scanfile=' + $scanPath)
    Step 'Restoring wsl.exe with Windows System File Checker...'
    $scan = Start-Process -FilePath $sfc -ArgumentList $scanArgument -Wait -NoNewWindow -PassThru
    if ($scan.ExitCode -eq 3010) { return 3010 }
    if (-not (Test-Path -LiteralPath $Wsl)) {
        Fail "Windows could not restore wsl.exe (SFC exit $($scan.ExitCode)). Review DISM/CBS logs; Ubuntu import was not started."
    }
    return 0
}
function EnableFeature {
    if (-not (IsAdmin)) { Fail 'Administrator permissions required to enable WSL.' }
    $state = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Windows-Subsystem-Linux
    if ($state.State -eq 'EnablePending' -or $state.State -eq 'DisablePending') { return 3010 }
    if ($state.State -ne 'Enabled') {
        Step 'Installing/enabling the Windows Subsystem for Linux component...'
        $result = Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Windows-Subsystem-Linux -All -NoRestart
        if ($result.RestartNeeded) { return 3010 }
        $state = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Windows-Subsystem-Linux
        if ($state.State -eq 'EnablePending') { return 3010 }
        if ($state.State -ne 'Enabled') { Fail "WSL Windows feature is not ready (state: $($state.State))." }
    } else {
        Write-Host 'WSL Windows feature already enabled.'
    }
    if (-not (Test-Path -LiteralPath $Wsl)) { return (RepairWslExecutable) }
    return 0
}
function EnsureFeature([switch]$Force) {
    # A missing launcher must not be invoked before enabling/restoring the component.
    if (-not $Force -and (Test-Path -LiteralPath $Wsl)) {
        try {
            $check = WslCapture -Parameters @('--list', '--verbose')
            if ($check.Code -eq 0 -or (TestNoDistros $check)) { return 0 }
        } catch {
            Write-Host ('WSL probe failed; checking its Windows feature. ' + $_.Exception.Message) -ForegroundColor Yellow
        }
    }
    Step 'Checking/installing Windows WSL feature (administrator access may be requested)...'
    if (IsAdmin) { return (EnableFeature) }
    $script = $PSCommandPath.Replace("'", "''")
    $cmd = "& '$script' -EnableFeatureOnly; exit `$LASTEXITCODE"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmd))
    # Use Windows' native PowerShell, including from a 32-bit parent process.
    $powershell = Join-Path (Split-Path -Parent $Wsl) 'WindowsPowerShell\v1.0\powershell.exe'
    try {
        $child = Start-Process -FilePath $powershell -Verb RunAs -Wait -PassThru -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded)
    } catch {
        Fail ('WSL preparation needs administrator access. The elevated process could not start: ' + $_.Exception.Message)
    }
    return $child.ExitCode
}

function NormalizeImportPath([string]$Path) {
    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    if ($expanded.StartsWith('\\?\UNC\', [StringComparison]::OrdinalIgnoreCase)) {
        $expanded = '\\' + $expanded.Substring(8)
    } elseif ($expanded.StartsWith('\\?\', [StringComparison]::OrdinalIgnoreCase)) {
        $expanded = $expanded.Substring(4)
    }
    return [IO.Path]::GetFullPath($expanded).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}
function RemoveImportTreeNoFollow([string]$Path) {
    # Do not traverse Linux symlinks or Windows junctions, including on PS 5.1.
    $attributes = [IO.File]::GetAttributes($Path)
    $directory = ($attributes -band [IO.FileAttributes]::Directory) -ne 0
    $link = ($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    if ($directory -and -not $link) {
        foreach ($child in [IO.Directory]::GetFileSystemEntries($Path)) {
            RemoveImportTreeNoFollow -Path $child
        }
    }
    if (-not $link -and ($attributes -band [IO.FileAttributes]::ReadOnly)) {
        [IO.File]::SetAttributes($Path, ($attributes -band (-bnot [IO.FileAttributes]::ReadOnly)))
    }
    if ($directory) { [IO.Directory]::Delete($Path, $false) }
    else { [IO.File]::Delete($Path) }
}
function PrepareUbuntuImportDirectory {
    # This function only handles the installer's fixed directory, never a caller path.
    $target = NormalizeImportPath (Join-Path $Data 'WSL\Ubuntu-22.04')
    $entries = @(GetDistros)
    if ($entries | Where-Object { $_.Name -eq 'Ubuntu-22.04' }) {
        Fail 'Ubuntu-22.04 was registered during setup. No cleanup performed. Run INSTALL-UBUNTU.cmd again.'
    }
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss'
    if (Test-Path -LiteralPath $key) {
        foreach ($entry in @(Get-ChildItem -LiteralPath $key)) {
            $info = Get-ItemProperty -LiteralPath $entry.PSPath
            if (-not $info.DistributionName) { continue }
            if (-not $info.BasePath) {
                Fail "Cannot verify the registered directory for '$($info.DistributionName)'. No cleanup performed."
            }
            $registered = NormalizeImportPath $info.BasePath
            $separator = [IO.Path]::DirectorySeparatorChar
            if ($target.Equals($registered, [StringComparison]::OrdinalIgnoreCase) -or
                $target.StartsWith($registered + $separator, [StringComparison]::OrdinalIgnoreCase) -or
                $registered.StartsWith($target + $separator, [StringComparison]::OrdinalIgnoreCase)) {
                Fail "Import directory overlaps registered distribution '$($info.DistributionName)'. No cleanup performed."
            }
        }
    }
    # A substituted parent directory must never redirect cleanup elsewhere.
    $current = $target
    while ($current) {
        try {
            $attributes = [IO.File]::GetAttributes($current)
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) {
                Fail "Import directory or parent is a link/junction: $current. No cleanup performed."
            }
        } catch [IO.FileNotFoundException] {
        } catch [IO.DirectoryNotFoundException] {
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
    if (Test-Path -LiteralPath $target) {
        if (-not ([IO.File]::GetAttributes($target) -band [IO.FileAttributes]::Directory)) {
            Fail "Import directory path is occupied by a file: $target."
        }
        Step "Removing leftover Ubuntu installation permanently: $target"
        try { RemoveImportTreeNoFollow -Path $target }
        catch { Fail ("Could not clean the old Ubuntu directory. Import was not started. Close programs using it and retry. Details: " + $_.Exception.Message) }
    }
    [IO.Directory]::CreateDirectory($target) | Out-Null
    return $target
}

try {
    if ($EnableFeatureOnly) {
        $ExitCode = EnableFeature
        if ($ExitCode -eq 3010) { Write-Host 'Restart Windows, then run INSTALL-UBUNTU.cmd again.' -ForegroundColor Yellow }
    } else {
        Step 'Ubuntu setup only: Ubuntu 22.04 / WSL1'
        AssertWindows -AllowMissingWsl
        $featureCode = EnsureFeature
        if ($featureCode -eq 3010) {
            $ExitCode = 3010
            Write-Host 'Restart Windows, then run INSTALL-UBUNTU.cmd again.' -ForegroundColor Yellow
        } elseif ($featureCode -ne 0) {
            Fail "WSL activation failed (exit $featureCode). Review the administrator window."
        } else {
            AssertWindows
            $ubuntu = SelectUbuntu -Requested $Distro -AllowMissing
            if ($null -eq $ubuntu) {
                $featureCode = EnsureFeature -Force
                if ($featureCode -eq 3010) {
                    Write-Host 'Restart Windows, then run INSTALL-UBUNTU.cmd again.' -ForegroundColor Yellow
                    $ExitCode = 3010
                    exit $ExitCode
                }
                if ($featureCode -ne 0) { Fail "WSL1 activation failed (exit $featureCode)." }
                Write-Host 'Ubuntu is not installed. Installing Ubuntu 22.04 with WSL1.'
                $Distro = 'Ubuntu-22.04'
                $Image = Join-Path $Data 'ubuntu-jammy-wsl-amd64-wsl.rootfs.tar.gz'
                $ImageUrl = 'https://cloud-images.ubuntu.com/wsl/releases/jammy/current/ubuntu-jammy-wsl-amd64-wsl.rootfs.tar.gz'
                $SumUrl = 'https://cloud-images.ubuntu.com/wsl/releases/jammy/current/SHA256SUMS'
                . (Join-Path $PSScriptRoot 'Download-Ubuntu.ps1')
                DownloadCheckedRootfs
                $WslLocation = PrepareUbuntuImportDirectory
                Step 'Importing Ubuntu 22.04 as WSL1...'
                & $Wsl --import $Distro $WslLocation $Image --version 1
                if ($LASTEXITCODE -ne 0) { Fail "Ubuntu import failed (exit $LASTEXITCODE). Run INSTALL-UBUNTU.cmd again; unregistered leftover files will be cleaned before the next import." }
                $ubuntu = SelectUbuntu -Requested $Distro
                Write-Host 'Ubuntu installed and version verified.' -ForegroundColor Green
            } else {
                Write-Host 'Compatible Ubuntu is already installed. Download and installation skipped.' -ForegroundColor Green
            }
            $selectionData = [ordered]@{ distro = $ubuntu.Name; release = $ubuntu.Release; wslVersion = $ubuntu.WslVersion }
            [IO.File]::WriteAllText($Selection, ($selectionData | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
            Write-Host "Ready: $($ubuntu.Name), Ubuntu $($ubuntu.Release), WSL$($ubuntu.WslVersion)."
            Write-Host 'Ubuntu step finished. Next: run INSTALL.cmd to configure scanner packages, printer IP and shortcuts.'
        }
    }
} catch {
    Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host ('Location: ' + $_.ScriptStackTrace) -ForegroundColor Yellow
    Write-Host "Log: $Log" -ForegroundColor Yellow
    $ExitCode = 1
} finally {
    try { Stop-Transcript | Out-Null } catch {}
    if ($EnableFeatureOnly) { Read-Host 'Press Enter to close administrator window' | Out-Null }
}
exit $ExitCode
