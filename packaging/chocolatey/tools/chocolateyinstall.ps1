$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = 'mipdfsign-community'
  fileType       = 'exe'
  url            = 'https://github.com/Mibuw/miPDFsign-Community/releases/download/v1.0.0/miPDFsignCommunity_Setup_1.0.0.exe'
  softwareName   = 'miPDFsign Community*'
  checksum       = 'A0AB7B934C834B77944E7A231BC3720FD747A4DF30435345F0A70E8187E3BEB1'
  checksumType   = 'sha256'
  # Inno Setup silent switches
  silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes = @(0, 3010, 1641)
}

Install-ChocolateyPackage @packageArgs
