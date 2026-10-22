# ENCODING: must be saved as UTF-8 WITH BOM. Windows PowerShell 5.1 reads these scripts without a BOM
# as ANSI and corrupts the Chinese messages, which breaks parsing. Do not strip the BOM.
param(
    [ValidateSet('Build','Test','Run')][string]$Action = 'Build',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    # One-time online fill of the workspace package cache; builds stay offline afterwards.
    [switch]$RestorePackages,
    # Force a rebuild when starting via -Action Run.
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ModernToolchain.ps1')

$projectRoot = Split-Path $PSScriptRoot -Parent
$workspaceRoot = Split-Path (Split-Path $projectRoot -Parent) -Parent
$dotnetPath = Initialize-ModernToolchain -WorkspaceRoot $workspaceRoot
$packageCache = Join-Path $workspaceRoot '.tools\nuget-packages'
$desktop = Join-Path $projectRoot "src\NTMiner.Desktop\bin\$Configuration\net10.0-windows\NTMiner.Desktop.dll"
$sourceRoots = @((Join-Path $projectRoot 'src'), (Join-Path $projectRoot 'tests'))
$stamp = Join-Path $projectRoot "artifacts\.build\$Configuration-solution.stamp"

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
        Set-BuildStamp -Stamp $stamp
    } finally { Pop-Location }
}

if ($Action -in @('Build', 'Test')) { Invoke-ModernBuild }

if ($Action -eq 'Run') {
    if ($Force -or (Test-BuildStale -Stamp $stamp -SourceRoots $sourceRoots)) {
        Write-Output '检测到源码变更，正在构建…'
        Invoke-ModernBuild
    } else {
        Write-Output '源码未变更，直接启动（加 -Force 可强制重建）。'
    }
    Assert-NoRunningInstance -Hint '同一用户一次只能打开一个现代版窗口，请关闭后重试。'
    Start-Process -FilePath $dotnetPath -ArgumentList ('"' + $desktop + '"') -WorkingDirectory $projectRoot -WindowStyle Normal
}

if ($Action -eq 'Test') {
    Assert-NoRunningInstance -Hint '先关闭已打开的 NtMiner 窗口再运行验收。'
    Push-Location $projectRoot
    try {
        Invoke-DotnetStep -DotnetPath $dotnetPath -Description '后台验收' -Arguments @("tests\NTMiner.Tests\bin\$Configuration\net10.0-windows\NTMiner.Tests.dll")
        Invoke-DotnetStep -DotnetPath $dotnetPath -Description 'WPF 界面验收' -Arguments @("tests\NTMiner.UiTests\bin\$Configuration\net10.0-windows\NTMiner.UiTests.dll", "$projectRoot\artifacts\ui")
    } finally { Pop-Location }
}
