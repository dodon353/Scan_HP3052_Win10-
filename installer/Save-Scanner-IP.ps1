#requires -Version 5.1
param([string]$Distro = 'Ubuntu-22.04')
$ErrorActionPreference = 'Stop'
function Test-IPv4([string]$Value) {
 if ($Value -notmatch '^(?:0|[1-9][0-9]{0,2})(?:\.(?:0|[1-9][0-9]{0,2})){3}$') { return $false }
 $addr = $null
 if (-not [System.Net.IPAddress]::TryParse($Value, [ref]$addr)) { return $false }
 $octets = $addr.GetAddressBytes()
 if ($addr.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { return $false }
 if ($octets[0] -eq 0 -or $octets[0] -ge 224 -or $octets[0] -eq 127) { return $false }
 if ($Value -eq '255.255.255.255') { return $false }
 return $true
}

function Merge-ScannerSettings($Previous,[string]$Ip,[string]$DistroName) {
 if ($null -eq $Previous) { $Previous = [pscustomobject]@{} }
 if ($Previous -isnot [pscustomobject]) { throw 'Existing settings must be a JSON object. Settings were not changed.' }
 $Previous | Add-Member -NotePropertyName ip -NotePropertyValue $Ip -Force
 $Previous | Add-Member -NotePropertyName distro -NotePropertyValue $DistroName -Force
 return $Previous
}
function Write-ScannerSettings([string]$Path,$Settings) {
 $temp = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
 try {
  $json = $Settings | ConvertTo-Json -Depth 30
  [IO.File]::WriteAllText($temp,$json,(New-Object Text.UTF8Encoding($false)))
  if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temp,$Path,[NullString]::Value) }
  else { [IO.File]::Move($temp,$Path) }
 } finally {
  if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
 }
}

try {
  $directory = Join-Path $env:APPDATA 'HP3052Scanner'
  $configPath = Join-Path $directory 'settings.json'
  $default = '192.168.110.100'
  $previous = $null
  if (Test-Path -LiteralPath $configPath) {
    try {
      $previous = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
      if (Test-IPv4 ([string]$previous.ip)) { $default = [string]$previous.ip }
    } catch { throw 'Existing settings.json cannot be read. Its contents were preserved. Open the application to check the settings before retrying.' }
  }
  Write-Host ''
  Write-Host 'Enter printer IPv4 address. Press ENTER to keep the displayed value.'
  $value = Read-Host "Printer IP [$default]"
  if ([string]::IsNullOrWhiteSpace($value)) { $value = $default }
  $value = $value.Trim()
  if (-not (Test-IPv4 $value)) { throw "Invalid IPv4 address: $value" }
  New-Item -Path $directory -ItemType Directory -Force | Out-Null
  $data = Merge-ScannerSettings -Previous $previous -Ip $value -DistroName $Distro
  Write-ScannerSettings -Path $configPath -Settings $data
  Write-Host "Saved printer IP: $value" -ForegroundColor Green
  Write-Host "Configuration: $configPath"
  Write-Host 'No scanner connection test was performed.'
} catch {
  Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
  exit 1
}
