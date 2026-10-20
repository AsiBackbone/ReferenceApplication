[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReportDirectory,

    [string]$SummaryPath = $env:GITHUB_STEP_SUMMARY
)

# Summarizes the Cobertura report produced by `dotnet test --coverlet` as a Markdown table of line and branch
# coverage, overall and per assembly. The repository reports coverage without a threshold gate; the full report is
# published as a CI artifact for line-level review.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ReportDirectory -PathType Container)) {
    throw "Coverage report directory '$ReportDirectory' does not exist."
}

$reports = @(Get-ChildItem -LiteralPath $ReportDirectory -Filter 'coverage.cobertura*.xml' -File -Recurse)
if ($reports.Count -ne 1) {
    throw "Expected exactly one Cobertura report under '$ReportDirectory' but found $($reports.Count)."
}

[xml]$report = Get-Content -LiteralPath $reports[0].FullName -Raw

function Format-Rate([string]$rate, [string]$covered, [string]$valid) {
    $percent = [double]::Parse($rate, [Globalization.CultureInfo]::InvariantCulture) * 100
    return '{0:0.0}% ({1}/{2})' -f $percent, $covered, $valid
}

function Get-Counts($node, [string]$kind) {
    # Package elements carry rates only, so count covered and valid lines or branches from the class-level line
    # elements. Cobertura repeats each line under its method as well, so `.//line` would count it twice.
    $lines = @($node.SelectNodes('./classes/class/lines/line'))
    if ($kind -eq 'line') {
        return @(($lines | Where-Object { [int]$_.hits -gt 0 }).Count, $lines.Count)
    }

    $covered = 0
    $valid = 0
    foreach ($line in $lines | Where-Object { $_.branch -eq 'True' }) {
        if ($line.'condition-coverage' -match '\((\d+)/(\d+)\)') {
            $covered += [int]$Matches[1]
            $valid += [int]$Matches[2]
        }
    }

    return @($covered, $valid)
}

$coverage = $report.coverage
$summary = [Collections.Generic.List[string]]::new()
$summary.Add('## Code coverage')
$summary.Add('')
$summary.Add('| Scope | Line | Branch |')
$summary.Add('|:---|---:|---:|')
$totalLine = Format-Rate $coverage.'line-rate' $coverage.'lines-covered' $coverage.'lines-valid'
$totalBranch = Format-Rate $coverage.'branch-rate' $coverage.'branches-covered' $coverage.'branches-valid'
$summary.Add("| **Total** | $totalLine | $totalBranch |")

foreach ($package in $coverage.packages.package | Sort-Object name) {
    $lineCounts = Get-Counts $package 'line'
    $branchCounts = Get-Counts $package 'branch'
    $packageLine = Format-Rate $package.'line-rate' $lineCounts[0] $lineCounts[1]
    $packageBranch = Format-Rate $package.'branch-rate' $branchCounts[0] $branchCounts[1]
    $summary.Add("| $($package.name) | $packageLine | $packageBranch |")
}

$summary.Add('')
$summary.Add('Generated migrations and the test assembly are excluded. Download the `coverage-report` artifact for line-level detail.')

$markdown = $summary -join [Environment]::NewLine
if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    Write-Output $markdown
}
else {
    Add-Content -LiteralPath $SummaryPath -Value $markdown -Encoding utf8
}
