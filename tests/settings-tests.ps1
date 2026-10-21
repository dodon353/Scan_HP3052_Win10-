param([string]$PackageRoot,[string]$Output)
$ErrorActionPreference='Stop'
$script:Passed=0
function Check([bool]$Value,[string]$Message){if(-not $Value){throw $Message};$script:Passed++;Write-Host "PASS: $Message"}
foreach($path in @((Get-ChildItem -LiteralPath (Join-Path $PackageRoot 'installer') -Filter '*.ps1')) + @((Get-Item -LiteralPath (Join-Path $PackageRoot 'tests/Run-Tests.ps1')))){
 $tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($path.FullName,[ref]$tokens,[ref]$errors)
 Check ($errors.Count -eq 0) ("PowerShell parser: "+$path.Name)
 if($path.Name -eq 'Save-Scanner-IP.ps1'){
  foreach($statement in $ast.EndBlock.Statements){if($statement -is [Management.Automation.Language.FunctionDefinitionAst]){. ([scriptblock]::Create($statement.Extent.Text))}}
 }
}
$null=New-Item -ItemType Directory -Force -Path $Output
$original='{"ip":"192.168.1.1","distro":"Old Ubuntu","LastFolder":"C:\\Scans","Current":{"Dpi":600,"Mode":"Color"},"Profiles":[{"Name":"Мой профиль","Source":"ADF","Dpi":300,"Nested":{"Keep":[1,2,3]}}]}'|ConvertFrom-Json
$result=Merge-ScannerSettings -Previous $original -Ip '192.168.1.20' -DistroName 'Ubuntu-22.04'
Check ($result.ip -eq '192.168.1.20' -and $result.distro -eq 'Ubuntu-22.04') 'IP helper changes only printer connection fields'
Check ($result.Profiles[0].Name -eq 'Мой профиль' -and $result.Current.Dpi -eq 600 -and $result.LastFolder -eq 'C:\Scans') 'Profiles, current scan settings and folder survive scanner reconfiguration'
$file=Join-Path $Output 'settings.json'
Write-ScannerSettings -Path $file -Settings $result
$read=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
Check ($read.Profiles[0].Nested.Keep.Count -eq 3) 'Nested settings are not truncated by JSON serialization depth'
$result.ip='10.0.0.10';Write-ScannerSettings -Path $file -Settings $result
$read=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
Check ($read.ip -eq '10.0.0.10' -and $read.Profiles[0].Source -eq 'ADF') 'Atomic replacement preserves profiles while updating existing settings'
Check (@(Get-ChildItem -LiteralPath $Output -Filter '*.tmp').Count -eq 0) 'Atomic settings write leaves no temporary files'
$new=Merge-ScannerSettings -Previous $null -Ip '192.168.1.2' -DistroName 'Ubuntu'
Check ($new.ip -eq '192.168.1.2' -and $new.distro -eq 'Ubuntu') 'Initial setup creates connection settings without requiring application profiles'
$caught=$false;try{Merge-ScannerSettings -Previous @('bad root') -Ip '1.2.3.4' -DistroName 'Ubuntu'}catch{$caught=$true}
Check $caught 'Invalid settings root cannot be silently overwritten'
Write-Host "TOTAL PASSED: $script:Passed"
