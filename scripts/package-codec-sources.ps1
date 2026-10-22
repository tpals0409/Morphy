$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$headers = @{ 'User-Agent' = 'Morphy-build' }
$root = Join-Path $repo ('.tools\codec-source-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $root | Out-Null
function Get-SourceTree([string]$Repository, [string]$Revision, [string]$Destination) {
    $tree = Invoke-RestMethod "https://api.github.com/repos/$Repository/git/trees/$Revision`?recursive=1" -Headers $headers
    $zip = Join-Path $root ([Guid]::NewGuid().ToString('N') + '.zip')
    $unpack = Join-Path $root ([Guid]::NewGuid().ToString('N'))
    Invoke-WebRequest "https://codeload.github.com/$Repository/zip/$Revision" -OutFile $zip
    Expand-Archive $zip $unpack
    $source = (Get-ChildItem -LiteralPath $unpack -Directory | Select-Object -First 1).FullName
    New-Item -ItemType Directory -Force $Destination | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $Destination -Recurse -Force
    $modules = Join-Path $source '.gitmodules'
    if (Test-Path $modules) {
        $text = Get-Content -LiteralPath $modules -Raw
        foreach ($block in [regex]::Split($text, '\[submodule ')) {
            $pathMatch = [regex]::Match($block, '(?m)^\s*path\s*=\s*(.+)$')
            $urlMatch = [regex]::Match($block, '(?m)^\s*url\s*=\s*(.+)$')
            if (!$pathMatch.Success -or !$urlMatch.Success) { continue }
            $relative = $pathMatch.Groups[1].Value.Trim()
            $url = $urlMatch.Groups[1].Value.Trim()
            if ($url -notmatch '^https://github.com/([^/]+/[^/]+?)(?:\.git)?$') { throw "Unsupported submodule URL: $url" }
            $childRepo = $Matches[1]
            $entry = $tree.tree | Where-Object { $_.path -eq $relative -and $_.type -eq 'commit' } | Select-Object -First 1
            if (!$entry) { throw "Missing submodule commit: $relative" }
            $child = [IO.Path]::GetFullPath((Join-Path $Destination $relative))
            if (!$child.StartsWith([IO.Path]::GetFullPath($Destination) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid source path' }
            Get-SourceTree $childRepo $entry.sha $child
        }
    }
}
$sources = Join-Path $root 'WicHeic-v1.1.16'
Get-SourceTree 'prsyahmi/wic_heic' 'v1.1.16' $sources
$out = Join-Path $repo 'build\windows-release'
New-Item -ItemType Directory -Force $out | Out-Null
$archive = Join-Path $out 'WicHeic-v1.1.16-corresponding-source.zip'
if (Test-Path $archive) { Remove-Item -LiteralPath $archive }
[IO.Compression.ZipFile]::CreateFromDirectory($sources, $archive)
Write-Output $archive
