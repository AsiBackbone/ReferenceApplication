[CmdletBinding()]
param(
    [string]$ReportDirectory,

    [string]$FloorsPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'eng/code-coverage-floors.json'),

    [string]$SummaryPath = $env:GITHUB_STEP_SUMMARY,

    [switch]$SelfTest
)

# Summarizes the Cobertura report produced by `dotnet test --coverlet` as a Markdown table of line and branch
# coverage, overall and per assembly, and gates it against the repository-owned floors in
# eng/code-coverage-floors.json. The summary is written before the gate is evaluated, so a failing run still shows
# every measured value next to its floor. The script fails when total or any production assembly's line or branch
# coverage falls below its floor, when a floored assembly is missing from the report, and when the report contains
# an assembly that has no floor. The full report is published as a CI artifact for line-level review.
#
# Each floor is the recorded baseline minus an explicit tolerance. Coverage is computed from the covered and valid
# counts rather than from Cobertura's rounded rate attributes, and a value exactly at its floor passes.
#
# Run with -SelfTest to exercise parsing, report discovery, threshold boundaries, and failure messages against
# synthetic reports.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$invariant = [Globalization.CultureInfo]::InvariantCulture

function ConvertTo-Decimal([object]$Value, [string]$Name) {
    if ($null -eq $Value -or $Value -isnot [ValueType]) {
        throw "Coverage floors: '$Name' must be a number."
    }

    return [decimal]::Parse(([double]$Value).ToString('R', $invariant), $invariant)
}

function Format-Percent([decimal]$Value) {
    return [string]::Format($invariant, '{0:0.00}%', $Value)
}

function Read-CoverageFloors([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Coverage floors file '$Path' does not exist."
    }

    $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    foreach ($property in @('tolerance', 'total', 'assemblies')) {
        if ($null -eq $document.PSObject.Properties[$property]) {
            throw "Coverage floors: '$property' is required."
        }
    }

    $tolerances = @{}
    foreach ($scope in @('total', 'assembly')) {
        if ($null -eq $document.tolerance.PSObject.Properties[$scope]) {
            throw "Coverage floors: 'tolerance.$scope' is required."
        }

        $tolerance = ConvertTo-Decimal $document.tolerance.$scope "tolerance.$scope"
        if ($tolerance -lt 0 -or $tolerance -gt 2) {
            throw "Coverage floors: 'tolerance.$scope' must be between 0 and 2 percentage points."
        }

        $tolerances[$scope] = $tolerance
    }

    function New-Floor($node, [string]$name, [decimal]$tolerance) {
        $floor = [ordered]@{ Name = $name }
        foreach ($kind in @('line', 'branch')) {
            if ($null -eq $node.PSObject.Properties[$kind]) {
                throw "Coverage floors: '$name.$kind' is required."
            }

            $baseline = ConvertTo-Decimal $node.$kind "$name.$kind"
            if ($baseline -lt 0 -or $baseline -gt 100) {
                throw "Coverage floors: '$name.$kind' must be between 0 and 100."
            }

            $floor["${kind}Baseline"] = $baseline
            $floor["${kind}Floor"] = [Math]::Max([decimal]0, $baseline - $tolerance)
        }

        $floor['Tolerance'] = $tolerance
        return [pscustomobject]$floor
    }

    $assemblyFloors = [ordered]@{}
    foreach ($assembly in $document.assemblies.PSObject.Properties) {
        $assemblyFloors[$assembly.Name] = New-Floor $assembly.Value $assembly.Name $tolerances['assembly']
    }

    if ($assemblyFloors.Count -eq 0) {
        throw "Coverage floors: 'assemblies' must list at least one production assembly."
    }

    return [pscustomobject]@{
        Total      = New-Floor $document.total 'total' $tolerances['total']
        Assemblies = $assemblyFloors
    }
}

function Get-Counts($node, [string]$kind) {
    # Package elements carry rates only, so count covered and valid lines or branches from the class-level line
    # elements. Cobertura repeats each line under its method as well, so `.//line` would count it twice.
    $lines = @($node.SelectNodes('./classes/class/lines/line'))
    if ($kind -eq 'line') {
        return @(@($lines | Where-Object { [int]$_.hits -gt 0 }).Count, $lines.Count)
    }

    $covered = 0
    $valid = 0
    foreach ($line in $lines | Where-Object { $_.GetAttribute('branch') -eq 'True' }) {
        if ($line.GetAttribute('condition-coverage') -match '\((\d+)/(\d+)\)') {
            $covered += [int]$Matches[1]
            $valid += [int]$Matches[2]
        }
    }

    return @($covered, $valid)
}

