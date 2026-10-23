param([string]$PublishDirectory = 'artifacts/publish/win-x64-aot')
# Exercise the real packaging script in an isolated fixture, with no personal data.
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('homechain-package-data-' + [Guid]::NewGuid().ToString('N'))
$testRepository = Join-Path $testRoot 'workspace'
function Write-Fixture([string]$relative, [string]$text) {
    $file = Join-Path $testRoot $relative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($file)) -Force | Out-Null
    [IO.File]::WriteAllText($file, $text)
}
try {
    Write-Fixture 'workspace/LICENSE' 'test license'
    Write-Fixture 'workspace/docs/usage.md' 'test'
    Write-Fixture 'workspace/docs/validation.md' 'test'
    Write-Fixture 'workspace/THIRD-PARTY-NOTICES.md' 'test'
    $publicDocuments = @('README.md','README.en.md','docs/router-asn.md','docs/releasing.md','docs/screenshots/README.md',
        'docs/screenshots/overview.png','docs/screenshots/subscriptions.png','docs/screenshots/residential.png',
        'docs/screenshots/routing.png','docs/screenshots/custom-rules.png','docs/screenshots/dns-privacy.png','docs/screenshots/output.png')
    foreach ($name in $publicDocuments) { Write-Fixture "workspace/$name" "public fixture: $name" }
    Write-Fixture 'workspace/docs/private-notes.md' 'must not ship'
    Write-Fixture 'workspace/docs/screenshots/private.png' 'must not ship'
    Write-Fixture 'workspace/docs/screenshots/router-login.json' 'must not ship'
    foreach ($name in @('av_libglesv2.dll','libSkiaSharp.dll','libHarfBuzzSharp.dll')) { Write-Fixture "workspace/artifacts/publish/win-x64-aot/$name" 'fixture' }
    Copy-Item -LiteralPath (Join-Path $source "$PublishDirectory/ClashRouteCraft.exe") -Destination (Join-Path $testRepository 'artifacts/publish/win-x64-aot/ClashRouteCraft.exe')
    New-Item -ItemType Directory -Path (Join-Path $testRepository 'scripts') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $source 'scripts/Package.ps1') -Destination (Join-Path $testRepository 'scripts/Package.ps1')
    & (Join-Path $testRepository 'scripts/Package.ps1') | Out-Null
    $names = @('ClashRouteCraft-win-x64')
    $areas = @('config','output','.cache','.project-trash','settings','resources')
    foreach ($name in $names) {
        foreach ($area in $areas) { Write-Fixture "workspace/releases/$name/$area/keep/data.txt" "$name/$area/preserved" }
    }
    & (Join-Path $testRepository 'scripts/Package.ps1') | Out-Null
    foreach ($name in $names) {
        foreach ($area in $areas) {
            if ([IO.File]::ReadAllText((Join-Path $testRepository "releases/$name/$area/keep/data.txt")) -ne "$name/$area/preserved") { throw "Data not preserved: $name/$area" }
        }
        $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $testRepository "releases/$name.zip"))
        try {
            if ($zip.Entries | Where-Object { $_.FullName -match '/(config|output|\.cache|\.project-trash|settings|resources)/|private-notes\.md|private\.png|router-login\.json' }) { throw 'Non-public data entered the ZIP' }
            foreach ($relative in $publicDocuments) {
                $entry = $zip.GetEntry("$name/$relative")
                if ($null -eq $entry) { throw "Missing packaged document: $relative" }
                $reader = [IO.StreamReader]::new($entry.Open())
                try { if ($reader.ReadToEnd() -ne "public fixture: $relative") { throw "Incorrect packaged document: $relative" } }
                finally { $reader.Dispose() }
            }
        }
        finally { $zip.Dispose() }
    }
    # Simulate a running application. A rejected update must retain every file.
    $exe = Join-Path $testRepository 'releases/ClashRouteCraft-win-x64/ClashRouteCraft.exe'
    $held = [IO.File]::Open($exe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    $blocked = $false
    try { & (Join-Path $testRepository 'scripts/Package.ps1') | Out-Null } catch { $blocked = $true } finally { $held.Dispose() }
    if (-not $blocked) { throw 'Packaging replaced a running executable' }
    foreach ($name in $names) { foreach ($area in $areas) {
        if ([IO.File]::ReadAllText((Join-Path $testRepository "releases/$name/$area/keep/data.txt")) -ne "$name/$area/preserved") { throw 'Blocked update changed data' }
    } }
    if (@(Get-ChildItem -LiteralPath (Join-Path $testRepository 'releases') -Force).Count -ne 3) { throw 'Repeated packaging accumulated outputs' }
    Write-Output 'PASS single distribution preserves data; bilingual documentation and screenshots included; unlisted docs excluded; occupied EXE blocks update'
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'homechain-package-data-*') { throw 'Unexpected test cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
