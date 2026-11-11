#requires -Version 5.1
$ErrorActionPreference = 'Stop'
try {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $name = "HP3052Scanner-DBus-$sid"
    # Remove only the task created by older versions for this Windows account.
    $task = Get-ScheduledTask -TaskPath '\' | Where-Object { $_.TaskName -eq $name }
    if ($task) {
        Unregister-ScheduledTask -TaskName $name -TaskPath '\' -Confirm:$false
        Write-Host 'Removed the periodic D-Bus check.'
    } else {
        Write-Host 'No periodic D-Bus check is configured for this user.'
    }
    Write-Host 'The Ubuntu service was left running if already started.'
    exit 0
} catch {
    Write-Host ("ERROR: " + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
