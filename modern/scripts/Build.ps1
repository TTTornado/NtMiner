param(
    [ValidateSet('Build','Test','Run')][string]$Action = 'Build',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$workspaceRoot = Split-Path (Split-Path $projectRoot -Parent) -Parent
$portableSdk = Join-Path $workspaceRoot '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $portableSdk) {
    $dotnetPath = $portableSdk
    $env:DOTNET_ROOT = Split-Path $portableSdk -Parent
} else {
    $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_HOME = Join-Path $workspaceRoot '.tools\dotnet-home'
Push-Location $projectRoot
try {
    & $dotnetPath build NTMiner.Modern.sln -c $Configuration --nologo -m:1 -nr:false "-p:RestoreConfigFile=$projectRoot\NuGet.Config"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. Install the .NET 10 SDK and check the build output.' }
    if ($Action -eq 'Test') {
        & $dotnetPath "tests\NTMiner.Tests\bin\$Configuration\net10.0-windows\NTMiner.Tests.dll"
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
        & $dotnetPath "tests\NTMiner.UiTests\bin\$Configuration\net10.0-windows\NTMiner.UiTests.dll" "$projectRoot\artifacts\ui"
        if ($LASTEXITCODE -ne 0) { throw 'UI smoke test failed.' }
    }
    if ($Action -eq 'Run') {
        Start-Process -FilePath $dotnetPath -ArgumentList ('"' + "$projectRoot\src\NTMiner.Desktop\bin\$Configuration\net10.0-windows\NTMiner.Desktop.dll" + '"') -WorkingDirectory $projectRoot -WindowStyle Hidden
    }
} finally { Pop-Location }
