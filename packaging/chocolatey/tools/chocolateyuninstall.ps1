$ErrorActionPreference = 'Stop'

$packageName = 'mipdfsign-community'
[array]$key = Get-UninstallRegistryKey -SoftwareName 'miPDFsign Community*'

if ($key.Count -eq 1) {
  $key | ForEach-Object {
    Uninstall-ChocolateyPackage -PackageName $packageName `
      -FileType 'exe' `
      -SilentArgs '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' `
      -ValidExitCodes @(0) `
      -File "$($_.UninstallString)"
  }
} elseif ($key.Count -eq 0) {
  Write-Warning "$packageName has already been uninstalled."
} else {
  Write-Warning "$($key.Count) matching entries found for '$packageName'. Uninstall skipped; remove manually."
  $key | ForEach-Object { Write-Warning "- $($_.DisplayName)" }
}
