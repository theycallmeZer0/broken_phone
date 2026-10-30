param([switch]$Publish, [switch]$Clean)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$dist = Join-Path $root 'dist'
$build = Join-Path $root 'build'
$candidate = Join-Path $dist 'BrokenPhone-next'
$current = Join-Path $dist 'BrokenPhone'
$data = Join-Path $root 'UserData'
$migration = Join-Path $data '_migration'
function Assert-ChildPath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if (!$absolute.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw ('Путь вне проекта: ' + $absolute) }
    return $absolute
}
function Assert-PlainTree([string]$Path) {
    $absolute = Assert-ChildPath $Path
    if (!(Test-Path -LiteralPath $absolute)) { return }
    if ((Get-Item -LiteralPath $absolute -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Обнаружена ссылка: ' + $absolute) }
    if (Get-ChildItem -LiteralPath $absolute -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1) { throw ('В дереве есть ссылки: ' + $absolute) }
}
function Copy-Tree([string]$Source, [string]$Target) {
    Assert-PlainTree $Source
    $destination = Assert-ChildPath $Target
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) { Copy-Item -LiteralPath $item.FullName -Destination $destination -Recurse -Force }
}
$bases = @($build) + @(Get-ChildItem -LiteralPath $build -Directory -Force | Where-Object { $_.Name -notin @('qa','compile') } | ForEach-Object FullName) + @(Get-ChildItem -LiteralPath $dist -Directory -Force | Where-Object { $_.FullName -ne $candidate -and $_.FullName -ne $current } | ForEach-Object FullName)
if ($Publish -and (Test-Path -LiteralPath $current)) { $bases += $current }
New-Item -ItemType Directory -Path $data,$migration -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$backupPath = Assert-ChildPath (Join-Path $data 'previous-recordings-settings.zip')
$files = @()
foreach ($base in $bases | Sort-Object -Unique) {
    foreach ($relative in @('Packs','exports','.runtime\packs','.runtime\sessions','.runtime\SteamCMD\config')) {
        $folder = Join-Path $base $relative
        if (Test-Path -LiteralPath $folder) { Assert-PlainTree $folder; $files += @(Get-ChildItem -LiteralPath $folder -Recurse -File -Force) }
    }
    foreach ($relative in @('.runtime\settings.json')) { $file = Join-Path $base $relative; if (Test-Path -LiteralPath $file) { $files += Get-Item -LiteralPath $file } }
}
$files = @($files | Sort-Object FullName -Unique)
$archive = [IO.Compression.ZipFile]::Open($backupPath, [IO.Compression.ZipArchiveMode]::Update)
try {
    foreach ($file in $files) {
        $name = $file.FullName.Substring($root.Length + 1).Replace('\','/')
        $entry = $archive.GetEntry($name)
        if ($entry -and $entry.Length -eq $file.Length -and $entry.LastWriteTime.UtcDateTime -ge $file.LastWriteTimeUtc.AddSeconds(-2)) { continue }
        if ($entry) { $entry.Delete() }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$name,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
$packRoots = @()
foreach ($base in $bases | Sort-Object -Unique) { foreach ($relative in @('Packs','.runtime\packs')) { $folder = Join-Path $base $relative; if (Test-Path -LiteralPath $folder) { $packRoots += $folder } } }
foreach ($folder in $packRoots | Sort-Object -Unique) {
    foreach ($pack in Get-ChildItem -LiteralPath $folder -Directory | Sort-Object LastWriteTime -Descending) {
        if ($pack.Name -like '*-import-*') { continue }
        if (!(Test-Path -LiteralPath (Join-Path $pack.FullName 'pack.json')) -and !(Test-Path -LiteralPath (Join-Path $pack.FullName '_pack_info.ini'))) { continue }
        $target = Join-Path (Join-Path $migration 'Packs') $pack.Name
        if (!(Test-Path -LiteralPath $target)) { Copy-Tree $pack.FullName $target }
    }
}
$settings = @($bases | ForEach-Object { $path = Join-Path $_ '.runtime\settings.json'; if (Test-Path -LiteralPath $path) { Get-Item -LiteralPath $path } } | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
if ($settings.Count) { Copy-Item -LiteralPath $settings[0].FullName -Destination (Join-Path $migration 'settings.json') -Force }
$steam = @($bases | ForEach-Object { $path = Join-Path $_ '.runtime\SteamCMD'; if (Test-Path -LiteralPath (Join-Path $path 'steamcmd.exe')) { Get-Item -LiteralPath $path } } | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
if ($steam.Count -and !(Test-Path -LiteralPath (Join-Path $migration 'SteamCMD\steamcmd.exe'))) { Copy-Tree $steam[0].FullName (Join-Path $migration 'SteamCMD') }
if ($Publish) {
    if (!(Test-Path -LiteralPath (Join-Path $candidate 'BrokenPhone.exe'))) { throw 'Нет проверенной сборки BrokenPhone-next.' }
    foreach ($report in @('self-test.txt','input-ui-test.txt','folder-packs-ui-test.txt','pack-downloads-ui-test.txt','screening-test.txt','steam-screening-test.txt')) {
        $text = Get-Content -LiteralPath (Join-Path $candidate $report) -Raw
        if (!$text.StartsWith('PASS:') -or $text.Contains('FAIL:')) { throw ('Не пройдена проверка: ' + $report) }
    }
    $qa = Join-Path $build 'qa'; New-Item -ItemType Directory -Path $qa -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $candidate -File | Where-Object { $_.Extension -eq '.png' -or ($_.Extension -eq '.txt' -and $_.Name -ne 'steam_appid.txt') }) { Move-Item -LiteralPath $file.FullName -Destination $qa -Force }
    $testData = Join-Path $candidate '.runtime'
    if (Test-Path -LiteralPath $testData) { Assert-PlainTree $testData; Move-Item -LiteralPath $testData -Destination (Assert-ChildPath (Join-Path $qa ('test-runtime-' + [Guid]::NewGuid().ToString('N')))) }
    if (Test-Path -LiteralPath $current) { Assert-PlainTree $current; Remove-Item -LiteralPath (Assert-ChildPath $current) -Recurse -Force }
    Assert-PlainTree $candidate; Move-Item -LiteralPath (Assert-ChildPath $candidate) -Destination (Assert-ChildPath $current)
    Copy-Item -LiteralPath (Join-Path $dist 'BrokenPhone-next-Windows-x64.zip') -Destination (Join-Path $dist 'BrokenPhone-Windows-x64.zip') -Force
}
if ($Publish -and (Test-Path -LiteralPath (Join-Path $current 'BrokenPhone.exe'))) {
    if (Test-Path -LiteralPath (Join-Path $migration 'Packs')) { Copy-Tree (Join-Path $migration 'Packs') (Join-Path $current 'Packs') }
    New-Item -ItemType Directory -Path (Join-Path $current '.runtime') -Force | Out-Null
    if (Test-Path -LiteralPath (Join-Path $migration 'settings.json')) { Copy-Item -LiteralPath (Join-Path $migration 'settings.json') -Destination (Join-Path $current '.runtime\settings.json') -Force }
    if (Test-Path -LiteralPath (Join-Path $migration 'SteamCMD')) { Copy-Tree (Join-Path $migration 'SteamCMD') (Join-Path $current '.runtime\SteamCMD') }
}
if ($Clean) {
    $running = @(Get-CimInstance Win32_Process -Filter "Name='BrokenPhone.exe'" | ForEach-Object ExecutablePath)
    $targets = @(Get-ChildItem -LiteralPath $dist -Directory -Force | Where-Object { $_.FullName -ne $current -and $_.FullName -ne $candidate }) + @(Get-ChildItem -LiteralPath $build -Directory -Force | Where-Object { $_.Name -notin @('qa','compile') })
    foreach ($target in $targets) {
        $absolute = Assert-ChildPath $target.FullName
        if ($running | Where-Object { $_ -and $_.StartsWith($absolute + '\',[StringComparison]::OrdinalIgnoreCase) }) { Write-Host ('Оставлена запущенная копия: ' + $absolute); continue }
        Assert-PlainTree $absolute; Remove-Item -LiteralPath $absolute -Recurse -Force
    }
    foreach ($file in Get-ChildItem -LiteralPath $build -File -Force) { Remove-Item -LiteralPath (Assert-ChildPath $file.FullName) -Force }
    foreach ($file in Get-ChildItem -LiteralPath $dist -File -Filter '*.zip' | Where-Object Name -ne 'BrokenPhone-Windows-x64.zip') { Remove-Item -LiteralPath (Assert-ChildPath $file.FullName) -Force }
}
Write-Host ('Данные сохранены: ' + $backupPath)
Write-Host ('Актуальная игра: ' + (Join-Path $current 'BrokenPhone.exe'))
