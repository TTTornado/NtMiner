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
} else { $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_HOME = Join-Path $workspaceRoot '.tools\dotnet-home'
$packageCache = Join-Path $workspaceRoot '.tools\nuget-packages'
$winuiProject = Join-Path $projectRoot 'src\NTMiner.WinUI\NTMiner.WinUI.csproj'
Push-Location $projectRoot
try {
    & $dotnetPath restore $winuiProject --configfile WinUI.NuGet.Config --packages $packageCache --locked-mode --disable-parallel -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'WinUI restore failed. Check the official NuGet connection and lock file.' }
    & $dotnetPath build $winuiProject -c $Configuration -p:Platform=x64 --no-restore --nologo -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed.' }
    $app = Join-Path $projectRoot "src\NTMiner.WinUI\bin\x64\$Configuration\net10.0-windows10.0.19041.0\win-x64\NTMiner.WinUI.exe"
    if ($Action -eq 'Run') {
        Start-Process -FilePath $app -WorkingDirectory $projectRoot -WindowStyle Hidden
    }
    if ($Action -eq 'Test') {
        $output = Join-Path $projectRoot 'artifacts\winui'
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        $resultPath = Join-Path $output 'result.json'
        if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
        $process = Start-Process -FilePath $app -ArgumentList @('--smoke-test', ('"' + $output + '"')) -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(60000)) {
            $process.Kill()
            throw 'WinUI smoke test timed out. Close any other NtMiner window before testing.'
        }
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $resultPath)) { throw "WinUI smoke test failed (exit $($process.ExitCode))." }
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        if (-not $result.Passed) { throw $result.Error }
        Write-Output ('PASS WinUI smoke test: ' + $result.Steps.Count + ' checks.')
        $result.Steps | ForEach-Object { Write-Output ('  ' + $_) }
    }
} finally { Pop-Location }
