param([string]$Version = '', [string]$PublishDirectory = 'artifacts/publish/win-x64-aot')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskPublish = [IO.Path]::GetFullPath((Join-Path $taskRoot $PublishDirectory))
$taskExe = Join-Path $taskPublish 'ClashRouteCraft.exe'
if (-not (Test-Path -LiteralPath $taskExe -PathType Leaf)) { throw 'Publish the desktop app before packaging.' }
$taskBinaryVersion = (Get-Item -LiteralPath $taskExe).VersionInfo.ProductVersion.Split('+')[0]
if (-not $Version) { $Version = $taskBinaryVersion }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$' -or $Version -ne $taskBinaryVersion) { throw 'Release version must match the published executable.' }
$taskReleases = Join-Path $taskRoot 'releases'
New-Item -ItemType Directory -Path $taskReleases -Force | Out-Null
if ((Get-Item -LiteralPath $taskReleases -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The releases directory must not be a symbolic link or junction.' }

function Assert-ReleasePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($taskReleases + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Path is outside releases: $full" }
    $parent = $full
    while ($parent -ne $taskReleases) {
        if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing linked path: $parent" }
        $parent = [IO.Path]::GetDirectoryName($parent)
    }
    return $full
}
function Remove-ReleaseItem([string]$Path) {
    $full = Assert-ReleasePath $Path
    if (-not (Test-Path -LiteralPath $full)) { return }
    if (Test-Path -LiteralPath $full -PathType Container) {
        if (Get-ChildItem -LiteralPath $full -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1) { throw "Refusing a directory containing links: $full" }
        Remove-Item -LiteralPath $full -Recurse -Force
    } else { Remove-Item -LiteralPath $full -Force }
}
function Move-ReleaseItem([string]$From, [string]$To) {
    $source = Assert-ReleasePath $From
    $destination = Assert-ReleasePath $To
    Move-Item -LiteralPath $source -Destination $destination
}
function Assert-AppClosed([string]$Directory) {
    $exe = Join-Path $Directory 'ClashRouteCraft.exe'
    if (Test-Path -LiteralPath $exe -PathType Leaf) {
        try { $handle = [IO.File]::Open($exe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None); $handle.Dispose() }
        catch { throw "请先关闭此目录中的 ClashRouteCraft，再重新打包：$Directory" }
    }
}
$taskDataDirectories = @('config', 'output', '.cache', '.project-trash', 'settings', 'resources')
function Assert-GeneratedDirectory([string]$Directory, [bool]$AllowData = $false) {
    $root = Assert-ReleasePath $Directory
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Not a release directory: $root" }
    $sums = Join-Path $root 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $sums -PathType Leaf)) { throw "缺少发布清单，保留目录：$root" }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void]$expected.Add($sums)
    foreach ($line in [IO.File]::ReadAllLines($sums)) {
        if ($line -notmatch '^([a-fA-F0-9]{64})  (.+)$') { throw "Invalid checksum entry in $sums" }
        $hash = $Matches[1]; $relative = $Matches[2]
        if ([IO.Path]::IsPathRooted($relative)) { throw "Invalid release entry: $relative" }
        $file = [IO.Path]::GetFullPath((Join-Path $root $relative))
        if (-not $file.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid release entry: $relative" }
        [void](Assert-ReleasePath $file)
        # Previously bundled resources are now mutable, optional downloads; preserve them as local data.
        if ($AllowData -and $relative.Replace('\','/').StartsWith('resources/', [StringComparison]::OrdinalIgnoreCase)) { continue }
        if (-not $expected.Add($file) -or -not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $hash) { throw "发布文件已被修改或缺失，保留目录：$root" }
    }
    if (-not $expected.Contains((Join-Path $root 'ClashRouteCraft.exe'))) { throw "Not a ClashRouteCraft release: $root" }
    $payload = @(Get-ChildItem -LiteralPath $root -Force | Where-Object { -not ($AllowData -and $_.PSIsContainer -and $_.Name -in $taskDataDirectories) })
    foreach ($data in Get-ChildItem -LiteralPath $root -Force | Where-Object { $AllowData -and $_.PSIsContainer -and $_.Name -in $taskDataDirectories }) { [void](Assert-ReleasePath $data.FullName) }
    $allPayload = @($payload) + @($payload | Where-Object PSIsContainer | Get-ChildItem -Recurse -Force)
    foreach ($item in $allPayload) {
        [void](Assert-ReleasePath $item.FullName)
        if ($item.PSIsContainer) {
            $prefix = $item.FullName + [IO.Path]::DirectorySeparatorChar
            if (-not @($expected | Where-Object { $_.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) }).Count) { throw "发布目录包含额外目录，请先移出个人数据：$root" }
        } elseif (-not $expected.Contains($item.FullName)) { throw "发布目录包含额外文件，请先移出个人数据：$root" }
    }
}

# Stable destinations: a release version is metadata, never a new output folder.
# Packaging is explicit; only one Windows x64 distribution is produced.
$taskDefinitions = @(@{Name='ClashRouteCraft-win-x64';Full=$false})
# Closing the packaging lock handle also removes this transient lock file.
$taskLockPath = Join-Path $taskReleases '.package.lock'
[void](Assert-ReleasePath $taskLockPath)
try { $taskLock = [IO.FileStream]::new($taskLockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 4096, [IO.FileOptions]::DeleteOnClose) }
catch { throw '另一个打包任务正在运行，请等待完成后重试。' }
$taskStage = Join-Path $taskReleases ('.pack-' + [Guid]::NewGuid().ToString('N'))
$taskBackups = [Collections.Generic.List[object]]::new()
$taskPublished = [Collections.Generic.List[string]]::new()
$taskDataMoves = [Collections.Generic.List[object]]::new()
$taskKeepStage = $false
try {
    foreach ($definition in $taskDefinitions) {
        $destination = Join-Path $taskReleases $definition.Name
        [void](Assert-ReleasePath $destination); [void](Assert-ReleasePath "$destination.zip")
        if (Test-Path -LiteralPath $destination) { Assert-AppClosed $destination; Assert-GeneratedDirectory $destination $true }
    }
    [void](Assert-ReleasePath $taskStage)
    New-Item -ItemType Directory -Path $taskStage -Force | Out-Null
    $taskManifest = @()
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($definition in $taskDefinitions) {
        $staged = Join-Path $taskStage $definition.Name
        $stagedZip = "$staged.zip"
        New-Item -ItemType Directory -Path $staged | Out-Null
        foreach ($name in @('ClashRouteCraft.exe','av_libglesv2.dll','libSkiaSharp.dll','libHarfBuzzSharp.dll')) {
            Copy-Item -LiteralPath (Join-Path $taskPublish $name) -Destination (Join-Path $staged $name)
        }
        # Explicit public-file inventory: never recursively copy a workspace or docs folder.
        $publicDocuments = @(
            'LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md', 'README.en.md',
            'docs/usage.md', 'docs/validation.md', 'docs/router-asn.md', 'docs/screenshots/README.md',
            'docs/screenshots/overview.png', 'docs/screenshots/subscriptions.png',
            'docs/screenshots/residential.png', 'docs/screenshots/routing.png',
            'docs/screenshots/custom-rules.png', 'docs/screenshots/dns-privacy.png', 'docs/screenshots/output.png'
        )
        foreach ($name in $publicDocuments) {
            $documentTarget = Join-Path $staged $name
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($documentTarget)) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $taskRoot $name) -Destination $documentTarget
        }
        $licenses = Join-Path $taskRoot 'artifacts/licenses'
        if (Test-Path -LiteralPath $licenses) { Copy-Item -LiteralPath $licenses -Destination (Join-Path $staged 'licenses') -Recurse }
        $files = @(Get-ChildItem -LiteralPath $staged -Recurse -File)
        $checksums = $files | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+$_.FullName.Substring($staged.Length+1).Replace('\','/') }
        [IO.File]::WriteAllLines((Join-Path $staged 'SHA256SUMS.txt'),$checksums,[Text.UTF8Encoding]::new($false))
        [IO.Compression.ZipFile]::CreateFromDirectory($staged,$stagedZip,[IO.Compression.CompressionLevel]::Optimal,$true)
        $destination = Join-Path $taskReleases $definition.Name
        $taskManifest += [pscustomobject]@{name=$definition.Name;version=$Version;full=$definition.Full;directory=$destination;zip="$destination.zip";published_bytes=((Get-ChildItem -LiteralPath $staged -Recurse -File) | Measure-Object Length -Sum).Sum;zip_bytes=(Get-Item -LiteralPath $stagedZip).Length;sha256=(Get-FileHash -LiteralPath $stagedZip -Algorithm SHA256).Hash.ToLowerInvariant();files=$files.Count+1}
    }
    ConvertTo-Json -InputObject @($taskManifest) -Depth 5 | Set-Content -LiteralPath (Join-Path $taskStage 'release-manifest.json') -Encoding UTF8
    $names = @($taskDefinitions | ForEach-Object { $_.Name; $_.Name + '.zip' }) + @('release-manifest.json')
    $backup = Join-Path $taskStage '.previous'
    New-Item -ItemType Directory -Path $backup | Out-Null
    try {
        foreach ($name in $names) {
            $target = Join-Path $taskReleases $name
            if (Test-Path -LiteralPath $target) {
                $saved = Join-Path $backup $name
                Move-ReleaseItem $target $saved
                $taskBackups.Add(@{Original=$target;Saved=$saved})
            }
            Move-ReleaseItem (Join-Path $taskStage $name) $target
            $taskPublished.Add($target)
        }
        # Transfer local data after publication. It never enters the distributable ZIP or checksums.
        foreach ($definition in $taskDefinitions) {
            foreach ($area in $taskDataDirectories) {
                $from = Join-Path (Join-Path $backup $definition.Name) $area
                $to = Join-Path (Join-Path $taskReleases $definition.Name) $area
                if (Test-Path -LiteralPath $from) {
                    Move-ReleaseItem $from $to
                    $taskDataMoves.Add(@{From=$from;To=$to})
                }
            }
        }
    } catch {
        $publicationError = $_
        try {
            for ($index=$taskDataMoves.Count-1; $index -ge 0; $index--) { Move-ReleaseItem $taskDataMoves[$index].To $taskDataMoves[$index].From }
            for ($index=$taskPublished.Count-1; $index -ge 0; $index--) { Remove-ReleaseItem $taskPublished[$index] }
            for ($index=$taskBackups.Count-1; $index -ge 0; $index--) { Move-ReleaseItem $taskBackups[$index].Saved $taskBackups[$index].Original }
        } catch { $taskKeepStage=$true; throw "发布回滚未完成，保留恢复文件：$taskStage。$($_.Exception.Message)" }
        throw $publicationError
    }
    # Old versioned directories are removed only when still matching our inventory.
    foreach ($old in Get-ChildItem -LiteralPath $taskReleases -Directory | Where-Object { $_.Name -match '^ClashRouteCraft-\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?-win-x64(?:-full)?$' }) {
        try {
            Assert-AppClosed $old.FullName; Assert-GeneratedDirectory $old.FullName
            Remove-ReleaseItem $old.FullName
            Remove-ReleaseItem ($old.FullName + '.zip')
        } catch { Write-Warning "旧产物暂未清理：$($old.Name)。$($_.Exception.Message)" }
    }
    $taskManifest | Select-Object name,version,@{Name='DirectoryMB';Expression={[math]::Round($_.published_bytes/1000000,2)}},@{Name='ZipMB';Expression={[math]::Round($_.zip_bytes/1000000,2)}},files
} finally {
    try { if (-not $taskKeepStage -and (Test-Path -LiteralPath $taskStage)) { Remove-ReleaseItem $taskStage } }
    finally { $taskLock.Dispose() }
}
