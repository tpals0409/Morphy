param([string]$Version = '0.1.0', [string]$CertificateThumbprint = '', [string]$SignTool = '')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a three-part numeric version.' }
$repo = Split-Path $PSScriptRoot
& (Join-Path $repo 'scripts\prepare-windows.ps1')
$sdk = Join-Path $repo '.tools\dotnet\dotnet.exe'
if (!(Test-Path $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $repo '.tools\cli'
$env:NUGET_PACKAGES = Join-Path $repo '.tools\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$out = Join-Path $repo 'build\windows-release'
$app = Join-Path $out ('app-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $out | Out-Null
$project = Join-Path $repo 'windows\Morphy.Windows\Morphy.Windows.csproj'
& $sdk restore $project --configfile (Join-Path $repo 'windows\NuGet.Config') -p:SelfContained=true
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
& $sdk publish $project -c Release -r win-x64 --self-contained true --no-restore -p:Version=$Version -o $app --nologo
if ($LASTEXITCODE -ne 0) { throw 'App publish failed' }
foreach ($native in @('Magick.Native-Q8-x64.dll', 'pdfium.dll', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'wic_heic.dll', 'WebView2Loader.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $app $native))) { throw "Published native dependency is missing: $native" }
}
if ($CertificateThumbprint) {
    if (!$SignTool) { $SignTool = (Get-Command signtool -ErrorAction Stop).Source }
    & $SignTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr 'https://timestamp.digicert.com' /td SHA256 (Join-Path $app 'Morphy.exe')
    if ($LASTEXITCODE -ne 0) { throw 'App signing failed' }
}
$notices = Join-Path $app 'ThirdParty'
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $app 'LICENSE.txt')
New-Item -ItemType Directory -Force $notices | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'windows\THIRD-PARTY.md') -Destination $notices
# Preserve available notices from the exact locked package versions.
$assets = Get-Content (Join-Path $repo 'windows\Morphy.Windows\obj\project.assets.json') -Raw | ConvertFrom-Json
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $package = Join-Path $env:NUGET_PACKAGES $library.Value.path
    foreach ($file in Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object { $_.Name -match '^(LICENSE|NOTICE|COPYING)' -and $_.Extension -notin '.dll','.xml' }) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $notices ($library.Name.Replace('/','-') + '-' + $file.Name)) -Force
    }
}
$hashes = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $app -Recurse -File) {
    $relative = [System.IO.Path]::GetRelativePath($app, $file.FullName).Replace('\','/')
    $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$hashes | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $app 'bundle-manifest.json') -Encoding utf8
$payload = Join-Path $out 'payload.zip'
if (Test-Path $payload) { Remove-Item -LiteralPath $payload }
[System.IO.Compression.ZipFile]::CreateFromDirectory($app, $payload, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$setup = Join-Path $repo 'windows\Morphy.Setup\Morphy.Setup.csproj'
& $sdk restore $setup --configfile (Join-Path $repo 'windows\NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'Setup restore failed' }
& $sdk publish $setup -c Release --no-restore -p:Version=$Version -o (Join-Path $out 'setup') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Setup publish failed' }
$installer = Join-Path $out "Morphy-$Version-win-x64-Setup.exe"
Copy-Item -LiteralPath (Join-Path $out 'setup\MorphySetup.exe') -Destination $installer -Force
if ($CertificateThumbprint) {
    & $SignTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr 'https://timestamp.digicert.com' /td SHA256 $installer
    if ($LASTEXITCODE -ne 0) { throw 'Setup signing failed' }
}
$digest = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$digest  $([System.IO.Path]::GetFileName($installer))" | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii
@{
    version = $Version
    path = "/releases/$([System.IO.Path]::GetFileName($installer))"
    sha256 = $digest
    bytes = (Get-Item -LiteralPath $installer).Length
    published_at = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
} | ConvertTo-Json | Set-Content (Join-Path $out 'windows-release.json') -Encoding utf8NoBOM
Write-Output $installer
