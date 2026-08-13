[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ReportPath,

    [string] $JsonOutputPath,

    [string] $MarkdownOutputPath,

    [switch] $FailOnInconclusive
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
        file         = [System.IO.Path]::GetFileName($file.Name)
        killed       = @($mutants | Where-Object status -eq 'Killed').Count
        timeout      = @($mutants | Where-Object status -eq 'Timeout').Count
        survived     = @($mutants | Where-Object status -eq 'Survived').Count
        noCoverage   = @($mutants | Where-Object status -eq 'NoCoverage').Count
        compileError = @($mutants | Where-Object status -eq 'CompileError').Count
        runtimeError = @($mutants | Where-Object status -eq 'RuntimeError').Count
        ignored      = @($mutants | Where-Object status -eq 'Ignored').Count
        total        = $mutants.Count
    }
}

function Get-Total([string] $property) {
    # Measure-Object returns $null for an empty set; [int] makes that a 0 instead of
    # poisoning the arithmetic below.
    [int](@($rows | Measure-Object -Property $property -Sum).Sum)
}

$killed = Get-Total 'killed'
$timeout = Get-Total 'timeout'
$survived = Get-Total 'survived'
$noCoverage = Get-Total 'noCoverage'
$compileError = Get-Total 'compileError'
$runtimeError = Get-Total 'runtimeError'
$ignored = Get-Total 'ignored'
$total = Get-Total 'total'

# Stryker's own metric, not an invented one:
#   detected   = Killed + Timeout   (a timeout WOULD fail CI, so it counts as detected)
#   undetected = Survived + NoCoverage
#   valid      = detected + undetected
#   score      = detected / valid * 100
# CompileError, RuntimeError, Ignored and Pending are excluded from the denominator —
# they were never validly tested. See
# https://stryker-mutator.io/docs/mutation-testing-elements/mutant-states-and-metrics/
#
# This replaces `killed / (total - ignored)`, which erred twice in the same direction —
# Timeout missing from the numerator, CompileError and RuntimeError left in the
# denominator — and therefore understated every score it ever printed.
$detected = $killed + $timeout
$undetected = $survived + $noCoverage
$valid = $detected + $undetected
$score = if ($valid -eq 0) { 0 } else { [math]::Round(($detected / $valid) * 100, 1) }
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

$approvedFinalStatuses = @('Killed', 'Timeout', 'Survived', 'NoCoverage', 'CompileError', 'Ignored')
$unapprovedStatuses = @(
    $statusTotals.GetEnumerator() |
        Where-Object { $_.Key -notin $approvedFinalStatuses -and $_.Value -gt 0 }
)
$decisive = $killed + $survived
$inconclusiveReason = if ($unapprovedStatuses.Count -gt 0) {
    "Unapproved mutant status(es): $($unapprovedStatuses.Key -join ', ')."
}
elseif ($decisive -eq 0) {
    'No mutants produced a decisive killed or survived result.'
}
elseif ($timeout -gt $decisive) {
    "Timed-out mutants ($timeout) outnumber decisive killed/survived results ($decisive)."
}
else {
    ''
}
$assurancePassed = [string]::IsNullOrEmpty($inconclusiveReason)

$summary = [ordered]@{
    killed       = $killed
    timeout      = $timeout
    survived     = $survived
    noCoverage   = $noCoverage
    compileError = $compileError
    runtimeError = $runtimeError
    ignored      = $ignored
    detected     = $detected
    undetected   = $undetected
    valid        = $valid
    total        = $total
    score        = $score
    assurancePassed = $assurancePassed
    inconclusiveReason = $inconclusiveReason
    statusTotals = $statusTotals
    hotspots     = $hotspots
}

$markdown = @(
    '### Stryker mutation summary'
    ''
    "| Metric | Value |"
    "| --- | ---: |"
    "| Killed | $killed |"
    "| Timeout | $timeout |"
    "| Survived | $survived |"
    "| No coverage | $noCoverage |"
    "| Compile error | $compileError |"
    "| Runtime error | $runtimeError |"
    "| Ignored | $ignored |"
    "| Valid mutants | $valid |"
    "| Score (detected / valid) | $score% |"
    ''
)

$extraStatuses = @($statusTotals.GetEnumerator() | Where-Object { $_.Key -notin @('Killed', 'Timeout', 'Survived', 'NoCoverage', 'CompileError', 'RuntimeError', 'Ignored') -and $_.Value -gt 0 })
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

$markdown += ''
if ($assurancePassed) {
    $markdown += 'Assurance verdict: conclusive.'
}
else {
    $markdown += "Assurance verdict: inconclusive — $inconclusiveReason"
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

if ($FailOnInconclusive -and -not $assurancePassed) {
    throw "Mutation assurance failed: $inconclusiveReason"
}
