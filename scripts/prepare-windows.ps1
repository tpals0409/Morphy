$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$tools = Join-Path $repo '.tools'
New-Item -ItemType Directory -Force $tools | Out-Null
$dll = Join-Path $tools 'wicheic\(Build)\x64\Release\wic_heic.dll'
$expected = '2edf898d40ba5a577d4bebefa542fad2c647e58bdf4207d296508db181210ad5'
if (!(Test-Path $dll) -or (Get-FileHash $dll -Algorithm SHA256).Hash -ne $expected) {
    $zip = Join-Path $tools 'wicheic.zip'
    Invoke-WebRequest 'https://github.com/prsyahmi/wic_heic/releases/download/v1.1.16/WicHeic-1.1.16.zip' -OutFile $zip
    Expand-Archive $zip (Join-Path $tools 'wicheic') -Force
    if ((Get-FileHash $dll -Algorithm SHA256).Hash -ne $expected) { throw 'HEIC encoder checksum mismatch' }
}
$license = Join-Path $tools 'wicheic-license.txt'
if (!(Test-Path $license)) { Invoke-WebRequest 'https://raw.githubusercontent.com/prsyahmi/wic_heic/v1.1.16/Installer/License.txt' -OutFile $license }
