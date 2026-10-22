param([string]$Version = '11.3.22')
$ErrorActionPreference = 'Stop'
$taskFeed = Join-Path $PSScriptRoot '..\.packages'
New-Item -ItemType Directory -Path $taskFeed -Force | Out-Null
$taskIds = @('avalonia','avalonia.desktop','avalonia.themes.fluent','avalonia.native','avalonia.skia','avalonia.win32','avalonia.x11','avalonia.freedesktop','avalonia.remote.protocol','avalonia.headless')
foreach ($taskId in $taskIds) {
    $taskPackage = Join-Path $taskFeed "$taskId.$Version.nupkg"
    if (-not (Test-Path -LiteralPath $taskPackage)) {
        Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$taskId/$Version/$taskId.$Version.nupkg" -OutFile $taskPackage -TimeoutSec 45
    }
    Write-Output "$taskId $Version"
}
$taskId = 'yamldotnet'
$taskVersion = '18.1.0'
$taskPackage = Join-Path $taskFeed "$taskId.$taskVersion.nupkg"
if (-not (Test-Path -LiteralPath $taskPackage)) { Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$taskId/$taskVersion/$taskId.$taskVersion.nupkg" -OutFile $taskPackage -TimeoutSec 45 }
Write-Output "$taskId $taskVersion"
