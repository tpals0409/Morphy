param([string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$tools = Join-Path $repo '.tools'
New-Item -ItemType Directory -Force $tools | Out-Null
$msi = Join-Path $tools 'LibreOffice.msi'
$hash = 'c6298b10cfa1748bcbedad94c62ce771e240e5021bfd6d7a967cd571b04b5312'
if (!(Test-Path $msi)) { Invoke-WebRequest 'https://download.documentfoundation.org/libreoffice/stable/26.8.1/win/x86_64/LibreOffice_26.8.1_Win_x86-64.msi' -OutFile $msi }
if ((Get-FileHash $msi -Algorithm SHA256).Hash -ne $hash) { throw 'Engine hash mismatch' }
$engine = Join-Path $tools 'lo-extracted\LibreOffice\program\soffice.com'
$app = Join-Path $repo 'build\windows-dev\Morphy.exe'
if (!(Test-Path $engine)) {
    $test = Start-Process $app -ArgumentList @('--extract-engine', ('"' + $msi + '"'), ('"' + (Join-Path $tools 'lo-extracted') + '"')) -Wait -PassThru -WindowStyle Hidden
    if ($test.ExitCode -ne 0) { throw 'Engine extraction failed' }
}
$env:MORPHY_ENGINE_PATH = $engine
$env:PYTHONIOENCODING = 'utf-8'
$env:SAL_DISABLE_OPENCL = '1'
$corpus = Join-Path $repo 'build\windows-corpus'
& $Python (Join-Path $repo 'scripts\make_corpus.py') $corpus
if ($LASTEXITCODE -ne 0) { throw 'Corpus generation failed' }
& $Python (Join-Path $repo 'scripts\test_windows.py') --app $app --engine $engine --corpus $corpus --output (Join-Path $repo 'build\windows-integration')
if ($LASTEXITCODE -ne 0) { throw 'Office integration tests failed' }