function New-Measurement([int]$Covered, [int]$Valid) {
    # A scope with nothing to cover (for example, no branches) is fully covered.
    $percent = if ($Valid -eq 0) { [decimal]100 } else { [decimal]$Covered * 100 / [decimal]$Valid }
    return [pscustomobject]@{ Covered = $Covered; Valid = $Valid; Percent = $percent }
}

function Test-CoverageScope([string]$Label, $Line, $Branch, $Floor, [Collections.Generic.List[string]]$Failures) {
    $passed = $true
    foreach ($kind in @('line', 'branch')) {
        $measurement = if ($kind -eq 'line') { $Line } else { $Branch }
        $floorValue = $Floor."${kind}Floor"
        if ($measurement.Percent -lt $floorValue) {
            $passed = $false
            $Failures.Add(
                ('{0} {1} coverage {2} ({3}/{4}) is below its floor of {5} (baseline {6} minus {7} percentage points).' -f
                    $Label,
                    $kind,
                    (Format-Percent $measurement.Percent),
                    $measurement.Covered,
                    $measurement.Valid,
                    (Format-Percent $floorValue),
                    (Format-Percent $Floor."${kind}Baseline"),
                    [string]::Format($invariant, '{0:0.0#}', $Floor.Tolerance)))
        }
    }

    return $passed
}

function Format-Cell($Measurement, [decimal]$Floor) {
    return '{0} ({1}/{2}) / {3}' -f
        (Format-Percent $Measurement.Percent), $Measurement.Covered, $Measurement.Valid, (Format-Percent $Floor)
}

function Invoke-CoverageGate([string]$Directory, [string]$Floors) {
    if ([string]::IsNullOrWhiteSpace($Directory) -or -not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "Coverage report directory '$Directory' does not exist."
    }

    $floorSet = Read-CoverageFloors $Floors

    $reports = @(Get-ChildItem -LiteralPath $Directory -Filter 'coverage.cobertura*.xml' -File -Recurse)
    if ($reports.Count -ne 1) {
        throw "Expected exactly one Cobertura report under '$Directory' but found $($reports.Count)."
    }

    [xml]$report = Get-Content -LiteralPath $reports[0].FullName -Raw
    $coverage = $report.SelectSingleNode('/coverage')
    if ($null -eq $coverage) {
        throw "'$($reports[0].FullName)' is not a Cobertura report: it has no <coverage> root element."
    }

    $failures = [Collections.Generic.List[string]]::new()
    $rows = [Collections.Generic.List[string]]::new()

    $totalLine = New-Measurement ([int]$coverage.GetAttribute('lines-covered')) ([int]$coverage.GetAttribute('lines-valid'))
    $totalBranch = New-Measurement ([int]$coverage.GetAttribute('branches-covered')) ([int]$coverage.GetAttribute('branches-valid'))
    if ($totalLine.Valid -eq 0) {
        throw "'$($reports[0].FullName)' reports no coverable lines."
    }

    $totalPassed = Test-CoverageScope 'Total' $totalLine $totalBranch $floorSet.Total $failures
    $rows.Add(('| **Total** | {0} | {1} | {2} |' -f
            (Format-Cell $totalLine $floorSet.Total.lineFloor),
            (Format-Cell $totalBranch $floorSet.Total.branchFloor),
            $(if ($totalPassed) { 'Pass' } else { '**Fail**' })))

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($package in @($coverage.SelectNodes('./packages/package')) | Sort-Object { $_.GetAttribute('name') }) {
        $name = $package.GetAttribute('name')
        if (-not $seen.Add($name)) {
            throw "Assembly '$name' appears more than once in the Cobertura report."
        }

        $lineCounts = Get-Counts $package 'line'
        $branchCounts = Get-Counts $package 'branch'
        $line = New-Measurement $lineCounts[0] $lineCounts[1]
        $branch = New-Measurement $branchCounts[0] $branchCounts[1]

        if (-not $floorSet.Assemblies.Contains($name)) {
            $failures.Add(
                "$name is in the coverage report but has no floor. Add it to eng/code-coverage-floors.json, or exclude it from coverage if it is not a production assembly.")
            $rows.Add(('| {0} | {1} ({2}/{3}) / none | {4} ({5}/{6}) / none | **Fail** |' -f
                    $name,
                    (Format-Percent $line.Percent), $line.Covered, $line.Valid,
                    (Format-Percent $branch.Percent), $branch.Covered, $branch.Valid))
            continue
        }

        $floor = $floorSet.Assemblies[$name]
        $passed = Test-CoverageScope $name $line $branch $floor $failures
        $rows.Add(('| {0} | {1} | {2} | {3} |' -f
                $name,
                (Format-Cell $line $floor.lineFloor),
                (Format-Cell $branch $floor.branchFloor),
                $(if ($passed) { 'Pass' } else { '**Fail**' })))
    }

    foreach ($name in $floorSet.Assemblies.Keys) {
        if (-not $seen.Contains($name)) {
            $failures.Add("$name has a coverage floor but is missing from the coverage report.")
            $rows.Add("| $name | missing | missing | **Fail** |")
        }
    }

    $summary = [Collections.Generic.List[string]]::new()
    $summary.Add('## Code coverage')
    $summary.Add('')
    $summary.Add('| Scope | Line / floor | Branch / floor | Gate |')
    $summary.Add('|:---|---:|---:|:---|')
    $summary.AddRange($rows)
    $summary.Add('')
    $summary.Add('Generated migrations and the test assembly are excluded. Floors come from `eng/code-coverage-floors.json`. Download the `coverage-report` artifact for line-level detail.')

    if ($failures.Count -gt 0) {
        $summary.Add('')
        $summary.Add('**Coverage gate failed:**')
        $summary.Add('')
        foreach ($failure in $failures) {
            $summary.Add("- $failure")
        }
    }

    return [pscustomobject]@{
        Markdown = $summary -join [Environment]::NewLine
        Failures = @($failures)
    }
}

