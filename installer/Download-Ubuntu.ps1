function DownloadCheckedRootfs {
 $curl = Join-Path $env:WINDIR 'System32\curl.exe'
 if (-not (Test-Path $curl)) {Fail 'curl.exe missing from Windows.'}
 $sumFile = Join-Path $Data 'SHA256SUMS'
 Step 'Downloading official Ubuntu 22.04 checksum list...'
 Write-Host ('Source: '+$SumUrl)
 & $curl -f -L --retry 3 --connect-timeout 20 -o $sumFile $SumUrl
 $downloadCode=$LASTEXITCODE
 if ($downloadCode -eq 60) {Fail ('TLS certificate verification failed for '+$SumUrl+'. Check the certificate presented on this PC (proxy, antivirus or DNS). Verification was not bypassed.')}
 if ($downloadCode -ne 0) {Fail ('Could not download Ubuntu SHA256SUMS (curl exit '+$downloadCode+'). Source: '+$SumUrl)}
 $pattern='(?m)^([0-9a-fA-F]{64})\s+\*?ubuntu-jammy-wsl-amd64-wsl\.rootfs\.tar\.gz\s*$'
 $content=[IO.File]::ReadAllText($sumFile)
 if ($content -notmatch $pattern) {Fail 'Checksum list does not contain expected Ubuntu image. Installation stopped.'}
 $expected=$Matches[1].ToLowerInvariant()
 $needsDownload=$true
 if (Test-Path $Image) {
  $hash=(Get-FileHash -LiteralPath $Image -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($hash -eq $expected) {$needsDownload=$false;Write-Host 'Previously downloaded Ubuntu image passed SHA256.'}
 }
 if ($needsDownload) {
  Step 'Downloading Ubuntu 22.04 rootfs (approximately 230 MB). Progress displayed below...'
  $partial="$Image.partial"
  & $curl -f -L --retry 3 --retry-delay 2 --connect-timeout 20 --speed-time 120 --speed-limit 1024 '-C' '-' '--progress-bar' '-o' $partial $ImageUrl
  $downloadCode=$LASTEXITCODE
  if ($downloadCode -eq 33) {
   Write-Warning 'Server rejected download resume. Restarting this image download from byte zero.'
   & $curl -f -L --retry 3 --retry-delay 2 --connect-timeout 20 --speed-time 120 --speed-limit 1024 '--progress-bar' '-o' $partial $ImageUrl
   $downloadCode=$LASTEXITCODE
  }
  if ($downloadCode -eq 60) {Fail ('TLS certificate verification failed for '+$ImageUrl+'. Verification was not bypassed; partial download retained.')}
  if ($downloadCode -ne 0) {Fail ('Ubuntu image download failed (curl exit '+$downloadCode+'). Retry INSTALL-UBUNTU.cmd (partial download is retained).')}
  $actual=(Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -ne $expected) {Remove-Item $partial -Force;Fail 'Ubuntu SHA256 mismatch. Invalid image removed.'}
  Move-Item -LiteralPath $partial -Destination $Image -Force
 }
}
