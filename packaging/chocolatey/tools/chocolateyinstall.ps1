$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = 'mipdfsign-community'
  fileType       = 'exe'
  url            = 'https://github.com/Mibuw/miPDFsign-Community/releases/download/v1.0.1/miPDFsignCommunity_Setup_1.0.1.exe'
  softwareName   = 'miPDFsign Community*'
  checksum       = 'AE0EFCAD7754C55057B0B94A4CB3919A7FBA2C5BA7AA400C666FC6A43F3910C0'
  checksumType   = 'sha256'
  # Inno Setup silent switches
  silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes = @(0, 3010, 1641)
}

Install-ChocolateyPackage @packageArgs
