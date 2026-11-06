# Evaluate real MSBuild graphs and reject unsupported daily publishes before staging.
# No client launch, media access or writes to the daily runtime directory.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $repo 'desktop\src\JeekMusic.App'
$common = @('-nologo', '-p:_IsPublishing=true', '-p:Configuration=Release', '-p:RuntimeIdentifier=win-x64', '-p:SelfContained=false', '-p:PublishAot=false')
function Properties([string[]]$Extra) {
    $output = & dotnet msbuild $project @common @Extra '-getProperty:BaseIntermediateOutputPath,BaseOutputPath,ProjectAssetsFile'
    if ($LASTEXITCODE -ne 0) { throw 'MSBuild graph evaluation failed' }
    return ($output -join "`n" | ConvertFrom-Json).Properties
}
$managed = Properties @()
$ready = Properties @('-p:PublishReadyToRun=true', '-p:PublishReadyToRunComposite=false')
$composite = Properties @('-p:PublishReadyToRun=true', '-p:PublishReadyToRunComposite=true')
$bundled = Properties @('-p:SelfContained=true', '-p:PublishReadyToRun=true', '-p:PublishReadyToRunComposite=true')
foreach ($property in @('BaseIntermediateOutputPath', 'BaseOutputPath', 'ProjectAssetsFile')) {
    $unique = @($managed.$property, $ready.$property, $composite.$property, $bundled.$property | Select-Object -Unique)
    if ($unique.Count -ne 4) { throw "Managed, precompiled or bundled-runtime graphs share $property" }
}
foreach ($invalid in @(@('-p:Configuration=Debug','-p:SelfContained=false'),@('-p:Configuration=Release','-p:SelfContained=true'))) {
    $errorLog = & dotnet msbuild $project -nologo '-t:StageJeekDailyPublish' '-p:JeekStageDailyPublish=true' '-p:RuntimeIdentifier=win-x64' '-p:PublishAot=false' '-p:PublishReadyToRun=true' '-p:PublishReadyToRunComposite=true' @invalid 2>&1
    if ($LASTEXITCODE -eq 0 -or ($errorLog -join "`n") -notmatch 'Daily publishing requires Release') { throw 'Unsupported daily publish was not rejected before staging' }
}
$errorLog = & dotnet msbuild $project -nologo '-t:StageJeekDailyBuild' '-p:JeekStageDailyBuild=true' '-p:RuntimeIdentifier=win-x64' '-p:SelfContained=true' '-p:PublishAot=false' 2>&1
if ($LASTEXITCODE -eq 0 -or ($errorLog -join "`n") -notmatch 'Daily player staging requires') { throw 'Bundled runtime accepted by the daily debug build' }
Write-Output 'PASS: framework-dependent/precompiled/bundled graph/output/assets isolation; unsupported publishes and bundled daily builds rejected before staging.'
