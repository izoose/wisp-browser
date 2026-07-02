# Memory benchmark: Wisp vs Brave vs Chrome for the same set of tabs.
# Launches each browser on a throwaway profile, loads the URLs, and sums the
# working set (physical RAM) of every process in that browser's tree.
#
#   powershell -ExecutionPolicy Bypass -File scripts/bench.ps1
#
# Edit the exe paths below for your machine. See docs/memory-case-study.md for results.

$ErrorActionPreference = "SilentlyContinue"
$wisp   = "$PSScriptRoot\..\dist\Wisp.exe"
$brave  = "$env:LOCALAPPDATA\BraveSoftware\Brave-Browser\Application\brave.exe"
$chrome = "$env:PROGRAMFILES\Google\Chrome\Application\chrome.exe"

$urls = @(
  "https://www.youtube.com", "https://www.reddit.com",
  "https://en.wikipedia.org/wiki/Chromium_(web_browser)", "https://github.com/trending",
  "https://www.cnn.com", "https://www.amazon.com", "https://weather.com", "https://www.espn.com"
)
$LOAD = 65   # seconds to let tabs load before measuring

function Measure-Tree($marker, $extraPids) {
  $procs = Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*$marker*" }
  $ws = [int64]0; $ids = New-Object System.Collections.Generic.HashSet[int]
  foreach ($p in $procs) { $ws += [int64]$p.WorkingSetSize; [void]$ids.Add([int]$p.ProcessId) }
  foreach ($id in $extraPids) { $pr = Get-Process -Id $id; if ($pr -and $ids.Add([int]$id)) { $ws += [int64]$pr.WorkingSet64 } }
  [pscustomobject]@{ MB = [math]::Round($ws/1MB,0); Procs = $ids.Count }
}
function Kill-Tree($marker, $extraPids) {
  Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*$marker*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
  foreach ($id in $extraPids) { Stop-Process -Id $id -Force }
  Start-Sleep 3
}
function Run-Chromium($exe, $label, $results) {
  $udf = "$env:TEMP\bench_" + [guid]::NewGuid().ToString("N").Substring(0,8)
  $a = @("--user-data-dir=$udf","--no-first-run","--no-default-browser-check","--disable-sync","--disable-session-crashed-bubble") + $urls
  Start-Process $exe -ArgumentList $a | Out-Null
  Start-Sleep $LOAD
  $results[$label] = Measure-Tree $udf @()
  Kill-Tree $udf @(); Remove-Item $udf -Recurse -Force
}

$results = [ordered]@{}
$env:WISP_NO_SINGLE_INSTANCE = "1"; $env:WISP_NO_FIRSTRUN = "1"; $env:WISP_TEST_TABS = ($urls -join " ")

# Wisp — all tabs live (sleeping disabled)
$udf = "$env:TEMP\benchwispA_" + [guid]::NewGuid().ToString("N").Substring(0,8)
$env:WISP_UDF = $udf; $env:WISP_SUSPEND_SECONDS = "99999"; $env:WISP_DISCARD_SECONDS = "99999"
$p = Start-Process $wisp -PassThru; Start-Sleep $LOAD
$results["Wisp (8 tabs, all live)"] = Measure-Tree $udf @($p.Id)
Kill-Tree $udf @($p.Id); Remove-Item $udf -Recurse -Force

# Wisp — background tabs slept/discarded
$udf = "$env:TEMP\benchwispB_" + [guid]::NewGuid().ToString("N").Substring(0,8)
$env:WISP_UDF = $udf; $env:WISP_SUSPEND_SECONDS = "18"; $env:WISP_DISCARD_SECONDS = "40"; $env:WISP_SLEEP_TICK_SECONDS = "5"
$p = Start-Process $wisp -PassThru; Start-Sleep ($LOAD + 30)
$results["Wisp (8 tabs, 7 slept)"] = Measure-Tree $udf @($p.Id)
Kill-Tree $udf @($p.Id); Remove-Item $udf -Recurse -Force

Run-Chromium $brave  "Brave (8 tabs)"  $results
Run-Chromium $chrome "Chrome (8 tabs)" $results

foreach ($k in $results.Keys) { "{0,-26} {1,6} MB  {2} procs" -f $k, $results[$k].MB, $results[$k].Procs }
