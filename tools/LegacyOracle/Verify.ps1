param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Scratch,
    [ValidateSet('Validate', 'Generate', 'GenerateSettings')][string]$Mode = 'Validate',
    [string]$Library,
    [string]$LibraryManifest,
    [string]$Golden = (Join-Path $PSScriptRoot '../../tests/golden')
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'The v10 oracle requires Windows.' }
. "$PSScriptRoot/Validation.ps1"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$Exe = Get-UnlinkedPath $Exe
$Golden = Get-UnlinkedPath $Golden
if (!(Test-Path -LiteralPath $Exe -PathType Leaf)) { throw 'Oracle executable missing.' }
$readOnly = @($repo, $Exe, $Golden)
if ($Library) { $Library = Get-UnlinkedPath $Library; $readOnly += $Library }
if ($LibraryManifest) { $LibraryManifest = Get-UnlinkedPath $LibraryManifest; $readOnly += $LibraryManifest }
# All canonical relationships and existing ancestors are checked BEFORE any write.
$Scratch = Assert-ScratchPath $Scratch $readOnly
$beforeGolden = Get-TreeState $Golden
$beforeLibrary = if ($Library) { Get-TreeState $Library }

$outcome = $null
$failure = $null
try {
    New-Item -ItemType Directory -Path $Scratch | Out-Null
    function Invoke-Oracle([string[]]$Arguments, [int]$Expected = 0) {
        & $Exe @Arguments
        if ($LASTEXITCODE -ne $Expected) { throw "Oracle exit $LASTEXITCODE; expected $Expected." }
    }

    # Synthetic input only; these are v10 public input properties, not a new output schema.
    # Pin every reachable constructor/ambient dependency.
    $settings = Join-Path $Scratch 'settings-input'
    New-Item -ItemType Directory -Path $settings | Out-Null
    [IO.File]::WriteAllText((Join-Path $settings 'config.json'),
        '{"Version":7,"InstallInstanceId":"30000000-0000-0000-0000-000000000001","BackgroundImageAnimation":false}')
    [IO.File]::WriteAllText((Join-Path $settings 'fullscreenConfig.json'), '{"Monitor":0}')
    [IO.File]::WriteAllText((Join-Path $settings 'windowPositions.json'), '{}')
    $beforeSettings = Get-TreeState $settings

    $first = Join-Path $Scratch 'settings-first'
    $second = Join-Path $Scratch 'settings-second'
    $repeat = Join-Path $Scratch 'settings-repeat'
    Invoke-Oracle @('settings-bytes', $settings, $first)
    Invoke-Oracle @('settings-bytes', $first, $second)
    Invoke-Oracle @('settings-bytes', $settings, $repeat)
    foreach ($name in $SettingsFiles) {
        Assert-SameBytes (Join-Path $first $name) (Join-Path $second $name)
        Assert-SameBytes (Join-Path $first $name) (Join-Path $repeat $name)
    }
    $config = Get-Content -LiteralPath (Join-Path $first 'config.json') -Raw | ConvertFrom-Json
    $fullscreen = Get-Content -LiteralPath (Join-Path $first 'fullscreenConfig.json') -Raw | ConvertFrom-Json
    if ($config.InstallInstanceId -cne '30000000-0000-0000-0000-000000000001' -or
        $config.BackgroundImageAnimation -cne $false -or $fullscreen.Monitor -cne 0) {
        throw 'Public synthetic settings overrides did not survive v10 serialization.'
    }
    Invoke-Oracle @('settings-bytes', (Join-Path $Scratch 'missing'), (Join-Path $Scratch 'missing-output')) 1
    Invoke-Oracle @('settings-bytes') 2
    $invalid = Join-Path $Scratch 'invalid-settings'
    New-Item -ItemType Directory -Path $invalid | Out-Null
    Copy-Item -LiteralPath (Join-Path $settings 'fullscreenConfig.json'), (Join-Path $settings 'windowPositions.json') -Destination $invalid
    [IO.File]::WriteAllText((Join-Path $invalid 'config.json'), '{invalid json')
    Invoke-Oracle @('settings-bytes', $invalid, (Join-Path $Scratch 'invalid-output')) 1
    Assert-TreeUnchanged $settings $beforeSettings 'Source settings changed.'

    $payload = Join-Path $Scratch 'payload'
    New-Item -ItemType Directory -Path "$payload/settings" | Out-Null
    foreach ($name in $SettingsFiles) {
        Copy-Item -LiteralPath (Join-Path $first $name) -Destination "$payload/settings"
    }

    if ($Mode -ne 'GenerateSettings') {
        if (!$Library -or !$LibraryManifest) {
            throw 'Approved library and SHA256 manifest are required; acceptance pending.'
        }
        Assert-HashInventory $Library $LibraryManifest $LibraryFiles
        $copy = Join-Path $Scratch 'library-copy'
        Invoke-Oracle @('library-export', $Library, $copy)
        Assert-SameTree $Library $copy
        Invoke-Oracle @('library-export', $Library, $copy) 1
        Copy-Item -LiteralPath $copy -Destination "$payload/library" -Recurse
        Assert-TreeUnchanged $Library $beforeLibrary 'Source library changed.'
    }
    Assert-Names @((Get-TreeHashes $payload).Keys) $PayloadFiles

    if ($Mode -eq 'Validate') {
        Assert-Goldens $payload $Golden
        $outcome = 'PASS: exact 21 golden payloads and path/hash manifest; settings roundtrip/repeat; raw library bytes; source preserved.'
    } else {
        $outcome = "GENERATED $($PayloadFiles.Count) scratch payloads for review: protected acceptance, fixture and Windows evidence remain pending. No protected write or automatic manifest acceptance."
    }
} catch {
    $failure = $_
} finally {
    try {
        Assert-TreeUnchanged $Golden $beforeGolden 'Protected goldens changed.'
        if ($Library) { Assert-TreeUnchanged $Library $beforeLibrary 'Source library changed.' }
    } catch {
        if (!$failure) { $failure = $_ }
    }
}
if ($failure) { throw $failure }
Write-Host $outcome
