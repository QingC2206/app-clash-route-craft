# Run in a clean checkout. Never point this at an installed personal workspace.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run with PowerShell 7 (pwsh).' }
$PSNativeCommandUseErrorActionPreference = $true
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $taskRoot
try {
    python scripts/release-guard.py source
    # A clean staging path avoids touching the user's normal AOT installation.
    dotnet run --project tests/ClashRouteCraft.Tests -c Release
    dotnet run --project tests/ClashRouteCraft.UiTests -c Release -- artifacts/release-ci-ui
    python -m unittest discover -s tests -p test_router_asn_installer.py -v
    python -m unittest discover -s tests -p test_release_guard.py -v
    dotnet publish src/ClashRouteCraft.Desktop -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:ContinuousIntegrationBuild=true -o artifacts/release-ci-aot
    ./scripts/Prepare-Notices.ps1
    ./scripts/check-package-data.ps1 -PublishDirectory artifacts/release-ci-aot
    ./scripts/Package.ps1 -PublishDirectory artifacts/release-ci-aot
    python scripts/check-release.py
    python scripts/release-guard.py archive
} finally { Pop-Location }
