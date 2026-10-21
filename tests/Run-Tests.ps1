$ErrorActionPreference = 'Stop'
$package = Split-Path -Parent $PSScriptRoot
$output = Join-Path $package ('test-results-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $output
Push-Location $package
try {
    & dotnet run --project tests/CoreTests.csproj -c Release -- (Join-Path $output 'core') $PSScriptRoot --skip-process
    if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
    & dotnet run --project tests/WindowsTests.csproj -c Release -- (Join-Path $output 'images')
    if ($LASTEXITCODE -ne 0) { throw 'Windows image checks failed.' }
    & (Join-Path $PSScriptRoot 'settings-tests.ps1') -PackageRoot $package -Output (Join-Path $output 'settings')
    Write-Host ('All Windows checks passed. Results: ' + $output)
} finally { Pop-Location }
