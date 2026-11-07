param([switch]$Verify, [switch]$VerifySteam, [string]$ReleaseName = 'BrokenPhone', [string]$CompileDirectory = 'build\compile')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiled = if ([IO.Path]::IsPathRooted($CompileDirectory)) { $CompileDirectory } else { Join-Path $projectRoot $CompileDirectory }
& (Join-Path $projectRoot 'build.ps1') -OutputDirectory $compiled
$release = Join-Path (Join-Path $projectRoot 'dist') $ReleaseName
$tools = Join-Path $release 'tools'
New-Item -ItemType Directory -Path $release, $tools -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $compiled 'BrokenPhone.exe') -Destination $release -Force
Copy-Item -LiteralPath (Join-Path $compiled 'BrokenPhone.exe.config') -Destination $release -Force
Copy-Item -LiteralPath (Join-Path $compiled 'steam_api64.dll') -Destination $release -Force
Copy-Item -LiteralPath (Join-Path $compiled 'steam_appid.txt') -Destination $release -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'Assets') -Destination $release -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $release -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'CONNECT.md') -Destination $release -Force
$releaseDocs = Join-Path $release 'docs'
New-Item -ItemType Directory -Path $releaseDocs -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\workshop-download-research.md') -Destination $releaseDocs -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'ThirdParty') -Destination $release -Recurse -Force
foreach ($name in @('ffmpeg', 'ffprobe')) {
    $command = Get-Command ($name + '.exe') -ErrorAction SilentlyContinue
    $source = if ($command) { $command.Source } else { Join-Path 'C:\ffmpeg\bin' ($name + '.exe') }
    if (!(Test-Path -LiteralPath $source)) { throw ('Не найден ' + $name + '.exe для переносимой сборки.') }
    Copy-Item -LiteralPath $source -Destination $tools -Force
}
& (Join-Path $tools 'ffmpeg.exe') -version 2>&1 | Out-File -LiteralPath (Join-Path $release 'ThirdParty\FFmpeg-build.txt') -Encoding utf8
& (Join-Path $tools 'ffmpeg.exe') -L 2>&1 | Out-File -LiteralPath (Join-Path $release 'ThirdParty\FFmpeg-license-notice.txt') -Encoding utf8
if ($Verify) {
    foreach ($check in @('pack-commit-test','lobby-pack-test','playback-disconnect-test','self-test','state-packets-test','role-lines-test','role-mix-test','roles-network-test','roles-ui-test','design-test','display-ui-test','join-ui-test','audio-output-ui-test','ui-smoke','input-ui-test','folder-packs-ui-test','pack-downloads-ui-test','pack-sync-test','microphone-ui-test','presentation-ui-test','turn-navigation-ui-test','empty-lobby-ui-test','screening-test','playback-sync-test')) {
        $testArguments = '--' + $check
        if ($check -eq 'screening-test') { $testArguments += ' --disconnect-guest' }
        $process = Start-Process -FilePath (Join-Path $release 'BrokenPhone.exe') -ArgumentList $testArguments -PassThru -Wait -WindowStyle Hidden
        $reportPath = Join-Path $release ($check + '.txt')
        if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $reportPath)) { throw ('Переносимая сборка не прошла проверку: ' + $check) }
        $report = Get-Content -LiteralPath $reportPath -Raw
        Write-Host $report
        if (!$report.StartsWith('PASS:') -or $report.Contains('FAIL:')) { throw ('Проверка переносимой сборки не пройдена: ' + $check) }
    }
}
if ($VerifySteam) {
    foreach ($check in @(@{Argument='--steam-test'; Report='steam-test.txt'}, @{Argument='--steam-ui-test'; Report='steam-screening-test.txt'}, @{Argument='--steam-overlay-test'; Report='steam-overlay-test.txt'})) {
        $process = Start-Process -FilePath (Join-Path $release 'BrokenPhone.exe') -ArgumentList $check.Argument -PassThru -Wait -WindowStyle Hidden
        $reportPath = Join-Path $release $check.Report
        if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $reportPath)) { throw ('Steam-проверка не пройдена: ' + $check.Argument) }
        $report = Get-Content -LiteralPath $reportPath -Raw
        Write-Host $report
        if (!$report.StartsWith('PASS:') -or $report.Contains('FAIL:')) { throw ('Steam-проверка не пройдена: ' + $check.Argument) }
    }
}
$releaseRoot = [IO.Path]::GetFullPath($release).TrimEnd('\')
$distributionRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist')).TrimEnd('\')
if (!$releaseRoot.StartsWith($distributionRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Папка выпуска должна находиться внутри dist проекта.' }
$testAssets = [IO.Path]::GetFullPath((Join-Path $releaseRoot 'Assets\Demo'))
if (!$testAssets.StartsWith($releaseRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Некорректная папка тестовых ресурсов.' }
if (Test-Path -LiteralPath $testAssets) {
    if ((Get-Item -LiteralPath $testAssets).Attributes -band [IO.FileAttributes]::ReparsePoint -or (Get-ChildItem -LiteralPath $testAssets -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1)) { throw 'В тестовых ресурсах обнаружена ссылка.' }
    Remove-Item -LiteralPath $testAssets -Recurse -Force
}
$items = @('BrokenPhone.exe', 'BrokenPhone.exe.config', 'steam_api64.dll', 'steam_appid.txt', 'Assets', 'tools', 'README.md', 'CONNECT.md', 'ThirdParty', 'docs') | ForEach-Object { Join-Path $release $_ }
$archive = Join-Path $projectRoot ('dist\' + $ReleaseName + '-Windows-x64.zip')
Compress-Archive -LiteralPath $items -DestinationPath $archive -CompressionLevel Optimal -Force
Write-Host ('Игра: ' + (Join-Path $release 'BrokenPhone.exe'))
Write-Host ('Архив: ' + $archive)
