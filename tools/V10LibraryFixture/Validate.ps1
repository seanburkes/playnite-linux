param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Output,
    [Parameter(Mandatory = $true)][string]$Fixture
)
$ErrorActionPreference = 'Stop'

# Reject any output that could write into the fixture or the repository before
# the first directory creation. Reparse points are resolved component by
# component so an aliased ancestor cannot redirect the scratch tree.
function Get-ReparseTarget([string]$ItemPath) {
    $item = Get-Item -LiteralPath $ItemPath -Force
    if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return $null }
    $method = $item.GetType().GetMethod('ResolveLinkTarget', [Type[]]@([bool]))
    if ($null -ne $method) {
        $resolved = $method.Invoke($item, @($true))
        if ($null -eq $resolved) { throw "Cannot resolve reparse point: $ItemPath" }
        return [IO.Path]::GetFullPath($resolved.FullName)
    }
    $target = @($item.PSObject.Properties['Target'])[0]
    if ($null -eq $target -or @($target.Value).Count -eq 0) {
        throw "Cannot resolve reparse point: $ItemPath"
    }
    $targetPath = @($target.Value)[0]
    return [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $ItemPath) $targetPath))
}

function Normalize-CanonicalPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    if ($full.Length -gt $root.Length) {
        $full = $full.TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
    }
    return $full
}

function Get-CanonicalPath {
    param([string]$Path, [int]$Depth = 0)
    if ($Depth -gt 64) { throw "Path link recursion exceeds limit: $Path" }
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Path must not be empty.' }
    if (-not [IO.Path]::IsPathRooted($Path)) {
        $Path = Join-Path (Get-Location).ProviderPath $Path
    }
    $root = [IO.Path]::GetPathRoot($Path)
    if ([string]::IsNullOrEmpty($root)) { throw "Path has no root: $Path" }
    $parts = $Path.Substring($root.Length).Split(
        [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar),
        [StringSplitOptions]::RemoveEmptyEntries)
    $current = $root
    foreach ($part in $parts) {
        if ($part -eq '.') { continue }
        if ($part -eq '..') {
            $parent = Split-Path -Parent $current
            if (-not [string]::IsNullOrEmpty($parent)) { $current = $parent }
            continue
        }
        $next = Join-Path $current $part
        if (Test-Path -LiteralPath $next) {
            $target = Get-ReparseTarget $next
            if ($null -ne $target) {
                $current = Get-CanonicalPath -Path $target -Depth ($Depth + 1)
            }
            else {
                $current = $next
            }
        }
        else {
            $current = $next
        }
    }
    return (Normalize-CanonicalPath $current)
}

function Get-ComparablePath([string]$Path) {
    $trimmed = $Path.TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
    if ([string]::IsNullOrEmpty($trimmed)) { return $Path }
    return $trimmed + [IO.Path]::DirectorySeparatorChar
}

function Test-PathOverlap([string]$Left, [string]$Right) {
    $comparison = [StringComparison]::OrdinalIgnoreCase
    $left = Get-ComparablePath $Left
    $right = Get-ComparablePath $Right
    return $left.StartsWith($right, $comparison) -or $right.StartsWith($left, $comparison)
}

function Find-RepositoryRoot([string]$Path) {
    $probe = Normalize-CanonicalPath $Path
    while ($true) {
        if (Test-Path -LiteralPath (Join-Path $probe '.git')) { return $probe }
        $parent = Split-Path -Parent $probe
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $probe) { return $null }
        $probe = $parent
    }
}

$fixturePath = Get-CanonicalPath $Fixture
$outputPath = Get-CanonicalPath $Output
if (Test-PathOverlap $fixturePath $outputPath) {
    throw "UNSAFE-OUTPUT: Output must not equal, contain, or be contained by the fixture tree: $outputPath"
}
$repositoryRoot = Find-RepositoryRoot $fixturePath
if ($null -ne $repositoryRoot -and (Test-PathOverlap $repositoryRoot $outputPath)) {
    throw "UNSAFE-OUTPUT: Output must be genuinely external disposable scratch outside the repository: $outputPath"
}
if (Test-Path -LiteralPath $Output) { throw 'Output must not exist' }
New-Item -ItemType Directory -Path $Output | Out-Null

function Invoke-Fixture([string]$Mode, [string]$Path) {
    & $Exe $Mode $Path
    if ($LASTEXITCODE -ne 0) { throw "Fixture $Mode failed for $Path" }
}

$first = Join-Path $Output 'first'
$second = Join-Path $Output 'second'
Invoke-Fixture generate $first
Invoke-Fixture generate $second
& $Exe compare $first $second
if ($LASTEXITCODE -ne 0) { throw 'Two fresh generations differ logically' }

# Both negative checks operate only on a disposable generated copy.
$corrupt = Join-Path $Output 'corrupt'
Copy-Item -Recurse $first $corrupt
$gameFile = Join-Path $corrupt 'library/games.db'
$bytes = [IO.File]::ReadAllBytes($gameFile)
$bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 1
[IO.File]::WriteAllBytes($gameFile, $bytes)
& $Exe verify $corrupt
if ($LASTEXITCODE -eq 0) { throw 'One-byte mutation was accepted' }
Copy-Item (Join-Path $first 'library/games.db') $gameFile -Force
[IO.File]::WriteAllText((Join-Path $corrupt 'library/unexpected.txt'), 'synthetic')
& $Exe verify $corrupt
if ($LASTEXITCODE -eq 0) { throw 'Unexpected file was accepted' }

# The committed capture is strict only when its checksum manifest exists. Before
# protected intake the workflow still generates, runs the negative checks, and
# uploads artifacts with an explicit pending notice.
$fixtureManifest = Join-Path $Fixture 'SHA256SUMS'
if (Test-Path -LiteralPath $fixtureManifest) {
    Invoke-Fixture verify $Fixture
    & $Exe compare $first $Fixture
    if ($LASTEXITCODE -ne 0) { throw 'Fresh generation differs logically from the committed fixture' }
    Write-Host 'PASS: two logically identical generations, one-byte rejection, extra-file rejection, pinned committed capture and v10 read-back'
    exit 0
}
else {
    if (Test-Path -LiteralPath $Fixture) {
        $entries = @(Get-ChildItem -LiteralPath $Fixture -Force)
        if ($entries.Count -gt 0) {
            throw "Fixture directory exists without SHA256SUMS; refusing to treat an incomplete capture as pending: $Fixture"
        }
    }
    Write-Host 'PENDING: committed-fixture comparison pending (SHA256SUMS is absent); two logically identical generations, one-byte rejection, extra-file rejection and v10 read-back passed'
    exit 0
}
