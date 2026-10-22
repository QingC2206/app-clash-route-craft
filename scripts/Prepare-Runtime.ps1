param([string]$Version = '10.0.13')
$ErrorActionPreference = 'Stop'
$taskFeed = Join-Path $PSScriptRoot '..\.packages'
New-Item -ItemType Directory -Path $taskFeed -Force | Out-Null
foreach ($taskId in @('microsoft.netcore.app.runtime.win-x64','microsoft.dotnet.ilcompiler','runtime.win-x64.microsoft.dotnet.ilcompiler','microsoft.net.illink.tasks','microsoft.windowsdesktop.app.runtime.win-x64','microsoft.aspnetcore.app.runtime.win-x64','microsoft.netcore.app.runtime.nativeaot.win-x64')) {
    $taskPackage = Join-Path $taskFeed "$taskId.$Version.nupkg"
    if (-not (Test-Path -LiteralPath $taskPackage)) { Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$taskId/$Version/$taskId.$Version.nupkg" -OutFile $taskPackage -TimeoutSec 90 }
    Write-Output "$taskId $Version"
}
