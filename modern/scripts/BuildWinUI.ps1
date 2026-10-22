# ENCODING: must be saved as UTF-8 WITH BOM. Windows PowerShell 5.1 reads these scripts without a BOM
# as ANSI and corrupts the Chinese messages, which breaks parsing. Do not strip the BOM.
param(
    [ValidateSet('Build','Test','Run')][string]$Action = 'Build',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    # Force a full rebuild even when the output looks current.
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ModernToolchain.ps1')

$projectRoot = Split-Path $PSScriptRoot -Parent
$workspaceRoot = Split-Path (Split-Path $projectRoot -Parent) -Parent
$dotnetPath = Initialize-ModernToolchain -WorkspaceRoot $workspaceRoot
$packageCache = Join-Path $workspaceRoot '.tools\nuget-packages'
$winuiProject = Join-Path $projectRoot 'src\NTMiner.WinUI\NTMiner.WinUI.csproj'
$app = Join-Path $projectRoot "src\NTMiner.WinUI\bin\x64\$Configuration\net10.0-windows10.0.19041.0\win-x64\NTMiner.WinUI.exe"
$sourceRoots = @((Join-Path $projectRoot 'src'), (Join-Path $projectRoot 'tests'))
$stamp = Join-Path $projectRoot "artifacts\.build\$Configuration-winui.stamp"

function Invoke-WinUIBuild {
    Assert-NoRunningInstance
    Push-Location $projectRoot
    try {
        # Only restore when the lock file or project changed; --locked-mode makes a
        # redundant restore pure overhead on every start.
        $lockFile = Join-Path $projectRoot 'src\NTMiner.WinUI\packages.lock.json'
        $assetsFile = Join-Path $projectRoot 'src\NTMiner.WinUI\obj\project.assets.json'
        $needsRestore = $Force -or -not (Test-Path -LiteralPath $assetsFile) `
            -or ((Get-Item -LiteralPath $lockFile).LastWriteTimeUtc -gt (Get-Item -LiteralPath $assetsFile).LastWriteTimeUtc)
        if ($needsRestore) {
            Invoke-DotnetStep -DotnetPath $dotnetPath -Description 'WinUI 依赖还原' -Arguments @(
                'restore', $winuiProject, '--configfile', 'WinUI.NuGet.Config',
                '--packages', $packageCache, '--locked-mode', '--disable-parallel', '-p:Platform=x64')
        } else {
            Write-Output '依赖未变化，跳过还原。'
        }
        Invoke-DotnetStep -DotnetPath $dotnetPath -Description 'WinUI 构建' -Arguments @(
            'build', $winuiProject, '-c', $Configuration, '-p:Platform=x64', '--no-restore', '--nologo', '-m:1', '-nr:false')
        Set-BuildStamp -Stamp $stamp
    } finally { Pop-Location }
}

if ($Action -in @('Build', 'Test')) {
    Invoke-WinUIBuild
}

if ($Action -eq 'Run') {
    # Starting the app must not require a build: reuse the existing output unless a
    # source file is newer, the executable is missing, or -Force was passed.
    if ($Force -or (Test-BuildStale -Stamp $stamp -SourceRoots $sourceRoots)) {
        Write-Output '检测到源码变更，正在构建…'
        Invoke-WinUIBuild
    } else {
        Write-Output '源码未变更，直接启动（加 -Force 可强制重建）。'
    }
    Assert-NoRunningInstance -Hint '同一用户一次只能打开一个现代版窗口，请关闭后重试。'
    $started = Start-Process -FilePath $app -WorkingDirectory $projectRoot -PassThru
    # Report a failed launch instead of exiting silently.
    Start-Sleep -Milliseconds 1500
    if ($started.HasExited -and $started.ExitCode -ne 0) {
        Write-Host "启动失败（退出码 $($started.ExitCode)）。$app" -ForegroundColor Red
        exit 1
    }
    Write-Output "已启动：$app"
}

if ($Action -eq 'Test') {
    Assert-NoRunningInstance -Hint '先关闭已打开的 NtMiner 窗口再运行验收。'
    $output = Join-Path $projectRoot 'artifacts\winui'
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $resultPath = Join-Path $output 'result.json'
    if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
    $process = Start-Process -FilePath $app -ArgumentList @('--smoke-test', ('"' + $output + '"')) -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) {
        $process.Kill()
        throw 'WinUI 验收超时。请先关闭其他 NtMiner 窗口再重试。'
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $resultPath)) { throw "WinUI 验收失败（退出码 $($process.ExitCode)）。" }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if (-not $result.Passed) { throw $result.Error }
    Write-Output ('PASS WinUI smoke test: ' + $result.Steps.Count + ' checks.')
    $result.Steps | ForEach-Object { Write-Output ('  ' + $_) }
}
