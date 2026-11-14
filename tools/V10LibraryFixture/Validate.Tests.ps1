Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$validator = Join-Path $PSScriptRoot 'Validate.ps1'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('v10-validate-path-tests-' + [Guid]::NewGuid().ToString('N'))

function Get-TreeSnapshot([string]$Root) {
    $resolved = (Resolve-Path -LiteralPath $Root).ProviderPath
    $lines = @(Get-ChildItem -LiteralPath $resolved -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($resolved.Length).TrimStart([char[]]@('\', '/'))
        if ($_.PSIsContainer) {
            "D $relative"
        }
        else {
            "F $relative $((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
        }
    })
    return ($lines -join "`n")
}

function New-SyntheticFixture([string]$Path) {
    New-Item -ItemType Directory -Path (Join-Path $Path 'library/files') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $Path 'library/database.json'), '{"Version":4}')
    [IO.File]::WriteAllText((Join-Path $Path 'SHA256SUMS'), 'synthetic')
    [IO.File]::WriteAllText((Join-Path $Path 'library/games.db'), 'synthetic')
}

function Invoke-ExpectUnsafeOutput {
    param(
        [string]$Name,
        [string]$Fixture,
        [string]$Output,
        [string]$SourceRoot = ''
    )
    if ([string]::IsNullOrEmpty($SourceRoot)) { $SourceRoot = $Fixture }
    $before = Get-TreeSnapshot $SourceRoot
    $rejection = $null
    $accepted = $false
    try {
        & $validator -Exe (Join-Path $PSScriptRoot 'missing-generator.exe') -Output $Output -Fixture $Fixture
        $accepted = $true
    }
    catch {
        $rejection = $_.Exception.Message
    }
    if ($accepted) { throw "${Name}: validator accepted unsafe output: $Output" }
    if ($rejection -notmatch '^UNSAFE-OUTPUT:') {
        throw "${Name}: expected UNSAFE-OUTPUT rejection, got: $rejection"
    }
    $after = Get-TreeSnapshot $SourceRoot
    if ($before -ne $after) { throw "${Name}: source tree file set or bytes changed on rejection" }
}

New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $fixture = Join-Path $scratch 'fixture'
    New-SyntheticFixture $fixture

    Invoke-ExpectUnsafeOutput 'nested-output' $fixture (Join-Path $fixture 'review-scratch')
    Invoke-ExpectUnsafeOutput 'equal-output' $fixture $fixture
    Invoke-ExpectUnsafeOutput 'parent-output' $fixture $scratch

    $alias = Join-Path $scratch 'fixture-link'
    New-Item -ItemType Junction -Path $alias -Target $fixture | Out-Null
    Invoke-ExpectUnsafeOutput 'linked-output' $fixture (Join-Path $alias 'linked-scratch')

    $repository = Join-Path $scratch 'repository'
    $repositoryFixture = Join-Path $repository 'tests/fixtures/v10'
    New-Item -ItemType Directory -Path (Join-Path $repository '.git') -Force | Out-Null
    New-SyntheticFixture $repositoryFixture
    Invoke-ExpectUnsafeOutput 'repository-internal-output' $repositoryFixture (Join-Path $repository 'scratch') $repository

    Write-Host 'PASS: nested, equal, parent-of, linked and repository-internal outputs rejected before any source write'
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
