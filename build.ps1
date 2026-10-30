param([switch]$Run, [switch]$Test, [string]$OutputDirectory = 'build\compile')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
& (Join-Path $projectRoot 'tools\BuildBrand.ps1')
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Нужен .NET Framework 4.8 для Windows.' }
$output = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$references = @('System.dll','System.Core.dll','System.Xaml.dll','System.Web.Extensions.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Windows.Forms.dll','System.Drawing.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$references += @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path $framework ('WPF\' + $_)) }
$sources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',('/win32icon:' + (Join-Path $projectRoot 'Assets\Brand\BrokenPhone.ico')),('/out:' + (Join-Path $output 'BrokenPhone.exe')),('/resource:' + (Join-Path $projectRoot 'UI\MainWindow.xaml') + ',MainWindow.xaml')) + $references + $sources
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'Assets') -Destination $output -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'App.config') -Destination (Join-Path $output 'BrokenPhone.exe.config') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'Dependencies\Steam\steam_api64.dll') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'steam_appid.txt') -Destination $output -Force
Write-Host ('Готово: ' + (Join-Path $output 'BrokenPhone.exe'))
if ($Test) {
    $process = Start-Process -FilePath (Join-Path $output 'BrokenPhone.exe') -ArgumentList '--self-test' -PassThru -Wait -WindowStyle Hidden
    Get-Content -LiteralPath (Join-Path $output 'self-test.txt')
    if ($process.ExitCode -ne 0) { throw 'Проверки не пройдены.' }
}
if ($Run) { Start-Process -FilePath (Join-Path $output 'BrokenPhone.exe') }
