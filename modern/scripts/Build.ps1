param(
    [ValidateSet('Build','Test','Run')][string]$Action = 'Build',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$RestorePackages
)
# -RestorePackages: one-time online fill of the workspace package cache, then builds stay offline.
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
# 包只从工作区缓存还原，保持离线可构建；缓存由 -RestorePackages 联网补齐。
$packageCache = Join-Path $workspaceRoot '.tools\nuget-packages'
Push-Location $projectRoot
try {
    if ($RestorePackages) {
        # 写在项目目录内，避免临时目录路径含特殊字符导致 --configfile 解析失败。
        $onlineConfig = Join-Path $projectRoot ('nuget-online.{0}.config' -f [guid]::NewGuid().ToString('N'))
        $configText = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
'@
        try {
            [System.IO.File]::WriteAllText($onlineConfig, $configText, (New-Object System.Text.UTF8Encoding($false)))
            & $dotnetPath restore NTMiner.Modern.sln --configfile $onlineConfig --packages $packageCache
            if ($LASTEXITCODE -ne 0) { throw 'Package restore failed. Check the nuget.org connection.' }
        } finally { Remove-Item -LiteralPath $onlineConfig -Force -ErrorAction SilentlyContinue }
    }
    & $dotnetPath build NTMiner.Modern.sln -c $Configuration --nologo -m:1 -nr:false "-p:RestoreConfigFile=$projectRoot\NuGet.Config" "-p:RestorePackagesPath=$packageCache"
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
