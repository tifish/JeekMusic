# Isolated staging/migration regression. Does not launch a client or touch user data.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$fixture = Join-Path $repo ('desktop\artifacts\runtime layout test ' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $fixture 'desktop\src\JeekMusic.App\bin\fixture'
$launcher = Join-Path $fixture 'desktop\src\JeekMusic.App\obj\daily\JeekMusic.exe'
$script = Join-Path $fixture 'desktop\tools\stage-daily-build.ps1'

function Put([string]$Path, [string]$Text) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text)
}
function Check([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
Put $launcher 'launcher'
Put $script ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'stage-daily-build.ps1')))
Put (Join-Path $source 'JeekMusic.exe') 'original launcher'
Put (Join-Path $source 'JeekMusic.dll') 'application'
Put (Join-Path $source 'hostfxr.dll') 'runtime'
Put (Join-Path $source 'JeekMusic.pdb') 'debug symbols'
Put (Join-Path $source 'native.lib') 'linking library'
Put (Join-Path $source 'portable.json') '{}'
Put (Join-Path $source 'Models\yamnet.onnx') 'current model'
Put (Join-Path $source 'Preferences\Runtime\worker.py') 'current worker'
Put (Join-Path $source 'licenses\license.txt') 'license'
Put (Join-Path $fixture 'output\Models\downloaded.onnx') 'downloaded model'
Put (Join-Path $fixture 'output\debug-build.log') 'old build log'
Put (Join-Path $fixture 'output\unknown-user-file.txt') 'keep me'
Put (Join-Path $fixture 'bin\data\settings.json') 'portable user settings'
& $script -Source $source -Launcher $launcher -MigrateLegacyData
$bin = Join-Path $fixture 'bin'
Check ([IO.File]::ReadAllText((Join-Path $bin 'JeekMusic.exe')) -eq 'launcher') 'Launcher not staged.'
Check (Test-Path -LiteralPath (Join-Path $bin 'runtime\JeekMusic.dll')) 'Missing nested application.'
Check (Test-Path -LiteralPath (Join-Path $bin 'runtime\hostfxr.dll')) 'Missing bundled runtime.'
Check (Test-Path -LiteralPath (Join-Path $bin 'Models\downloaded.onnx')) 'Downloaded model not migrated.'
Check (Test-Path -LiteralPath (Join-Path $bin 'Preferences\Runtime\worker.py')) 'Missing worker.'
Check (-not (Test-Path -LiteralPath (Join-Path $bin 'runtime\JeekMusic.pdb'))) 'Debug symbols leaked.'
Check (-not (Test-Path -LiteralPath (Join-Path $bin 'runtime\native.lib'))) 'Build library leaked.'
Check (-not (Test-Path -LiteralPath (Join-Path $fixture 'output'))) 'Legacy output not archived.'
$archive = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'desktop\artifacts') -Directory -Filter 'legacy-output-*')
Check ($archive.Count -eq 1) 'Missing archive.'
Check (Test-Path -LiteralPath (Join-Path $archive[0].FullName 'unknown-user-file.txt')) 'Unknown legacy file lost.'
Check (-not (Test-Path -LiteralPath (Join-Path $bin 'debug-build.log'))) 'Build log leaked into bin.'
Put (Join-Path $bin 'runtime\unknown.dll') 'unmanaged user file'

# Source removal simulates an obsolete dependency. Only manifest-owned files move.
$obsolete = [IO.Path]::GetFullPath((Join-Path $source 'hostfxr.dll'))
Check ($obsolete.StartsWith($fixture + '\', [StringComparison]::OrdinalIgnoreCase)) 'Unsafe test removal.'
Remove-Item -LiteralPath $obsolete
& $script -Source $source -Launcher $launcher -MigrateLegacyData
Check (-not (Test-Path -LiteralPath (Join-Path $bin 'runtime\hostfxr.dll'))) 'Obsolete runtime survived.'
Check (Test-Path -LiteralPath (Join-Path $bin 'runtime\unknown.dll')) 'Unknown runtime file removed.'
Check ([IO.File]::ReadAllText((Join-Path $bin 'data\settings.json')) -eq 'portable user settings') 'Existing data overwritten.'
Check ([IO.File]::ReadAllText((Join-Path $bin 'Models\downloaded.onnx')) -eq 'downloaded model') 'Extra model overwritten.'

# A tampered manifest cannot move files outside runtime/.
Put (Join-Path $fixture 'desktop\artifacts\daily-runtime-files.json') '["data/settings.json"]'
$rejected = $false
try { & $script -Source $source -Launcher $launcher } catch { $rejected = $_.Exception.Message -match 'Invalid staged runtime path' }
Check $rejected 'Unsafe manifest path accepted.'
Check ([IO.File]::ReadAllText((Join-Path $bin 'data\settings.json')) -eq 'portable user settings') 'Data affected by invalid manifest.'

Put (Join-Path $fixture 'desktop\artifacts\daily-runtime-files.json') '[]'
Put (Join-Path $source 'linked\target.dll') 'must not escape'
$outside = Join-Path $fixture 'outside'
[IO.Directory]::CreateDirectory($outside) | Out-Null
$junction = Join-Path $bin 'runtime\linked'
New-Item -ItemType Junction -Path $junction -Value $outside | Out-Null
$rejected = $false
try { & $script -Source $source -Launcher $launcher } catch { $rejected = $_.Exception.Message -match 'reparse point' }
Check $rejected 'Destination junction accepted.'
Check (-not (Test-Path -LiteralPath (Join-Path $outside 'target.dll'))) 'Staging escaped through a junction.'

Write-Output "PASS: runtime layout, space-containing paths, asset migration, legacy archive, data preservation, obsolete-file isolation, traversal and junction guards. Evidence: $fixture"
