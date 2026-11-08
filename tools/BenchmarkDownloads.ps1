param(
    [string]$BaselineDirectory = 'build\download-baseline',
    [string]$CandidateDirectory = 'build\download-candidate',
    [ValidateRange(5, 15)][int]$Pairs = 5,
    [ValidateRange(16, 256)][int]$PayloadMiB = 32
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
function Resolve-ProjectPath([string]$path) {
    if ([IO.Path]::IsPathRooted($path)) { return [IO.Path]::GetFullPath($path) }
    return [IO.Path]::GetFullPath((Join-Path $projectRoot $path))
}
$baseline = Resolve-ProjectPath $BaselineDirectory
$candidate = Resolve-ProjectPath $CandidateDirectory
if ($baseline -eq $candidate) { throw 'Для сравнения нужны две отдельные сборки.' }
$runRoot = Join-Path $projectRoot ('build\download-comparison-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$harness = Join-Path $PSScriptRoot 'DownloadBenchmark.cs'
foreach ($directory in @($baseline, $candidate)) {
    $assembly = Join-Path $directory 'BrokenPhone.exe'
    if (!(Test-Path -LiteralPath $assembly)) { throw ('Нет сборки: ' + $assembly) }
    & $compiler '/nologo' '/target:exe' '/platform:x64' '/optimize+' '/utf8output' ('/reference:' + (Join-Path $framework 'System.dll')) ('/reference:' + (Join-Path $framework 'System.Core.dll')) ('/reference:' + (Join-Path $framework 'System.Web.Extensions.dll')) ('/reference:' + $assembly) ('/out:' + (Join-Path $directory 'DownloadBenchmark.exe')) $harness
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать программу сравнения.' }
}
$payload = Join-Path $runRoot 'payload.bin'
$random = [Random]::new(1729)
$stream = [IO.File]::Create($payload)
try {
    $buffer = [byte[]]::new(65536)
    $remaining = [long]$PayloadMiB * 1048576 + 113
    while ($remaining -gt 0) {
        $random.NextBytes($buffer)
        $count = [int][Math]::Min($buffer.Length, $remaining)
        $stream.Write($buffer, 0, $count)
        $remaining -= $count
    }
} finally { $stream.Dispose() }
$samples = [Collections.Generic.List[object]]::new()
function Invoke-DownloadBenchmark([string]$label, [string]$directory, [int]$pair, [bool]$warmup) {
    $prefix = if ($warmup) { 'warmup' } else { 'pair-' + $pair }
    $resultFile = Join-Path $runRoot ($prefix + '-' + $label + '.json')
    & (Join-Path $directory 'DownloadBenchmark.exe') $payload $resultFile
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $resultFile)) { throw ('Не пройдено сравнение: ' + $prefix + '-' + $label) }
    $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
    if (!$result.passed -or $result.scenarios.Count -ne 4 -or $result.bytes -ne (Get-Item -LiteralPath $payload).Length) { throw 'Некорректный результат сравнения.' }
    foreach ($scenario in $result.scenarios) {
        if (!$scenario.verified -or !$scenario.exactProgress -or $scenario.milliseconds -le 0 -or $scenario.bytes -ne $result.bytes * $scenario.receivers) { throw 'Некорректные размеры, целостность, прогресс или время передачи.' }
        if (!$warmup) { $samples.Add([pscustomobject]@{ pair=$pair; version=$label; scenario=$scenario.name; milliseconds=[double]$scenario.milliseconds; bytes=[long]$scenario.bytes; receivers=[int]$scenario.receivers }) }
    }
    Write-Output ($prefix + ' ' + $label + ': ' + (($result.scenarios | ForEach-Object { $_.name + '=' + [Math]::Round($_.milliseconds) + 'ms' }) -join ', '))
}
Invoke-DownloadBenchmark 'baseline' $baseline 0 $true
Invoke-DownloadBenchmark 'candidate' $candidate 0 $true
for ($pair = 1; $pair -le $Pairs; $pair++) {
    if ($pair % 2 -eq 1) {
        Invoke-DownloadBenchmark 'baseline' $baseline $pair $false
        Invoke-DownloadBenchmark 'candidate' $candidate $pair $false
    } else {
        Invoke-DownloadBenchmark 'candidate' $candidate $pair $false
        Invoke-DownloadBenchmark 'baseline' $baseline $pair $false
    }
}
function Get-Median([double[]]$values) {
    $sorted = @($values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}
$summary = foreach ($name in @('http_one','http_two','swarm_one','swarm_two')) {
    $old = @($samples | Where-Object { $_.version -eq 'baseline' -and $_.scenario -eq $name } | Sort-Object pair)
    $new = @($samples | Where-Object { $_.version -eq 'candidate' -and $_.scenario -eq $name } | Sort-Object pair)
    if ($old.Count -ne $Pairs -or $new.Count -ne $Pairs) { throw ('Не хватает парных замеров: ' + $name) }
    $oldMedian = Get-Median @($old.milliseconds)
    $newMedian = Get-Median @($new.milliseconds)
    $ratios = for ($i = 0; $i -lt $Pairs; $i++) { $new[$i].milliseconds / $old[$i].milliseconds }
    [pscustomobject]@{
        scenario=$name
        baseline_median_ms=[Math]::Round($oldMedian, 3)
        candidate_median_ms=[Math]::Round($newMedian, 3)
        baseline_range_ms=@([Math]::Round(($old.milliseconds | Measure-Object -Minimum).Minimum,3),[Math]::Round(($old.milliseconds | Measure-Object -Maximum).Maximum,3))
        candidate_range_ms=@([Math]::Round(($new.milliseconds | Measure-Object -Minimum).Minimum,3),[Math]::Round(($new.milliseconds | Measure-Object -Maximum).Maximum,3))
        median_change_percent=[Math]::Round(100 * ($newMedian / $oldMedian - 1), 2)
        paired_median_change_percent=[Math]::Round(100 * ((Get-Median @($ratios)) - 1), 2)
        baseline_mib_per_second=[Math]::Round($old[0].bytes / 1048576.0 / ($oldMedian / 1000), 2)
        candidate_mib_per_second=[Math]::Round($new[0].bytes / 1048576.0 / ($newMedian / 1000), 2)
    }
}
$report = [pscustomobject]@{
    baseline_directory=$baseline
    candidate_directory=$candidate
    baseline_sha256=(Get-FileHash -LiteralPath (Join-Path $baseline 'BrokenPhone.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    candidate_sha256=(Get-FileHash -LiteralPath (Join-Path $candidate 'BrokenPhone.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    payload_bytes=(Get-Item -LiteralPath $payload).Length
    payload_sha256=(Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
    pairs=$Pairs
    conditions='Local TCP/HTTP on one Windows computer; one warmup per build; separate processes; alternating paired order; same payload; real transfer paths; SHA256 verification; no bandwidth limits or timing pass threshold. Swarm includes helper discovery and initial chunk plan. External Steam CDN throughput is not measured.'
    scenarios=@($summary)
    samples=@($samples.ToArray())
}
$json = $report | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $runRoot 'summary.json'), $json, [Text.UTF8Encoding]::new($false))
Write-Output ($report | Select-Object payload_bytes,pairs,conditions,scenarios | ConvertTo-Json -Depth 6)
Write-Output ('Результаты: ' + (Join-Path $runRoot 'summary.json'))
