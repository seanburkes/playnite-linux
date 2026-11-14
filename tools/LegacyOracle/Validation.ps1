# Shared by the Windows verifier and scratch-only comparator/safety tests.
$SettingsFiles = @('config.json', 'fullscreenConfig.json', 'windowPositions.json')
$LibraryFiles = @('database.json', 'platforms.db', 'emulators.db', 'genres.db',
    'companies.db', 'tags.db', 'categories.db', 'ageratings.db', 'series.db',
    'regions.db', 'sources.db', 'features.db', 'tools.db', 'games.db', 'scanners.db',
    'filterpresets.db', 'importexclusions.db', 'completionstatuses.db')
$PayloadFiles = @($SettingsFiles | ForEach-Object { "settings/$_" }) +
    @($LibraryFiles | ForEach-Object { "library/$_" })
if ($PayloadFiles.Count -ne 21 -or $SettingsFiles.Count -ne 3 -or $LibraryFiles.Count -ne 18) {
    throw 'Approved payload inventory must be exactly 21 files: 3 settings and 18 library.'
}

function Get-UnlinkedPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    # Windows 8.3 aliases carry a ~N component; reject the shape instead of resolving it.
    foreach ($segment in ($full -split '[\\/]')) {
        if ($segment -match '~\d') { throw "Short (8.3) path aliases are forbidden: $full" }
    }
    for ($current = $full; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Linked path is forbidden: $current"
        }
    }
    return $full
}

function Test-Within([string]$Path, [string]$Root) {
    $rootPath = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    return $Path.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-ScratchPath([string]$Scratch, [string[]]$ReadOnlyPaths) {
    $full = Get-UnlinkedPath $Scratch
    foreach ($path in $ReadOnlyPaths) {
        $source = Get-UnlinkedPath $path
        if ((Test-Within $full $source) -or (Test-Within $source $full)) {
            throw "Scratch overlaps a read-only path: $source"
        }
    }
    if (Test-Path -LiteralPath $full) { throw 'Scratch must not exist.' }
    return $full
}

function Get-TreeHashes([string]$Root) {
    $null = Get-UnlinkedPath $Root
    if (!(Test-Path -LiteralPath $Root -PathType Container)) { throw "Missing directory: $Root" }
    $result = [ordered]@{}
    # Check links before recursion; never follow a linked subtree.
    foreach ($item in (Get-ChildItem -LiteralPath $Root -Force | Sort-Object Name)) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked entry: $($item.FullName)" }
        if ($item.PSIsContainer) {
            $children = Get-TreeHashes $item.FullName
            foreach ($name in $children.Keys) { $result[$item.Name + '/' + $name] = $children[$name] }
        } else {
            $result[$item.Name] = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    return $result
}

function Get-TreeState([string]$Root) {
    if (!(Test-Path -LiteralPath $Root)) { return '<absent>' }
    return Get-TreeHashes $Root | ConvertTo-Json -Compress
}

function Assert-TreeUnchanged([string]$Root, [string]$Before, [string]$Reason) {
    if ($Before -cne (Get-TreeState $Root)) { throw $Reason }
}

function Assert-Names([string[]]$Actual, [string[]]$Expected) {
    if ($Actual.Count -ne $Expected.Count -or
        (Compare-Object -ReferenceObject $Expected -DifferenceObject $Actual -CaseSensitive)) {
        throw 'Exact relative file inventory differs.'
    }
}

function Assert-SameBytes([string]$Left, [string]$Right) {
    $a = [IO.File]::ReadAllBytes($Left)
    $b = [IO.File]::ReadAllBytes($Right)
    if ($a.Length -ne $b.Length) { throw 'Byte lengths differ.' }
    for ($i = 0; $i -lt $a.Length; $i++) {
        if ($a[$i] -ne $b[$i]) { throw "Bytes differ at offset $i." }
    }
}

function Assert-SameTree([string]$Left, [string]$Right) {
    $a = Get-TreeHashes $Left
    $b = Get-TreeHashes $Right
    Assert-Names @($a.Keys) @($b.Keys)
    foreach ($name in $a.Keys) { Assert-SameBytes (Join-Path $Left $name) (Join-Path $Right $name) }
}

function Get-ManifestHashes([string]$Manifest, [string[]]$ExpectedNames, [string]$Prefix = '') {
    $null = Get-UnlinkedPath $Manifest
    if (!(Test-Path -LiteralPath $Manifest -PathType Leaf)) { throw "Missing SHA256 manifest: $Manifest" }
    $listed = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($line in [IO.File]::ReadAllLines($Manifest)) {
        if ($line -cnotmatch '^([0-9a-f]{64})  ([a-zA-Z0-9./_-]+)$') { throw 'Invalid SHA256 manifest line.' }
        $name = $Matches[2]
        if ($Prefix) {
            # p1-003 pins the bundle root; only library/ entries belong to this unit.
            if (!$name.StartsWith($Prefix, [StringComparison]::Ordinal)) { continue }
            $name = $name.Substring($Prefix.Length)
            if (!$name) { throw 'Empty path after library prefix.' }
        }
        if (!$listed.TryAdd($name, $Matches[1])) { throw "Duplicate manifest path: $name" }
    }
    Assert-Names @($listed.Keys) $ExpectedNames
    return $listed
}

function Assert-HashInventory([string]$Root, [string]$Manifest, [string[]]$Names, [string]$Prefix = '') {
    $actual = Get-TreeHashes $Root
    Assert-Names @($actual.Keys) $Names
    $listed = Get-ManifestHashes $Manifest $Names $Prefix
    foreach ($name in $Names) {
        if ($listed[$name] -cne $actual[$name]) { throw "SHA256 differs: $name" }
    }
}

function Assert-Goldens([string]$Emitted, [string]$Golden) {
    Assert-Names @((Get-TreeHashes $Emitted).Keys) $PayloadFiles
    foreach ($group in @('settings', 'library')) {
        $names = if ($group -eq 'settings') { $SettingsFiles } else { $LibraryFiles }
        $hashes = Get-TreeHashes (Join-Path $Golden $group)
        Assert-Names @($hashes.Keys) $names
    }
    if (!(Test-Path -LiteralPath (Join-Path $Golden 'settings-library.md') -PathType Leaf)) {
        throw 'Missing golden provenance; acceptance pending.'
    }
    # Manifest paths are relative to tests/golden, e.g. "settings/config.json".
    $listed = Get-ManifestHashes (Join-Path $Golden 'settings-library.sha256') $PayloadFiles
    foreach ($name in $PayloadFiles) {
        $path = Join-Path $Golden $name
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $listed[$name]) {
            throw "Golden SHA256 differs: $name"
        }
        Assert-SameBytes (Join-Path $Emitted $name) $path
    }
}
