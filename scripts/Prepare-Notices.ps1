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
$taskHeaders = @{'User-Agent'='ClashRouteCraft-build'}
if ($env:GH_TOKEN) { $taskHeaders.Authorization = 'Bearer ' + $env:GH_TOKEN }
foreach ($taskName in $taskRepos.Keys) {
    $taskPath = Join-Path $taskDestination ($taskName+'-LICENSE.txt')
    if (-not (Test-Path -LiteralPath $taskPath)) {
        $taskUri = 'https://api.github.com/repos/'+$taskRepos[$taskName]+'/license'
        if ($taskName -eq 'Mihomo') { $taskUri += '?ref=v1.19.28' }
        $taskResponse = Invoke-RestMethod -Uri $taskUri -Headers $taskHeaders -TimeoutSec 60
        [System.IO.File]::WriteAllBytes($taskPath,[System.Convert]::FromBase64String($taskResponse.content))
    }
    Write-Output ($taskName+' license retained')
}
$taskPackageCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$taskAssets = Get-Content -LiteralPath (Join-Path $taskDesktop 'src/ClashRouteCraft.Desktop/obj/project.assets.json') -Raw | ConvertFrom-Json
$taskRuntimeDownload = @($taskAssets.project.frameworks.'net10.0'.downloadDependencies | Where-Object name -eq 'Microsoft.NETCore.App.Runtime.win-x64')
if ($taskRuntimeDownload.Count -ne 1) { throw 'Publish win-x64 before collecting runtime licenses.' }
$taskRuntimeVersion = $taskRuntimeDownload[0].version.Trim('[',']').Split(',')[0].Trim()
$taskRuntime = Join-Path $taskPackageCache ('microsoft.netcore.app.runtime.win-x64/' + $taskRuntimeVersion)
foreach ($taskName in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) {
    Copy-Item -LiteralPath (Join-Path $taskRuntime $taskName) -Destination (Join-Path $taskDestination ('dotnet-'+$taskName))
}
Write-Output ('Runtime license version: ' + $taskRuntimeVersion)
