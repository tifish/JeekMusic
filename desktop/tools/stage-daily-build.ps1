# Stage only runtime files. Build products and migration backups stay in artifacts/.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Launcher,
    [switch]$MigrateLegacyData
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sourcePath = [IO.Path]::GetFullPath($Source).TrimEnd('\')
$buildRoot = [IO.Path]::GetFullPath((Join-Path $repo 'desktop\src\JeekMusic.App\bin'))
$destination = Join-Path $repo 'bin'
$legacy = Join-Path $repo 'output'
$artifacts = Join-Path $repo 'desktop\artifacts'
if (-not $sourcePath.StartsWith($buildRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Daily staging source must be inside $buildRoot; got $sourcePath"
}
$launcherPath = [IO.Path]::GetFullPath($Launcher)
$intermediateRoot = Join-Path $repo 'desktop\src\JeekMusic.App\obj'
if (-not $launcherPath.StartsWith($intermediateRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Daily launcher must be inside $intermediateRoot; got $launcherPath"
}

$checkedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Assert-NoReparsePoint([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ($checkedPaths.Contains($current)) { break }
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to stage through a reparse point: $current"
        }
        if (Test-Path -LiteralPath $current) { [void]$checkedPaths.Add($current) }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

function Copy-TreePreserving([string]$From, [string]$To, [string]$Exclude = '(?!)') {
    if (-not (Test-Path -LiteralPath $From -PathType Container)) { return }
    Assert-NoReparsePoint $From
    Assert-NoReparsePoint $To
    $entries = [Collections.Generic.List[IO.FileSystemInfo]]::new()
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($From)
    while ($pending.Count) {
        foreach ($entry in @(Get-ChildItem -LiteralPath $pending.Dequeue() -Force)) {
            $relative = $entry.FullName.Substring($From.Length + 1)
            if ($relative -match $Exclude) { continue }
            Assert-NoReparsePoint $entry.FullName
            $entries.Add($entry)
            if ($entry.PSIsContainer) { $pending.Enqueue($entry.FullName) }
        }
    }
    foreach ($entry in $entries) {
        Assert-NoReparsePoint $entry.FullName
        Assert-NoReparsePoint (Join-Path $To $entry.FullName.Substring($From.Length + 1))
    }
    [IO.Directory]::CreateDirectory($To) | Out-Null
    foreach ($entry in $entries) {
        $target = Join-Path $To $entry.FullName.Substring($From.Length + 1)
        if ($entry.PSIsContainer) { [IO.Directory]::CreateDirectory($target) | Out-Null }
        elseif (-not (Test-Path -LiteralPath $target)) {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            [IO.File]::Copy(('\\?\' + $entry.FullName), ('\\?\' + $target))
        }
    }
}

foreach ($path in @($sourcePath, $launcherPath, $destination, $legacy, $artifacts)) { Assert-NoReparsePoint $path }
if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) { throw 'Daily launcher is missing.' }
$files = @(Get-ChildItem -LiteralPath $sourcePath -File -Recurse -Force | Where-Object {
    $_.Extension -notin @('.pdb', '.lib', '.xml') -and $_.Name -ne 'JeekMusic.exe'
})
$plan = foreach ($file in $files) {
    $relative = $file.FullName.Substring($sourcePath.Length + 1)
    if ($relative -notmatch '^(Models|Preferences|licenses)\\' -and $relative -notin @('portable.json', 'LICENSE.txt')) {
        $relative = 'runtime\' + $relative
    }
    $target = Join-Path $destination $relative
    Assert-NoReparsePoint $file.FullName
    Assert-NoReparsePoint $target
    [pscustomobject]@{ Source = $file.FullName; Target = $target; Relative = $relative }
}
Assert-NoReparsePoint (Join-Path $destination 'JeekMusic.exe')

# Preserve downloaded assets, then archive the entire old mixed directory.
# Unknown files are retained in the archive, never deleted or copied into the player.
if (Test-Path -LiteralPath $legacy -PathType Container) {
    foreach ($entry in @(Get-ChildItem -LiteralPath $legacy -Recurse -Force)) { Assert-NoReparsePoint $entry.FullName }
    foreach ($name in @('Models', 'Preferences', 'licenses')) {
        Copy-TreePreserving (Join-Path $legacy $name) (Join-Path $destination $name)
    }
    $backup = [IO.Path]::GetFullPath((Join-Path $artifacts ('legacy-output-' + [guid]::NewGuid().ToString('N'))))
    if ($legacy -ne (Join-Path $repo 'output') -or -not $backup.StartsWith($artifacts + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Invalid legacy archive paths.'
    }
    [IO.Directory]::CreateDirectory($artifacts) | Out-Null
    Move-Item -LiteralPath $legacy -Destination $backup
    Write-Host "Archived old output at $backup"
}

# Only the explicit build-and-run workflow imports the previous data.
# Never overwrite portable data; retain the original as backup.
$data = Join-Path $destination 'data'
if ($MigrateLegacyData -and -not $env:JEEKMUSIC_DATA -and -not (Test-Path -LiteralPath $data)) {
    $oldData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'JeekMusic'
    if (Test-Path -LiteralPath $oldData -PathType Container) {
        $pendingData = Join-Path $artifacts ('portable-data-' + [guid]::NewGuid().ToString('N'))
        # Python virtual environments and in-flight jobs contain machine-specific
        # absolute paths. Reinstall those on demand; preserve learned models/vectors.
        $exclude = '^preference-components\\(venv|job-[^\\]+)(\\|$)|^preference-components\\(ready|runtime-install|torch-install)\.json$'
        Copy-TreePreserving $oldData $pendingData $exclude
        Assert-NoReparsePoint $data
        Move-Item -LiteralPath $pendingData -Destination $data
        Write-Host "Imported existing data into $data (original retained)."
    }
}

# Move only obsolete files recorded by our previous staging into a backup.
# Also removes a self-contained host when switching to a framework-dependent build.
$manifest = Join-Path $artifacts 'daily-runtime-files.json'
Assert-NoReparsePoint $manifest
$oldManifest = Join-Path $destination 'runtime\staged-files.json'
Assert-NoReparsePoint $oldManifest
if (Test-Path -LiteralPath $oldManifest -PathType Leaf) {
    [IO.Directory]::CreateDirectory($artifacts) | Out-Null
    Move-Item -LiteralPath $oldManifest -Destination $manifest -Force
}
if (Test-Path -LiteralPath $manifest -PathType Leaf) {
    $previous = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    $retired = Join-Path $artifacts ('retired-runtime-' + [guid]::NewGuid().ToString('N'))
    foreach ($relative in $previous) {
        $old = [IO.Path]::GetFullPath((Join-Path $destination $relative))
        if (-not $old.StartsWith((Join-Path $destination 'runtime') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Invalid staged runtime path: $relative"
        }
        if ($relative -notin @($plan.Relative) -and (Test-Path -LiteralPath $old -PathType Leaf)) {
            Assert-NoReparsePoint $old
            $backup = Join-Path $retired $relative
            Assert-NoReparsePoint $backup
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup)) | Out-Null
            Move-Item -LiteralPath $old -Destination $backup
        }
    }
}
foreach ($item in $plan) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($item.Target)) | Out-Null
    Copy-Item -LiteralPath $item.Source -Destination $item.Target -Force
}
Copy-Item -LiteralPath $launcherPath -Destination (Join-Path $destination 'JeekMusic.exe') -Force
[IO.Directory]::CreateDirectory($artifacts) | Out-Null
@($plan.Relative | Where-Object { $_.StartsWith('runtime\') }) | ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding UTF8
Write-Host "Daily player staged at $destination\JeekMusic.exe"
