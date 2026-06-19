[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ReportPath,

    [string] $JsonOutputPath,

    [string] $MarkdownOutputPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ReportPath)) {
    throw "Stryker report not found: $ReportPath"
}

$report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
$files = @($report.files.PSObject.Properties)
$allStatuses = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

$rows = foreach ($file in $files) {
    $mutants = @($file.Value.mutants)
    foreach ($mutant in $mutants) {
        [void]$allStatuses.Add([string]$mutant.status)
    }
    [pscustomobject]@{
        file     = [System.IO.Path]::GetFileName($file.Name)
        killed   = @($mutants | Where-Object status -eq 'Killed').Count
        survived = @($mutants | Where-Object status -eq 'Survived').Count
        ignored  = @($mutants | Where-Object status -eq 'Ignored').Count
        total    = $mutants.Count
    }
}

$killed = @($rows | Measure-Object -Property killed -Sum).Sum
$survived = @($rows | Measure-Object -Property survived -Sum).Sum
$ignored = @($rows | Measure-Object -Property ignored -Sum).Sum
$total = @($rows | Measure-Object -Property total -Sum).Sum
$tested = $total - $ignored
$score = if ($tested -eq 0) { 0 } else { [math]::Round(($killed / $tested) * 100, 1) }
$hotspots = @(
    $rows |
        Where-Object survived -gt 0 |
        Sort-Object -Property @{ Expression = 'survived'; Descending = $true }, file |
        Select-Object -First 5
)
$statusTotals = [ordered]@{}
foreach ($status in ($allStatuses | Sort-Object)) {
    $statusTotals[$status] = @(
        $files |
            ForEach-Object { @($_.Value.mutants | Where-Object status -eq $status).Count } |
            Measure-Object -Sum
    ).Sum
}

$summary = [ordered]@{
    killed       = $killed
    survived     = $survived
    ignored      = $ignored
    tested       = $tested
    score        = $score
    statusTotals = $statusTotals
    hotspots     = $hotspots
}

$markdown = @(
    '### Stryker mutation summary'
    ''
    "| Metric | Value |"
    "| --- | ---: |"
    "| Killed | $killed |"
    "| Survived | $survived |"
    "| Ignored | $ignored |"
    "| Tested mutants | $tested |"
    "| Score (killed/tested) | $score% |"
    ''
)

$extraStatuses = @($statusTotals.GetEnumerator() | Where-Object { $_.Key -notin @('Killed', 'Survived', 'Ignored') -and $_.Value -gt 0 })
if ($extraStatuses.Count -gt 0) {
    $markdown += "| Additional status | Count |"
    $markdown += "| --- | ---: |"
    foreach ($entry in $extraStatuses) {
        $markdown += "| $($entry.Key) | $($entry.Value) |"
    }
    $markdown += ''
}

if ($hotspots.Count -gt 0) {
    $markdown += "| File | Killed | Survived | Ignored |"
    $markdown += "| --- | ---: | ---: | ---: |"
    foreach ($hotspot in $hotspots) {
        $markdown += "| $($hotspot.file) | $($hotspot.killed) | $($hotspot.survived) | $($hotspot.ignored) |"
    }
}
else {
    $markdown += "No surviving mutants."
}

$markdownText = ($markdown -join [Environment]::NewLine) + [Environment]::NewLine

if ($JsonOutputPath) {
    $directory = Split-Path -Parent $JsonOutputPath
    if ($directory) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $JsonOutputPath
}

if ($MarkdownOutputPath) {
    $directory = Split-Path -Parent $MarkdownOutputPath
    if ($directory) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    $markdownText | Set-Content -LiteralPath $MarkdownOutputPath
}

$markdownText
