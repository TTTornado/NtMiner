# ENCODING: must be saved as UTF-8 WITH BOM. Windows PowerShell 5.1 reads these scripts without a BOM
# as ANSI and corrupts the Chinese messages, which breaks parsing. Do not strip the BOM.
# Shared helpers for the modern build/start scripts.
# Dot-sourced by Build.ps1 and BuildWinUI.ps1 so both use the same detection rules.

function Initialize-ModernToolchain {
    param([Parameter(Mandatory)][string]$WorkspaceRoot)

    $portableSdk = Join-Path $WorkspaceRoot '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $portableSdk) {
        $env:DOTNET_ROOT = Split-Path $portableSdk -Parent
        $dotnet = $portableSdk
    } else {
        $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_CLI_HOME = Join-Path $WorkspaceRoot '.tools\dotnet-home'
    return $dotnet
}

# Running NtMiner processes hold the build output open, which makes MSBuild fail with
# MSB3021/MSB3027 instead of overwriting the DLLs.
function Get-ModernRunningProcesses {
    return @(Get-Process -Name 'NTMiner.WinUI', 'NTMiner.Agent', 'NTMiner.FakeMiner' -ErrorAction SilentlyContinue)
}

# Reports the condition and exits non-zero: callers show a readable message rather than a
# stack trace, and the .cmd wrapper still pauses so the message stays visible.
function Assert-NoRunningInstance {
    param([string]$Hint = '请先关闭已打开的程序窗口，然后重试。')

    $running = Get-ModernRunningProcesses
    if ($running.Count -eq 0) { return }
    $names = ($running | Select-Object -ExpandProperty ProcessName -Unique) -join '、'
    Write-Host "NtMiner 正在运行（$names），构建输出被占用。$Hint" -ForegroundColor Yellow
    exit 1
}

# True when the build stamp is missing or any source file is newer than it.
# The executable's own timestamp is unreliable: MSBuild keeps the previous file when the new
# output would be byte-identical, so a successful build can leave an older exe in place.
function Test-BuildStale {
    param(
        [Parameter(Mandatory)][string]$Stamp,
        [Parameter(Mandatory)][string[]]$SourceRoots,
        [string[]]$Extensions = @('*.cs', '*.xaml', '*.csproj', '*.json', '*.manifest')
    )

    if (-not (Test-Path -LiteralPath $Stamp)) { return $true }
    $builtAt = (Get-Item -LiteralPath $Stamp).LastWriteTimeUtc
    foreach ($root in $SourceRoots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $newer = Get-ChildItem -LiteralPath $root -Recurse -File -Include $Extensions -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.LastWriteTimeUtc -gt $builtAt } |
            Select-Object -First 1
        if ($null -ne $newer) { return $true }
    }
    return $false
}

# Records a successful build so the next start can skip rebuilding.
function Set-BuildStamp {
    param([Parameter(Mandatory)][string]$Stamp)
    New-Item -ItemType Directory -Path (Split-Path $Stamp -Parent) -Force | Out-Null
    Set-Content -LiteralPath $Stamp -Value (Get-Date).ToString('o') -Encoding ASCII
}

# Runs dotnet and reports the first actionable error instead of a generic failure.
#
# 输出不经过任何 PowerShell 管道。把原生进程的输出重定向进变量（或 Tee-Object）会让
# PowerShell 用 [Console]::OutputEncoding 解码字节，而该编码是本机 OEM 代码页 936，
# dotnet 在控制台场景下却写 UTF-8，于是中文变成 "0 个错�?" 这类乱码。
# 这里改为：正常情况让子进程直接写控制台（不捕获、不乱码）；仅失败时再启动一次同样的
# 命令并把输出重定向到文件，用 UTF-8 读取文件来提取错误行。
function Invoke-DotnetStep {
    param(
        [Parameter(Mandatory)][string]$DotnetPath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Description
    )

    & $DotnetPath @Arguments
    $code = $LASTEXITCODE
    if ($code -eq 0) { return }

    # 仅失败时收集诊断信息，此时再启动一次并把输出写进文件，避免解码问题。
    $log = Join-Path ([System.IO.Path]::GetTempPath()) ('ntminer-step-' + [guid]::NewGuid().ToString('N') + '.log')
    try {
        $quoted = ($Arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $DotnetPath
        $psi.Arguments = $quoted
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
        $psi.WorkingDirectory = (Get-Location).Path
        $proc = [System.Diagnostics.Process]::Start($psi)
        $all = $proc.StandardOutput.ReadToEnd() + $proc.StandardError.ReadToEnd()
        $proc.WaitForExit()
        $lines = @($all -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -match 'error|错误' })
        if ($lines.Count -eq 0) { throw "$Description 失败（退出码 $code）。" }
        if (($lines -join "`n") -match 'MSB3021|MSB3027|被锁定|being used by another process') {
            throw "$Description 失败：输出文件仍被占用。请先关闭已打开的程序窗口。"
        }
        $first = $lines | Select-Object -First 3
        throw "$Description 失败：`n" + ($first -join "`n")
    } finally { Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue }
}