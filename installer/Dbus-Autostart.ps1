#requires -Version 5.1
param(
    [ValidateSet('Enable','Disable','Check')][string]$Mode = 'Enable',
    [string]$Distro
)
. (Join-Path $PSScriptRoot 'Wsl-Common.ps1')

function EnsureDbus([string]$Distribution) {
    if ([string]::IsNullOrWhiteSpace($Distribution)) { Fail 'Missing Ubuntu distribution name.' }
    $scriptText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Ensure-Dbus.sh')).Replace("`r", '')
    $result = WslCapture -Parameters @('-d', $Distribution, '-u', 'root', '--exec', '/bin/sh', '-c', $scriptText) -TimeoutSeconds 35
    if ($result.Code -ne 0) { Fail ("D-Bus is unavailable in '$Distribution'. Exit $($result.Code).`n" + $result.Value.Trim()) }
    if ($result.Stdout.Trim()) { Write-Host $result.Stdout.Trim() }
}

$log = Join-Path $Data 'dbus-autostart.log'
try {
    if ($Mode -eq 'Check') {
        AssertWindows
        EnsureDbus $Distro
        exit 0
    }

    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $name = "HP3052Scanner-DBus-$sid"
    if ($Mode -eq 'Disable') {
        if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) {
            Unregister-ScheduledTask -TaskName $name -Confirm:$false
        }
        Write-Host 'D-Bus autostart is off. The running Ubuntu service was left alone.'
        exit 0
    }

    AssertWindows
    if (-not $Distro) {
        $settings = Join-Path (Join-Path $env:APPDATA 'HP3052Scanner') 'settings.json'
        if (Test-Path -LiteralPath $settings) {
            $Distro = [string]((Get-Content -LiteralPath $settings -Raw -Encoding UTF8 | ConvertFrom-Json).distro)
        }
        if (-not $Distro -and (Test-Path -LiteralPath $Selection)) {
            $Distro = [string]((Get-Content -LiteralPath $Selection -Raw -Encoding UTF8 | ConvertFrom-Json).distro)
        }
    }
    $ubuntu = SelectUbuntu -Requested $Distro
    EnsureDbus $ubuntu.Name

    # Keep the task independent of the extracted archive or its shortcuts.
    $destination = Join-Path $Data 'DbusAutostart'
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    foreach ($file in @('Dbus-Autostart.ps1', 'Wsl-Common.ps1', 'Ensure-Dbus.sh')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $destination -Force
    }
    $arguments = @('-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-ExecutionPolicy', 'Bypass', '-File',
        (Join-Path $destination 'Dbus-Autostart.ps1'), '-Mode', 'Check', '-Distro', $ubuntu.Name)
    $commandLine = ($arguments | ForEach-Object { QuoteNativeArgument $_ }) -join ' '
    $action = New-ScheduledTaskAction -Execute (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -Argument $commandLine
    $atLogon = New-ScheduledTaskTrigger -AtLogOn -User $sid
    $periodic = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
    $principal = New-ScheduledTaskPrincipal -UserId $sid -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 1)
    Register-ScheduledTask -TaskName $name -Action $action -Trigger @($atLogon, $periodic) `
        -Principal $principal -Settings $settings -Description 'Keep the Ubuntu D-Bus available for HP 3052 network scanning.' -Force | Out-Null
    Write-Host "D-Bus is ready in '$($ubuntu.Name)'. Windows will check it at sign-in and every minute." -ForegroundColor Green
    exit 0
} catch {
    $message = $_.Exception.Message
    try {
        New-Item -ItemType Directory -Force -Path $Data | Out-Null
        [IO.File]::WriteAllText($log, "$(Get-Date -Format o) $message")
    } catch {}
    Write-Host "ERROR: $message" -ForegroundColor Red
    Write-Host "Log: $log"
    exit 1
}
