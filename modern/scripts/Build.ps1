# ENCODING: must be saved as UTF-8 WITH BOM. Windows PowerShell 5.1 reads these scripts without a BOM
# as ANSI and corrupts the Chinese messages, which breaks parsing. Do not strip the BOM.
# Backend build and acceptance for the modern solution. The user-facing app is WinUI;
# use Start-WinUI.cmd to launch it. -Action Run only forwards to that script.
param(
    [ValidateSet('Build','Test','Run')][string]$Action = 'Build',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    # One-time online fill of the workspace package cache; builds stay offline afterwards.
    [switch]$RestorePackages
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ModernToolchain.ps1')

$projectRoot = Split-Path $PSScriptRoot -Parent
$workspaceRoot = Split-Path (Split-Path $projectRoot -Parent) -Parent
$dotnetPath = Initialize-ModernToolchain -WorkspaceRoot $workspaceRoot
$packageCache = Join-Path $workspaceRoot '.tools\nuget-packages'

function Invoke-ModernBuild {
    if ($RestorePackages) {
        # Written inside the project directory: --configfile fails on paths with special characters.
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
            Invoke-DotnetStep -DotnetPath $dotnetPath -Description '依赖联网还原' -Arguments @(
                'restore', 'NTMiner.Modern.sln', '--configfile', $onlineConfig, '--packages', $packageCache)
        } finally { Remove-Item -LiteralPath $onlineConfig -Force -ErrorAction SilentlyContinue }
    }
    Assert-NoRunningInstance
    Push-Location $projectRoot
    try {
        Invoke-DotnetStep -DotnetPath $dotnetPath -Description '解决方案构建' -Arguments @(
            'build', 'NTMiner.Modern.sln', '-c', $Configuration, '--nologo', '-m:1', '-nr:false',
            "-p:RestoreConfigFile=$projectRoot\NuGet.Config", "-p:RestorePackagesPath=$packageCache")
    } finally { Pop-Location }
}

if ($Action -eq 'Run') {
    # There is exactly one desktop frontend now. Keep a single launch path so users and
    # tests cannot start a stale second UI that owns the same profile and pipe.
    $winui = Join-Path $PSScriptRoot 'BuildWinUI.ps1'
    & $winui -Action Run -Configuration $Configuration
    exit $LASTEXITCODE
}

Invoke-ModernBuild

if ($Action -eq 'Test') {
    Assert-NoRunningInstance -Hint '先关闭已打开的 NtMiner 窗口再运行验收。'
    Push-Location $projectRoot
    try {
        Invoke-DotnetStep -DotnetPath $dotnetPath -Description '后台验收' -Arguments @(
            "tests\NTMiner.Tests\bin\$Configuration\net10.0-windows\NTMiner.Tests.dll")
    } finally { Pop-Location }
}
