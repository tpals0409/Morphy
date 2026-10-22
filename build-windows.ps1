param([switch]$Run, [switch]$SelfTest)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
& (Join-Path $repo 'scripts\prepare-windows.ps1')
$localSdk = Join-Path $repo '.tools\dotnet\dotnet.exe'
$sdk = if (Test-Path $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $repo '.tools\cli'
$env:NUGET_PACKAGES = Join-Path $repo '.tools\packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$project = Join-Path $repo 'windows\Morphy.Windows\Morphy.Windows.csproj'
& $sdk restore $project --configfile (Join-Path $repo 'windows\NuGet.Config') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Windows restore failed.' }
& $sdk publish $project --configuration Release -r win-x64 --self-contained true --no-restore --nologo -o (Join-Path $repo 'build\windows-dev')
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }
$exe = Join-Path $repo 'build\windows-dev\Morphy.exe'
foreach ($native in @('Magick.Native-Q8-x64.dll', 'pdfium.dll', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'wic_heic.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path (Split-Path $exe) $native))) { throw "Published native dependency is missing: $native" }
}
$env:DOTNET_ROOT = Split-Path $sdk
if ($SelfTest) {
    $process = Start-Process -FilePath $exe -ArgumentList '--selftest' -Wait -PassThru -WindowStyle Hidden
    Get-Content (Join-Path (Split-Path $exe) 'selftest-result.txt')
    if ($process.ExitCode -ne 0) { throw 'Windows self-test failed.' }
}
if ($Run) { Start-Process -FilePath $exe -WindowStyle Hidden }
