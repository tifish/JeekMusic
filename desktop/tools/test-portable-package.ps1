param([string]$Exe = "$PSScriptRoot\..\..\bin\JeekMusic.exe", [switch]$Relocate)
$ErrorActionPreference = 'Stop'
# -Relocate exercises native decoding/model loading with fresh data, no playback.
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$Exe = [IO.Path]::GetFullPath($Exe)
$evidence = Join-Path $root ('desktop\artifacts\portable-check-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($evidence) | Out-Null
if ($Relocate) {
    $package = Join-Path $evidence 'copied package with spaces'
    & robocopy.exe ([IO.Path]::GetDirectoryName($Exe)) $package /E /XD data /R:0 /W:0 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw 'Package copy failed' }
    $Exe = Join-Path $package 'JeekMusic.exe'
}
$start = [Diagnostics.ProcessStartInfo]::new($Exe, '--mcp')
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.StandardOutputEncoding = [Text.Encoding]::UTF8
$start.WorkingDirectory = $env:TEMP
$start.EnvironmentVariables.Remove('JEEKMUSIC_DATA')
$start.EnvironmentVariables['DOTNET_ROOT'] = Join-Path $evidence 'no-global-dotnet'
$start.EnvironmentVariables['DOTNET_ROOT_X64'] = Join-Path $evidence 'no-global-dotnet'
$start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$start.EnvironmentVariables['DOTNET_HOST_TRACE'] = '1'
$start.EnvironmentVariables['DOTNET_HOST_TRACEFILE'] = Join-Path $evidence 'host-trace.log'
$p = [Diagnostics.Process]::Start($start)
$script:id = 0
function Request([string]$method, $parameters) {
    $script:id++
    $message = @{ jsonrpc='2.0'; id=$script:id; method=$method; params=$parameters } | ConvertTo-Json -Depth 40 -Compress
    $p.StandardInput.WriteLine($message); $p.StandardInput.Flush()
    $read = $p.StandardOutput.ReadLineAsync()
    if (-not $read.Wait(45000)) { throw "MCP timeout: $method" }
    if (-not $read.Result) { throw "MCP disconnected: $($p.StandardError.ReadToEnd())" }
    $result = $read.Result | ConvertFrom-Json
    if ($result.error) { throw ($result.error | ConvertTo-Json -Compress) }
    return $result.result
}
function Call([string]$name, $arguments = @{}) {
    $result = Request 'tools/call' @{name=$name; arguments=$arguments}
    if ($result.isError) { throw ($result.content | ConvertTo-Json -Depth 10) }
    return ($result.content[0].text | ConvertFrom-Json)
}
$diagnostic = $null
try {
    $null = Request 'initialize' @{protocolVersion='2025-11-25'; capabilities=@{}; clientInfo=@{name='portable-check';version='1'}}
    $p.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $p.StandardInput.Flush()
    $catalog = Request 'tools/list' @{}
    $catalog | ConvertTo-Json -Depth 80 | Set-Content (Join-Path $evidence 'catalog.json') -Encoding UTF8
    $diagnostic = Call 'get_diagnostics'
    $diagnostic | ConvertTo-Json -Depth 80 | Set-Content (Join-Path $evidence 'diagnostics.json') -Encoding UTF8
    $appRoot = [IO.Path]::GetDirectoryName($Exe)
    if (-not $diagnostic.portable) { throw 'Portable marker not recognized' }
    if ($diagnostic.dataDirectorySource -ne 'portable') { throw 'Portable data not selected' }
    if ($diagnostic.appDirectory.TrimEnd('\') -ne $appRoot) { throw 'Incorrect app root' }
    if ($diagnostic.dataDirectory.TrimEnd('\') -ne (Join-Path $appRoot 'data')) { throw 'Incorrect portable data root' }
    if ($diagnostic.lastPlaybackError) { throw 'Native audio engine reported a load error' }
    if (-not (Test-Path (Join-Path $diagnostic.modelsDirectory 'yamnet.onnx'))) { throw 'Model path missing' }
    if (-not (Test-Path (Join-Path $diagnostic.preferencesDirectory 'Runtime\preference_worker.py'))) { throw 'Worker path missing' }
    if ($Relocate) {
        $null = Call 'show_window'
        $music = Join-Path $evidence 'fixture'
        [IO.Directory]::CreateDirectory($music) | Out-Null
        Add-Type @"
using System; using System.IO;
public static class PortableTone {
    public static void Write(string path) {
        const int rate = 16000, count = rate * 12;
        using (var w = new BinaryWriter(File.Create(path))) {
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + count * 2);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
            w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2);
            w.Write((short)2); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(count * 2);
            for (int i=0;i<count;i++) w.Write((short)(Math.Sin(2*Math.PI*440*i/rate)*3000));
        }
    }
}
"@
        [PortableTone]::Write((Join-Path $music 'portable-tone.wav'))
        $null = Call 'add_folder' @{path=$music}
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            $tracks = Call 'search_tracks' @{query='portable-tone';limit=1}
            if (@($tracks.items).Count) { break }
            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $deadline)
        if (-not @($tracks.items).Count) { throw 'Fixture scan failed' }
        $null = Call 'diagnose_analysis' @{action='start';trackId=$tracks.items[0].id;budgetSeconds=30}
        $deadline = [DateTime]::UtcNow.AddSeconds(35)
        do {
            $analysis = Call 'diagnose_analysis' @{action='status'}
            if ($analysis.state -notin @('running','cancelling')) { break }
            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $deadline)
        $analysis | ConvertTo-Json -Depth 80 | Set-Content (Join-Path $evidence 'analysis.json') -Encoding UTF8
        if ($analysis.state -ne 'completed') { throw "Portable model/decoder failed: $($analysis.state) / $($analysis.error)" }
        if ($analysis.summary.modelSha256 -ne (Get-FileHash (Join-Path $diagnostic.modelsDirectory 'yamnet.onnx')).Hash.ToLowerInvariant()) { throw 'Wrong relocated model' }
        Write-Output 'PASS: relocated native decoder and ONNX model; bounded synthetic-WAV diagnostics, no playback.'
    }
    Write-Output "PASS: $($catalog.tools.Count) MCP tools; portable data and app/model/worker paths. Evidence: $evidence"
    Write-Output "Player PID: $($diagnostic.processId); runtime: $($diagnostic.runtimeDirectory)"
} finally {
    $p.StandardInput.Close()
    if (-not $p.WaitForExit(5000)) { $p.Kill() }
    if ($Relocate) {
        Get-Process JeekMusic -ErrorAction SilentlyContinue | Where-Object Path -eq $Exe |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }
}
