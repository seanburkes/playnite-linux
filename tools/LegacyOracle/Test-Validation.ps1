param(
    [Parameter(Mandatory)][string]$Scratch,
    [string]$Exe
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Validation.ps1"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$Scratch = Assert-ScratchPath $Scratch @($repo)
New-Item -ItemType Directory -Path $Scratch | Out-Null
$checks = 0

function Assert-Rejected([scriptblock]$Action, [string]$Reason) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (!$rejected) { throw "Accepted negative case: $Reason" }
    $script:checks++
    Write-Host "REJECTED: $Reason"
}

function Assert-Unchanged([string]$Root, [string]$Before, [string]$Reason) {
    Assert-TreeUnchanged $Root $Before $Reason
    $script:checks++
}

# Arbitrary comparator unit-test bytes, NOT v10 output, approved fixture, or golden data.
$emitted = Join-Path $Scratch 'emitted'
$golden = Join-Path $Scratch 'expected'
foreach ($root in @($emitted, $golden)) {
    New-Item -ItemType Directory -Path "$root/settings", "$root/library" | Out-Null
    for ($i = 0; $i -lt $PayloadFiles.Count; $i++) {
        [IO.File]::WriteAllBytes((Join-Path $root $PayloadFiles[$i]), [byte[]](1, 2, 3, $i))
    }
}
$manifest = Join-Path $golden 'settings-library.sha256'
$lines = @($PayloadFiles | ForEach-Object {
    (Get-FileHash -LiteralPath (Join-Path $golden $_) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_
})
[IO.File]::WriteAllLines($manifest, [string[]]$lines)
[IO.File]::WriteAllText((Join-Path $golden 'settings-library.md'), 'Comparator test only; not oracle provenance.')
Assert-Goldens $emitted $golden
$checks++
Write-Host "ACCEPTED: exact $($PayloadFiles.Count)-payload set and manifest"

# Exercise the SAME whole-set comparator used by Verify, not an isolated byte helper.
foreach ($root in @($emitted, $golden)) {
    $path = Join-Path $root $PayloadFiles[0]
    $original = [IO.File]::ReadAllBytes($path)
    $changed = $original.Clone()
    $changed[0] = $changed[0] -bxor 1
    [IO.File]::WriteAllBytes($path, $changed)
    Assert-Rejected { Assert-Goldens $emitted $golden } "one-byte mutation in $root"
    [IO.File]::WriteAllBytes($path, $original)
    [IO.File]::Delete($path)
    Assert-Rejected { Assert-Goldens $emitted $golden } "missing payload in $root"
    [IO.File]::WriteAllBytes($path, $original)
    $extra = Join-Path $root 'library/extra.db'
    [IO.File]::WriteAllBytes($extra, $original)
    Assert-Rejected { Assert-Goldens $emitted $golden } "extra payload in $root"
    [IO.File]::Delete($extra)
    $renamed = Join-Path $root 'settings/Config.json.wrong-case'
    [IO.File]::Move($path, $renamed)
    Assert-Rejected { Assert-Goldens $emitted $golden } "renamed payload in $root"
    [IO.File]::Move($renamed, $path)
    $secondPath = Join-Path $root $PayloadFiles[1]
    $secondBytes = [IO.File]::ReadAllBytes($secondPath)
    [IO.File]::Delete($secondPath)
    Assert-Rejected { Assert-Goldens $emitted $golden } "missing final payload in $root"
    [IO.File]::WriteAllBytes($secondPath, $secondBytes)
}
Assert-Goldens $emitted $golden
$checks++

function Assert-ManifestRejected([string[]]$Content, [string]$Reason) {
    [IO.File]::WriteAllLines($manifest, $Content)
    Assert-Rejected { Assert-Goldens $emitted $golden } "manifest: $Reason"
}

Assert-ManifestRejected ([string[]]$lines[1..20]) 'missing path'
Assert-ManifestRejected ([string[]](@($lines) + ('0' * 64 + '  library/extra.db'))) 'extra path'
Assert-ManifestRejected ([string[]](@($lines) + @($lines[0]))) 'duplicate path'
Assert-ManifestRejected ([string[]](@(('0' * 64 + '  ' + $PayloadFiles[0])) + @($lines[1..20]))) 'wrong hash'
Assert-ManifestRejected ([string[]](@($lines[0].Replace('settings/', '../settings/')) + @($lines[1..20]))) 'path traversal'
Assert-ManifestRejected ([string[]](@($lines[0].Replace('settings/', 'Settings/')) + @($lines[1..20]))) 'path case'
Assert-ManifestRejected ([string[]](@($lines[0].Replace('settings/', '/settings/')) + @($lines[1..20]))) 'absolute path'
Assert-ManifestRejected ([string[]](@($lines[0].Replace($lines[0].Substring(0, 64), $lines[0].Substring(0, 64).ToUpperInvariant())) + @($lines[1..20]))) 'uppercase hash'
Assert-ManifestRejected ([string[]](@('not a manifest line') + @($lines[0..19]))) 'malformed line'
[IO.File]::WriteAllLines($manifest, [string[]]$lines)
[IO.File]::Delete($manifest)
Assert-Rejected { Assert-Goldens $emitted $golden } 'missing manifest'
[IO.File]::WriteAllLines($manifest, [string[]]$lines)
$provenance = Join-Path $golden 'settings-library.md'
[IO.File]::Delete($provenance)
Assert-Rejected { Assert-Goldens $emitted $golden } 'missing provenance'
[IO.File]::WriteAllText($provenance, 'Comparator test only; not oracle provenance.')
Assert-Rejected { Assert-Goldens $emitted (Join-Path $Scratch 'absent-goldens') } 'absent protected corpus'
Assert-Goldens $emitted $golden
$checks++

# A separate SHA256 inventory pins the single approved library capture before generation.
$libraryManifest = Join-Path $Scratch 'library.sha256'
$libraryLines = @($lines | Where-Object { $_ -cmatch '  library/' } | ForEach-Object { $_.Replace('  library/', '  ') })
[IO.File]::WriteAllLines($libraryManifest, [string[]]$libraryLines)
Assert-HashInventory "$golden/library" $libraryManifest $LibraryFiles
$checks++
[IO.File]::WriteAllLines($libraryManifest, [string[]]$libraryLines[1..17])
Assert-Rejected { Assert-HashInventory "$golden/library" $libraryManifest $LibraryFiles } 'missing pinned library hash'
[IO.File]::WriteAllLines($libraryManifest, [string[]](@($libraryLines) + ('0' * 64 + '  extra.db')))
Assert-Rejected { Assert-HashInventory "$golden/library" $libraryManifest $LibraryFiles } 'extra pinned library hash'

# Scratch is rejected without changing source bytes or file sets, even via linked aliases.
$source = Join-Path $Scratch 'disposable-source'
New-Item -ItemType Directory -Path $source | Out-Null
[IO.File]::WriteAllBytes((Join-Path $source 'sentinel'), [byte[]](9, 8, 7))
$beforeSource = Get-TreeState $source
foreach ($unsafe in @($source, "$source/nested", "$source/../disposable-source/nested", $Scratch)) {
    Assert-Rejected { $null = Assert-ScratchPath $unsafe @($source) } "overlapping scratch $unsafe"
    Assert-Unchanged $source $beforeSource 'Rejected overlap changed source.'
    if (Test-Path -LiteralPath "$source/nested") { throw 'Rejected scratch wrote to source.' }
}
$alias = Join-Path $Scratch 'linked-source'
$kind = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
New-Item -ItemType $kind -Path $alias -Target $source | Out-Null
Assert-Rejected { $null = Assert-ScratchPath "$alias/nested" @($source) } 'linked scratch ancestor'
Assert-Unchanged $source $beforeSource 'Rejected link changed source.'
if (Test-Path -LiteralPath "$source/nested") { throw 'Rejected linked scratch wrote to source.' }

if ($Exe) {
    # Real harness negative paths; these require the built net462 executable on Windows.
    $Exe = Get-UnlinkedPath $Exe
    if (!(Test-Path -LiteralPath $Exe -PathType Leaf)) { throw 'Oracle executable missing.' }
    Assert-Rejected {
        & "$PSScriptRoot/Verify.ps1" -Exe $Exe -Library $source -Scratch "$source/nested" -Golden $golden
    } 'verifier rejects scratch nested in source before writing'
    Assert-Unchanged $source $beforeSource 'Verifier preflight changed source.'
    if (Test-Path -LiteralPath "$source/nested") { throw 'Verifier preflight wrote to source.' }
    Assert-Rejected {
        & "$PSScriptRoot/Verify.ps1" -Exe $Exe -Library $source -Scratch "$alias/nested" -Golden $golden
    } 'verifier rejects linked scratch ancestor before writing'
    Assert-Unchanged $source $beforeSource 'Verifier linked preflight changed source.'

    function Assert-OracleExit([string[]]$Arguments, [int]$Expected) {
        & $Exe @Arguments
        if ($LASTEXITCODE -ne $Expected) { throw "Oracle exit $LASTEXITCODE; expected $Expected for $($Arguments -join ' ')." }
        $script:checks++
    }
    Assert-OracleExit @('library-export', $source, "$source/nested") 1
    Assert-Unchanged $source $beforeSource 'Nested library-export changed source.'
    if (Test-Path -LiteralPath "$source/nested") { throw 'Nested library-export wrote to source.' }
    Assert-OracleExit @('library-export', $source, $source) 1
    $existing = Join-Path $Scratch 'existing-destination'
    New-Item -ItemType Directory -Path $existing | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $existing 'keep'), [byte[]](4, 5))
    Assert-OracleExit @('library-export', $source, $existing) 1
    Assert-Unchanged $existing (Get-TreeState $existing) 'Existing destination changed.'
    Assert-OracleExit @('library-export', $alias, (Join-Path $Scratch 'alias-output')) 1
    if (Test-Path -LiteralPath (Join-Path $Scratch 'alias-output')) { throw 'Linked source export wrote output.' }
    Assert-OracleExit @('library-export', (Join-Path $Scratch 'missing-source'), (Join-Path $Scratch 'missing-copy')) 1
    Assert-OracleExit @('settings-bytes', $source, "$source/nested-settings") 1
    Assert-OracleExit @('settings-bytes', $source, $existing) 1
    Assert-OracleExit @('settings-bytes', (Join-Path $Scratch 'missing-settings'), (Join-Path $Scratch 'missing-settings-output')) 1
    Assert-OracleExit @('settings-bytes') 2
    Assert-OracleExit @('unknown-command', $source, (Join-Path $Scratch 'unused')) 2
    Assert-Unchanged $source $beforeSource 'Harness negatives changed source.'
    if (Test-Path -LiteralPath "$source/nested-settings") { throw 'Nested settings-bytes wrote to source.' }
}

Assert-Goldens $emitted $golden
Write-Host "PASS: $checks bounded comparator/safety checks on synthetic unit data only; no v10 fixture, golden, or Windows acceptance claim."
# Expected-failure oracle invocations leave LASTEXITCODE nonzero; do not leak it as this script's exit.
exit 0
