# Fetch and verify the pinned Playnite.Deps package on Windows.
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "oracle-lock\packages"
$url = "https://nuget.playnite.link/v3/package/playnite.deps/10.56.0/playnite.deps.10.56.0.nupkg"
$file = "Playnite.Deps.10.56.0.nupkg"
$path = Join-Path $dest $file

New-Item -ItemType Directory -Force -Path $dest | Out-Null
if (!(Test-Path $path)) {
    Write-Host "fetching $url"
    Invoke-WebRequest -Uri $url -OutFile $path
}

$expected = (Get-Content (Join-Path $PSScriptRoot "SHA256SUMS") |
    Where-Object { $_ -match [regex]::Escape($file) } |
    ForEach-Object { ($_ -split '\s+')[0] })
$actual = (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant()
if ($actual -ne $expected) {
    Remove-Item $path -Force
    throw "checksum mismatch for $file`: expected $expected, got $actual"
}

Write-Host "ok: $path"
