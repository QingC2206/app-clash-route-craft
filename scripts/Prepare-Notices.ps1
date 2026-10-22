$ErrorActionPreference = 'Stop'
$taskDesktop = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskDestination = Join-Path $taskDesktop 'artifacts/licenses'
New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
$taskRepos = @{
    'Avalonia'='AvaloniaUI/Avalonia'
    'AvaloniaEdit'='AvaloniaUI/AvaloniaEdit'
    'YamlDotNet'='aaubry/YamlDotNet'
    'dotnet-runtime'='dotnet/runtime'
    'SkiaSharp'='mono/SkiaSharp'
    'ANGLE'='google/angle'
    'HarfBuzz'='harfbuzz/harfbuzz'
    'Mihomo'='MetaCubeX/mihomo'
}
foreach ($taskName in $taskRepos.Keys) {
    $taskPath = Join-Path $taskDestination ($taskName+'-LICENSE.txt')
    if (-not (Test-Path -LiteralPath $taskPath)) {
        $taskResponse = Invoke-RestMethod -Uri ('https://api.github.com/repos/'+$taskRepos[$taskName]+'/license') -Headers @{'User-Agent'='ClashRouteCraft-build'} -TimeoutSec 30
        [System.IO.File]::WriteAllBytes($taskPath,[System.Convert]::FromBase64String($taskResponse.content))
    }
    Write-Output ($taskName+' license retained')
}
$taskPackageCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$taskRuntime = Join-Path $taskPackageCache 'microsoft.netcore.app.runtime.win-x64/10.0.13'
if (Test-Path -LiteralPath $taskRuntime) {
    Get-ChildItem -LiteralPath $taskRuntime -File | Where-Object { $_.Name -match 'LICENSE|NOTICE' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $taskDestination ('dotnet-'+$_.Name)) }
}
