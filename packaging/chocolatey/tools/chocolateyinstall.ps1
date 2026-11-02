$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = 'mipdfsign-community'
  fileType       = 'exe'
  url            = 'https://github.com/Mibuw/miPDFsign-Community/releases/download/v1.1.0/miPDFsignCommunity_Setup_1.1.0.exe'
  softwareName   = 'miPDFsign Community*'
  checksum       = '9AA2F9424B87553D61D53FC04CF2555F3665D1F1BB8CADE5C748417681346D6E'
  checksumType   = 'sha256'
  # Inno Setup silent switches
  silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes = @(0, 3010, 1641)
}

Install-ChocolateyPackage @packageArgs
