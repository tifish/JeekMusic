# Evaluate real MSBuild graphs and reject unsupported daily publishes before staging.
# No client launch, media access or writes to the daily runtime directory.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $repo 'desktop\src\JeekMusic.App'
$common = @('-nologo', '-p:_IsPublishing=true', '-p:Configuration=Release', '-p:RuntimeIdentifier=win-x64', '-p:SelfContained=true', '-p:PublishAot=false')
function Properties([string[]]$Extra) {
    $output = & dotnet msbuild $project @common @Extra '-getProperty:BaseIntermediateOutputPath,BaseOutputPath,ProjectAssetsFile'
    if ($LASTEXITCODE -ne 0) { throw 'MSBuild graph evaluation failed' }
    return ($output -join "`n" | ConvertFrom-Json).Properties
}
$managed = Properties @()
$ready = Properties @('-p:PublishReadyToRun=true', '-p:PublishReadyToRunComposite=false')
$composite = Properties @('-p:PublishReadyToRun=true', '-p:PublishReadyToRunComposite=true')
foreach ($property in @('BaseIntermediateOutputPath', 'BaseOutputPath', 'ProjectAssetsFile')) {
    $unique = @($managed.$property, $ready.$property, $composite.$property | Select-Object -Unique)
    if ($unique.Count -ne 3) { throw "Managed and precompiled graphs share $property" }
}
$errorLog = & dotnet msbuild $project -nologo '-t:StageJeekDailyPublish' '-p:JeekStageDailyPublish=true' '-p:Configuration=Debug' '-p:RuntimeIdentifier=win-x64' '-p:SelfContained=true' '-p:PublishAot=false' '-p:PublishReadyToRun=true' '-p:PublishReadyToRunComposite=true' 2>&1
if ($LASTEXITCODE -eq 0 -or ($errorLog -join "`n") -notmatch 'Daily publishing requires Release') { throw 'Unsupported daily publish was not rejected before staging' }
Write-Output 'PASS: managed, ReadyToRun and composite graph/output/assets isolation; unsupported daily publish rejected before staging.'
