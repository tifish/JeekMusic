# Called automatically after a portable framework-dependent App build.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Source)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sourcePath = [IO.Path]::GetFullPath($Source).TrimEnd('\')
$bin = [IO.Path]::GetFullPath((Join-Path $repo 'desktop\src\JeekMusic.App\bin'))
$output = [IO.Path]::GetFullPath((Join-Path $repo 'output'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repo 'desktop\artifacts'))
if (-not $sourcePath.StartsWith($bin + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Daily staging source must be inside $bin; got $sourcePath"
}

function Assert-NoReparsePoint([string]$Path) {
    $current = $Path
    while ($current -and $current.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to stage through a reparse point: $current"
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

# Never delete output/ or unknown files: it can contain a user's own files.
# Only these exact .NET runtime names are migrated from the old shared output.
$runtimeFiles = @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll',
    'clrjit.dll', 'clrgc.dll', 'clretwrc.dll', 'mscordaccore.dll', 'mscordbi.dll',
    'mscorrc.dll', 'createdump.exe', 'Microsoft.DiaSymReader.Native.amd64.dll')
Assert-NoReparsePoint $sourcePath
Assert-NoReparsePoint $output
Assert-NoReparsePoint $artifacts
$config = Get-Content -LiteralPath (Join-Path $sourcePath 'JeekMusic.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not ($config.runtimeOptions.PSObject.Properties.Name -contains 'framework') -and
    -not ($config.runtimeOptions.PSObject.Properties.Name -contains 'frameworks')) {
    throw 'Daily staging requires a framework-dependent runtimeconfig.'
}
foreach ($name in $runtimeFiles) {
    if (Test-Path -LiteralPath (Join-Path $sourcePath $name)) {
        throw "Daily staging source contains a self-contained runtime: $name"
    }
}
$files = @(Get-ChildItem -LiteralPath $sourcePath -File -Recurse -Force)
# Preflight every destination before any migration/copy, including nested symlinks.
foreach ($file in $files) {
    Assert-NoReparsePoint $file.FullName
    $relative = $file.FullName.Substring($sourcePath.Length + 1)
    Assert-NoReparsePoint (Join-Path $output $relative)
}
foreach ($name in $runtimeFiles) { Assert-NoReparsePoint (Join-Path $output $name) }

$backup = Join-Path $artifacts ('legacy-output-runtime-' + [Guid]::NewGuid().ToString('N'))
if (-not ([IO.Path]::GetFullPath($backup)).StartsWith($artifacts + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Invalid runtime migration destination.'
}
foreach ($name in $runtimeFiles) {
    $old = Join-Path $output $name
    if (Test-Path -LiteralPath $old -PathType Leaf) {
        [IO.Directory]::CreateDirectory($backup) | Out-Null
        Move-Item -LiteralPath $old -Destination (Join-Path $backup $name)
        Write-Host "Migrated legacy runtime: $old -> $backup"
    }
}
foreach ($file in $files) {
    $destination = Join-Path $output $file.FullName.Substring($sourcePath.Length + 1)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
}
Write-Host "Daily framework-dependent build staged at $output\JeekMusic.exe"
