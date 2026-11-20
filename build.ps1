param([switch]$Test)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$sdk = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { throw 'The workspace SDK is missing. Download .NET 10 Windows x64 SDK into .tools/dotnet first.' }
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools/cli'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools/packages'
$env:APPDATA = Join-Path $taskRoot '.tools/appdata'
$env:LOCALAPPDATA = Join-Path $taskRoot '.tools/localappdata'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $taskRoot '.tools/http-cache'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
& $sdk publish (Join-Path $taskRoot 'Homepage.csproj') -c Release -o (Join-Path $taskRoot 'dist') ('-p:RestoreConfigFile=' + (Join-Path $taskRoot 'NuGet.Config'))
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if ($Test) {
    $testOutput = Join-Path $taskRoot ('test-output/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $exe = Join-Path $taskRoot 'dist/Homepage.exe'
    $process = Start-Process -FilePath $exe -ArgumentList @('--self-test', '--output', ('"' + $testOutput + '"')) -WindowStyle Hidden -Wait -PassThru
    Get-Content -LiteralPath (Join-Path $testOutput 'results.txt')
    if ($process.ExitCode -ne 0) { throw 'Self-tests failed.' }
}
