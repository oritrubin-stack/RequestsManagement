<#
.SYNOPSIS
    Measures HTTP latency of read-only API endpoints (GET requests only).

.DESCRIPTION
    Sends each scenario's GET request repeatedly to a running API and reports p50, p95, min and max latency
    (client-side, including the full response body). Warm-up requests are not counted.
    The script never sends a request that changes data.

.EXAMPLE
    # List and warm summary scenarios
    .\tools\perf\measure-api.ps1 -BaseUrl http://localhost:5130 -Iterations 30 -Warmup 5

.EXAMPLE
    # Cold summary: run the API with RequestSummaryCache__TimeToLive=00:00:01 and wait longer than the TTL between calls
    .\tools\perf\measure-api.ps1 -BaseUrl http://localhost:5130 -Scenario SummaryCold -Iterations 10 -Warmup 0 -DelayMs 1500
#>
param(
    [string] $BaseUrl = "http://localhost:5130",
    [ValidateSet("All", "List", "SummaryWarm", "SummaryCold")]
    [string] $Scenario = "All",
    [int] $Iterations = 30,
    [int] $Warmup = 5,
    [int] $DelayMs = 0
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http
$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromSeconds(60)

$listScenarios = [ordered]@{
    "A1 default page (createdAt desc)"       = "/api/requests"
    "A2 filter: status + date range"         = "/api/requests?status=Waiting&createdFrom=2025-01-01T00:00:00Z&createdTo=2025-07-01T00:00:00Z"
    "A3 text search: search=invoice"         = "/api/requests?search=invoice"
    "A4 sort by priority desc"               = "/api/requests?sortBy=priority&sortDirection=desc"
    "A5 deep page: page=2500 (skip 49,980)"  = "/api/requests?page=2500&pageSize=20"
}

function Measure-Get([string] $name, [string] $path) {
    $url = $BaseUrl.TrimEnd("/") + $path
    $times = New-Object System.Collections.Generic.List[double]

    for ($i = 0; $i -lt ($Warmup + $Iterations); $i++) {
        if ($DelayMs -gt 0) { Start-Sleep -Milliseconds $DelayMs }

        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $response = $client.GetAsync($url).GetAwaiter().GetResult()
        [void] $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $stopwatch.Stop()

        if (-not $response.IsSuccessStatusCode) {
            throw "$name returned HTTP $([int]$response.StatusCode)"
        }
        if ($i -ge $Warmup) { $times.Add($stopwatch.Elapsed.TotalMilliseconds) }
    }

    $sorted = $times | Sort-Object
    $p50 = $sorted[[int][Math]::Floor(0.50 * ($sorted.Count - 1))]
    $p95 = $sorted[[int][Math]::Ceiling(0.95 * ($sorted.Count - 1))]
    "| {0} | {1} | {2:N1} | {3:N1} | {4:N1} | {5:N1} |" -f $name, $times.Count, $p50, $p95, $sorted[0], $sorted[-1]
}

"Base URL: $BaseUrl, iterations: $Iterations, warm-up: $Warmup, delay: $DelayMs ms, run at: $((Get-Date).ToUniversalTime().ToString('o'))"
"| Scenario | Requests | p50 ms | p95 ms | min ms | max ms |"
"|---|---|---|---|---|---|"

if ($Scenario -in "All", "List") {
    foreach ($entry in $listScenarios.GetEnumerator()) { Measure-Get $entry.Key $entry.Value }
}
if ($Scenario -in "All", "SummaryWarm") {
    Measure-Get "S-warm summary (served from cache)" "/api/requests/summary"
}
if ($Scenario -eq "SummaryCold") {
    Measure-Get "S-cold summary (computed on every call)" "/api/requests/summary"
}

$client.Dispose()