function Write-CoverageGateSummary($Result, [string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        Write-Output $Result.Markdown
    }
    else {
        Add-Content -LiteralPath $Path -Value $Result.Markdown -Encoding utf8
    }
}

function Assert-CoverageGatePassed($Result) {
    if ($Result.Failures.Count -gt 0) {
        $message = @('Coverage gate failed:') + @($Result.Failures | ForEach-Object { "  - $_" }) + @(
            'Add tests for the uncovered code. If a lower value is intended, lower the baseline in eng/code-coverage-floors.json and explain why in the pull request.')
        throw ($message -join [Environment]::NewLine)
    }
}

function Invoke-SelfTest {
    $root = Join-Path ([IO.Path]::GetTempPath()) ("coverage-gate-selftest-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root | Out-Null
    $script:assertions = 0

    function Assert-True([bool]$Condition, [string]$Message) {
        $script:assertions++
        if (-not $Condition) {
            throw "Self-test failed: $Message"
        }
    }

    function Assert-Throws([scriptblock]$Action, [string]$Expected, [string]$Message) {
        $script:assertions++
        try {
            & $Action | Out-Null
        }
        catch {
            if ($_.Exception.Message -notlike "*$Expected*") {
                throw "Self-test failed: $Message. Expected an error containing '$Expected' but got '$($_.Exception.Message)'."
            }

            return
        }

        throw "Self-test failed: $Message. Expected an error containing '$Expected'."
    }

    function New-Case([string]$Name) {
        $path = Join-Path $root $Name
        New-Item -ItemType Directory -Path $path | Out-Null
        return $path
    }

    function New-Floors([string]$Directory, [hashtable]$Assemblies, [double]$TotalLine = 90, [double]$TotalBranch = 75) {
        $document = [ordered]@{
            description = 'Self-test floors.'
            tolerance   = [ordered]@{ total = 0.5; assembly = 1.0 }
            total       = [ordered]@{ line = $TotalLine; branch = $TotalBranch }
            assemblies  = [ordered]@{}
        }

        foreach ($name in $Assemblies.Keys | Sort-Object) {
            $document.assemblies[$name] = [ordered]@{ line = $Assemblies[$name][0]; branch = $Assemblies[$name][1] }
        }

        $path = Join-Path $Directory 'floors.json'
        $document | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding utf8
        return $path
    }

    # Packages are @{ Name = ..; LinesCovered = ..; LinesValid = ..; BranchesCovered = ..; BranchesValid = .. }.
    # Every class line is also repeated under a method, as coverlet does, to prove lines are not counted twice.
    function New-Report([string]$Directory, [object[]]$Packages, [string]$FileName = 'coverage.cobertura.xml') {
        $builder = [Text.StringBuilder]::new()
        $linesCovered = 0; $linesValid = 0; $branchesCovered = 0; $branchesValid = 0
        $packageXml = [Text.StringBuilder]::new()
        foreach ($package in $Packages) {
            $linesCovered += $package.LinesCovered; $linesValid += $package.LinesValid
            $branchesCovered += $package.BranchesCovered; $branchesValid += $package.BranchesValid

            $lines = [Text.StringBuilder]::new()
            for ($i = 1; $i -le $package.LinesValid; $i++) {
                $hits = if ($i -le $package.LinesCovered) { 1 } else { 0 }
                if ($i -eq 1 -and $package.BranchesValid -gt 0) {
                    $percent = [int](100 * $package.BranchesCovered / $package.BranchesValid)
                    [void]$lines.Append("<line number=`"$i`" hits=`"$hits`" branch=`"True`" condition-coverage=`"$percent% ($($package.BranchesCovered)/$($package.BranchesValid))`" />")
                }
                else {
                    [void]$lines.Append("<line number=`"$i`" hits=`"$hits`" branch=`"False`" />")
                }
            }

            [void]$packageXml.Append("<package name=`"$($package.Name)`" line-rate=`"0`" branch-rate=`"0`" complexity=`"1`"><classes><class name=`"$($package.Name).Sample`" filename=`"Sample.cs`" line-rate=`"0`" branch-rate=`"0`" complexity=`"1`"><methods><method name=`"Run`" signature=`"()`" line-rate=`"0`" branch-rate=`"0`" complexity=`"1`"><lines>$lines</lines></method></methods><lines>$lines</lines></class></classes></package>")
        }

        [void]$builder.Append("<?xml version=`"1.0`" encoding=`"utf-8`"?><coverage line-rate=`"0`" branch-rate=`"0`" version=`"1.9`" timestamp=`"0`" lines-covered=`"$linesCovered`" lines-valid=`"$linesValid`" branches-covered=`"$branchesCovered`" branches-valid=`"$branchesValid`"><sources /><packages>$packageXml</packages></coverage>")
        $path = Join-Path $Directory $FileName
        Set-Content -LiteralPath $path -Value $builder.ToString() -Encoding utf8
        return $path
    }

    function New-Package([string]$Name, [int]$LinesCovered, [int]$LinesValid, [int]$BranchesCovered, [int]$BranchesValid) {
        return @{
            Name = $Name; LinesCovered = $LinesCovered; LinesValid = $LinesValid
            BranchesCovered = $BranchesCovered; BranchesValid = $BranchesValid
        }
    }

    $assemblies = @{ 'App.Web' = @(90, 75); 'App.Infrastructure' = @(90, 75) }

    try {
        # Pass: every scope above its floor; counts come from class lines only, not the repeated method lines.
        $case = New-Case 'pass'
        $floors = New-Floors $case $assemblies
        New-Report $case @(
            (New-Package 'App.Web' 95 100 8 10),
            (New-Package 'App.Infrastructure' 190 200 16 20)) | Out-Null
        $result = Invoke-CoverageGate $case $floors
        Assert-True ($result.Failures.Count -eq 0) "a report above every floor passes: $($result.Failures -join '; ')"
        Assert-True ($result.Markdown -match [regex]::Escape('| **Total** | 95.00% (285/300) / 89.50% | 80.00% (24/30) / 74.50% | Pass |')) 'the total row shows measured counts and the floor'
        Assert-True ($result.Markdown -match [regex]::Escape('| App.Web | 95.00% (95/100) / 89.00% | 80.00% (8/10) / 74.00% | Pass |')) 'per-assembly counts are not doubled by method-level lines'
        Assert-True ($result.Markdown -match [regex]::Escape('| App.Infrastructure |')) 'both assemblies are visible in the summary'
        Assert-True ($result.Markdown.IndexOf('App.Infrastructure') -lt $result.Markdown.IndexOf('App.Web')) 'assemblies are listed in name order'
        Assert-True ($result.Markdown -notmatch 'Coverage gate failed') 'a passing summary has no failure section'
        Assert-CoverageGatePassed $result

        # Exact boundary: values equal to the floor pass, one covered line fewer fails.
        $case = New-Case 'boundary'
        $floors = New-Floors $case @{ 'App.Web' = @(90.5, 75.5) } 90.5 75.5
        New-Report $case @((New-Package 'App.Web' 900 1000 150 200)) | Out-Null
        $result = Invoke-CoverageGate $case $floors
        Assert-True ($result.Failures.Count -eq 0) "values exactly at the floor pass: $($result.Failures -join '; ')"

        $case = New-Case 'just-below'
        $floors = New-Floors $case @{ 'App.Web' = @(80, 70) } 90.5 70
        New-Report $case @((New-Package 'App.Web' 899 1000 150 200)) | Out-Null
        $result = Invoke-CoverageGate $case $floors
        Assert-True ($result.Failures.Count -eq 1) 'one line below the total floor fails'
        Assert-True ($result.Failures[0] -eq 'Total line coverage 89.90% (899/1000) is below its floor of 90.00% (baseline 90.50% minus 0.5 percentage points).') "the total failure message names the scope, value, counts, floor, and baseline: $($result.Failures[0])"
        Assert-Throws { Assert-CoverageGatePassed $result } 'lower the baseline in eng/code-coverage-floors.json' 'a failing gate throws with remediation guidance'

        # Per-assembly: a well-covered assembly cannot hide a regression in another.
        $case = New-Case 'assembly-regression'
        $floors = New-Floors $case $assemblies 80 60
        New-Report $case @(
            (New-Package 'App.Web' 99 100 6 10),
            (New-Package 'App.Infrastructure' 990 1000 98 100)) | Out-Null
        $result = Invoke-CoverageGate $case $floors
        Assert-True ($result.Failures.Count -eq 1) "only the regressed assembly fails: $($result.Failures -join '; ')"
        Assert-True ($result.Failures[0] -like 'App.Web branch coverage 60.00% (6/10) is below its floor of 74.00%*') "the assembly failure names the assembly and kind: $($result.Failures[0])"
        Assert-True ($result.Markdown -match [regex]::Escape('| App.Web | 99.00% (99/100) / 89.00% | 60.00% (6/10) / 74.00% | **Fail** |')) 'the failing assembly row is marked in the summary'
        Assert-True ($result.Markdown -match 'Coverage gate failed') 'the summary lists failures'

        # An assembly with nothing to branch on counts as fully branch-covered rather than failing at 0%.
        $case = New-Case 'no-branches'
        $floors = New-Floors $case @{ 'App.Web' = @(90, 75) } 90 75
        New-Report $case @((New-Package 'App.Web' 95 100 0 0)) | Out-Null
        $result = Invoke-CoverageGate $case $floors
        Assert-True ($result.Failures.Count -eq 0) "a scope without branches passes the branch floor: $($result.Failures -join '; ')"
        Assert-True ($result.Markdown -match [regex]::Escape('| App.Web | 95.00% (95/100) / 89.00% | 100.00% (0/0) / 74.00% | Pass |')) 'a scope without branches is shown as fully covered'

        # A floored assembly missing from the report fails.
        $case = New-Case 'missing-assembly'
        $floors = New-Floors $case $assemblies
        New-Report $case @((New-Package 'App.Web' 95 100 8 10)) | Out-Null
        $result = Invoke-CoverageGate $case $floors
        Assert-True (@($result.Failures | Where-Object { $_ -eq 'App.Infrastructure has a coverage floor but is missing from the coverage report.' }).Count -eq 1) 'a missing assembly fails'

        # An assembly without a floor fails, which also catches a test assembly leaking into the report.
        $case = New-Case 'unexpected-assembly'
        $floors = New-Floors $case @{ 'App.Web' = @(90, 75) }
        New-Report $case @(
            (New-Package 'App.Web' 95 100 8 10),
            (New-Package 'App.Web.Tests' 100 100 10 10)) | Out-Null
        $result = Invoke-CoverageGate $case $floors
        Assert-True ($result.Failures.Count -eq 1 -and $result.Failures[0] -like 'App.Web.Tests is in the coverage report but has no floor*') 'an assembly without a floor fails'

        # Duplicate assembly entries are rejected rather than silently merged or skipped.
        $case = New-Case 'duplicate-assembly'
        $floors = New-Floors $case @{ 'App.Web' = @(90, 75) }
        New-Report $case @((New-Package 'App.Web' 95 100 8 10), (New-Package 'App.Web' 95 100 8 10)) | Out-Null
        Assert-Throws { Invoke-CoverageGate $case $floors } "Assembly 'App.Web' appears more than once" 'a duplicate assembly is rejected'

        # Report discovery: none, several, and an invalid document.
        $case = New-Case 'missing-report'
        $floors = New-Floors $case $assemblies
        Assert-Throws { Invoke-CoverageGate $case $floors } 'found 0' 'a missing report is rejected'
        Assert-Throws { Invoke-CoverageGate (Join-Path $root 'does-not-exist') $floors } 'does not exist' 'a missing report directory is rejected'

        $case = New-Case 'multiple-reports'
        $floors = New-Floors $case $assemblies
        New-Report $case @((New-Package 'App.Web' 95 100 8 10)) | Out-Null
        $nested = Join-Path $case 'second-run'
        New-Item -ItemType Directory -Path $nested | Out-Null
        New-Report $nested @((New-Package 'App.Web' 95 100 8 10)) | Out-Null
        Assert-Throws { Invoke-CoverageGate $case $floors } 'found 2' 'several reports, including nested ones, are rejected'

        $case = New-Case 'not-cobertura'
        $floors = New-Floors $case $assemblies
        Set-Content -LiteralPath (Join-Path $case 'coverage.cobertura.xml') -Value '<?xml version="1.0"?><report />' -Encoding utf8
        Assert-Throws { Invoke-CoverageGate $case $floors } 'is not a Cobertura report' 'a document without a coverage root is rejected'

        # Floors file validation.
        $case = New-Case 'bad-floors'
        New-Report $case @((New-Package 'App.Web' 95 100 8 10)) | Out-Null
        Assert-Throws { Invoke-CoverageGate $case (Join-Path $case 'absent.json') } 'does not exist' 'a missing floors file is rejected'
        $badFloors = Join-Path $case 'floors.json'
        '{ "tolerance": { "total": 5, "assembly": 1 }, "total": { "line": 90, "branch": 75 }, "assemblies": { "App.Web": { "line": 90, "branch": 75 } } }' |
            Set-Content -LiteralPath $badFloors -Encoding utf8
        Assert-Throws { Invoke-CoverageGate $case $badFloors } "'tolerance.total' must be between 0 and 2" 'an oversized tolerance is rejected'
        '{ "tolerance": { "total": 0.5, "assembly": 1 }, "total": { "line": 90, "branch": 75 }, "assemblies": { "App.Web": { "line": 90 } } }' |
            Set-Content -LiteralPath $badFloors -Encoding utf8
        Assert-Throws { Invoke-CoverageGate $case $badFloors } "'App.Web.branch' is required" 'a missing branch floor is rejected'
        '{ "tolerance": { "total": 0.5, "assembly": 1 }, "total": { "line": 101, "branch": 75 }, "assemblies": { "App.Web": { "line": 90, "branch": 75 } } }' |
            Set-Content -LiteralPath $badFloors -Encoding utf8
        Assert-Throws { Invoke-CoverageGate $case $badFloors } "'total.line' must be between 0 and 100" 'an out-of-range baseline is rejected'
        '{ "tolerance": { "total": 0.5, "assembly": 1 }, "total": { "line": 90, "branch": 75 }, "assemblies": {} }' |
            Set-Content -LiteralPath $badFloors -Encoding utf8
        Assert-Throws { Invoke-CoverageGate $case $badFloors } 'at least one production assembly' 'an empty assembly list is rejected'

        # The summary is appended to the step summary file even when the gate fails.
        $case = New-Case 'summary-file'
        $floors = New-Floors $case $assemblies
        New-Report $case @((New-Package 'App.Web' 10 100 1 10)) | Out-Null
        $summaryFile = Join-Path $case 'summary.md'
        $result = Invoke-CoverageGate $case $floors
        Write-CoverageGateSummary $result $summaryFile
        $written = Get-Content -LiteralPath $summaryFile -Raw
        Assert-True ($written -match '## Code coverage' -and $written -match '\*\*Coverage gate failed:\*\*') 'a failing summary is written to the summary file'

        # The repository's own floors file is valid and floors both production assemblies.
        $repositoryFloors = Read-CoverageFloors $FloorsPath
        foreach ($name in @('ReferenceApplication.Infrastructure', 'ReferenceApplication.Web')) {
            Assert-True ($repositoryFloors.Assemblies.Contains($name)) "the repository floors include $name"
        }
    }
    finally {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Output "Coverage gate self-test passed ($script:assertions assertions)."
}

if ($SelfTest) {
    Invoke-SelfTest
    return
}

if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    throw 'ReportDirectory is required unless SelfTest is specified.'
}

$gateResult = Invoke-CoverageGate $ReportDirectory $FloorsPath
Write-CoverageGateSummary $gateResult $SummaryPath
Assert-CoverageGatePassed $gateResult
