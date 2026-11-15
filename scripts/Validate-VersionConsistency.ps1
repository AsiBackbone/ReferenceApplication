[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ProjectProperty {
    param(
        [xml]$Project,
        [string]$Name,
        [System.Collections.Generic.List[string]]$Failures
    )

    $nodes = @($Project.Project.PropertyGroup.ChildNodes | Where-Object {
        $_.NodeType -eq [System.Xml.XmlNodeType]::Element -and $_.Name -eq $Name
    })

    if ($nodes.Count -eq 0) {
        $Failures.Add("Directory.Build.props is missing $Name.")
        return ''
    }

    return $nodes[0].InnerText.Trim()
}

function Test-VersionDocuments {
    param(
        [string]$DirectoryBuildProps,
        [string]$DirectoryPackagesProps,
        [string]$Changelog,
        [string]$Readme,
        [string]$BaselineJson,
        [string]$ScaffoldWalkthrough,
        [string]$PackageWalkthrough
    )

    $failures = [System.Collections.Generic.List[string]]::new()

    try {
        [xml]$build = $DirectoryBuildProps
    }
    catch {
        $failures.Add("Directory.Build.props is not valid XML: $($_.Exception.Message)")
        return $failures.ToArray()
    }

    $appVersion = Get-ProjectProperty $build 'VersionPrefix' $failures
    $assemblyVersion = Get-ProjectProperty $build 'AssemblyVersion' $failures
    $fileVersion = Get-ProjectProperty $build 'FileVersion' $failures

    if ($appVersion -notmatch '^\d+\.\d+\.\d+$') {
        $failures.Add("Directory.Build.props VersionPrefix '$appVersion' is not MAJOR.MINOR.PATCH.")
    }
    else {
        if ($assemblyVersion -ne "$appVersion.0") {
            $failures.Add("Directory.Build.props AssemblyVersion expected '$appVersion.0' but found '$assemblyVersion'.")
        }

        if ($fileVersion -ne "$appVersion.0") {
            $failures.Add("Directory.Build.props FileVersion expected '$appVersion.0' but found '$fileVersion'.")
        }

        $releasedVersion = [regex]::Match($Changelog, '(?m)^##\s+(?<version>\d+\.\d+\.\d+)\s+-\s+\d{4}-\d{2}-\d{2}\s*$')
        if (-not $releasedVersion.Success -or $releasedVersion.Groups['version'].Value -ne $appVersion) {
            $actual = if ($releasedVersion.Success) { $releasedVersion.Groups['version'].Value } else { '<missing>' }
            $failures.Add("CHANGELOG latest released version expected '$appVersion' but found '$actual'.")
        }

        $stableReleasePattern = '\*\*Stable release:\s*`' + [regex]::Escape($appVersion) + '`\.\*\*'
        if ($Readme -notmatch $stableReleasePattern) {
            $failures.Add("README stable release marker does not name '$appVersion'.")
        }
    }

    try {
        $baseline = $BaselineJson | ConvertFrom-Json
        $ncatVersion = [string]$baseline.version
    }
    catch {
        $failures.Add("eng/ncat-baseline.json is not valid JSON: $($_.Exception.Message)")
        $ncatVersion = ''
    }

    if ($ncatVersion -notmatch '^\d+\.\d+\.\d+$') {
        $failures.Add("eng/ncat-baseline.json version '$ncatVersion' is not MAJOR.MINOR.PATCH.")
    }
    else {
        foreach ($required in @(
            "The current NCAT baseline is **$ncatVersion**",
            "unmodified $ncatVersion scaffold",
            "``ncat-v$ncatVersion``"
        )) {
            if (-not $Readme.Contains($required, [StringComparison]::Ordinal)) {
                $failures.Add("README current NCAT marker is missing: $required")
            }
        }

        foreach ($required in @(
            "baseline has since been refreshed to NCAT $ncatVersion",
            "current baseline tag (currently ``ncat-v$ncatVersion``)"
        )) {
            if (-not $ScaffoldWalkthrough.Contains($required, [StringComparison]::Ordinal)) {
                $failures.Add("Walkthrough Step 1 current NCAT marker is missing: $required")
            }
        }
    }

    try {
        [xml]$packages = $DirectoryPackagesProps
        $asiBackboneVersions = @($packages.Project.ItemGroup.PackageVersion | Where-Object {
            [string]$_.Include -like 'AsiBackbone.*'
        } | ForEach-Object { [string]$_.Version } | Sort-Object -Unique)
    }
    catch {
        $failures.Add("Directory.Packages.props is not valid XML: $($_.Exception.Message)")
        $asiBackboneVersions = @()
    }

    if ($asiBackboneVersions.Count -ne 1 -or $asiBackboneVersions[0] -notmatch '^\d+\.\d+\.\d+$') {
        $actual = if ($asiBackboneVersions.Count -eq 0) { '<missing>' } else { $asiBackboneVersions -join ', ' }
        $failures.Add("AsiBackbone central package versions must use one MAJOR.MINOR.PATCH value; found '$actual'.")
    }
    else {
        $asiBackboneVersion = $asiBackboneVersions[0]
        foreach ($required in @(
            "## Current package set — AsiBackbone ``$asiBackboneVersion``",
            "current repository pins nine AsiBackbone packages at ``$asiBackboneVersion``",
            "releases/tag/v$asiBackboneVersion"
        )) {
            if (-not $PackageWalkthrough.Contains($required, [StringComparison]::Ordinal)) {
                $failures.Add("Walkthrough Step 2 current AsiBackbone marker is missing: $required")
            }
        }

        $sampleVersionCount = [regex]::Matches(
            $PackageWalkthrough,
            '<PackageVersion Include="AsiBackbone\.[^"]+" Version="' + [regex]::Escape($asiBackboneVersion) + '" />'
        ).Count
        if ($sampleVersionCount -ne 9) {
            $failures.Add("Walkthrough Step 2 expected nine current AsiBackbone package examples at '$asiBackboneVersion' but found $sampleVersionCount.")
        }
    }

    return $failures.ToArray()
}

function Invoke-SelfTest {
    $appVersion = '1.2.3'
    $ncatVersion = '4.5.6'
    $asiBackboneVersion = '7.8.9'
    $packageItems = 1..9 | ForEach-Object {
        "<PackageVersion Include=`"AsiBackbone.Package$_`" Version=`"$asiBackboneVersion`" />"
    }

    $fixture = @{
        DirectoryBuildProps = "<Project><PropertyGroup><VersionPrefix>$appVersion</VersionPrefix><AssemblyVersion>$appVersion.0</AssemblyVersion><FileVersion>$appVersion.0</FileVersion></PropertyGroup></Project>"
        DirectoryPackagesProps = "<Project><ItemGroup>$($packageItems -join '')</ItemGroup></Project>"
        Changelog = "# Changelog`n`n## Unreleased`n`n## $appVersion - 2026-01-02"
        Readme = "**Stable release: ``$appVersion``.** The current NCAT baseline is **$ncatVersion**. The unmodified $ncatVersion scaffold is tagged ``ncat-v$ncatVersion``."
        BaselineJson = "{`"version`":`"$ncatVersion`"}"
        ScaffoldWalkthrough = "The baseline has since been refreshed to NCAT $ncatVersion. Use the current baseline tag (currently ``ncat-v$ncatVersion``)."
        PackageWalkthrough = "## Current package set — AsiBackbone ``$asiBackboneVersion```nThe current repository pins nine AsiBackbone packages at ``$asiBackboneVersion``. See releases/tag/v$asiBackboneVersion.`n$($packageItems -join "`n")"
    }

    $valid = @(Test-VersionDocuments @fixture)
    if ($valid.Count -ne 0) {
        throw "Valid version fixture failed: $($valid -join '; ')"
    }

    $cases = @(
        @{ Name = 'stale application release'; Key = 'Readme'; Old = "``$appVersion``"; New = '``1.2.2``' },
        @{ Name = 'stale NCAT README baseline'; Key = 'Readme'; Old = "**$ncatVersion**"; New = '**4.5.5**' },
        @{ Name = 'stale NCAT walkthrough tag'; Key = 'ScaffoldWalkthrough'; Old = "ncat-v$ncatVersion"; New = 'ncat-v4.5.5' },
        @{ Name = 'stale AsiBackbone walkthrough heading'; Key = 'PackageWalkthrough'; Old = "AsiBackbone ``$asiBackboneVersion``"; New = 'AsiBackbone ``7.8.8``' }
    )

    foreach ($case in $cases) {
        $candidate = $fixture.Clone()
        $candidate[$case.Key] = $candidate[$case.Key].Replace($case.Old, $case.New)
        $failures = @(Test-VersionDocuments @candidate)
        if ($failures.Count -eq 0) {
            throw "Self-test did not reject $($case.Name)."
        }
    }

    Write-Host 'Version consistency validator self-test passed.'
}

if ($SelfTest) {
    Invoke-SelfTest
    exit 0
}

$requiredFiles = @{
    DirectoryBuildProps = 'Directory.Build.props'
    DirectoryPackagesProps = 'Directory.Packages.props'
    Changelog = 'CHANGELOG.md'
    Readme = 'README.md'
    BaselineJson = 'eng/ncat-baseline.json'
    ScaffoldWalkthrough = 'docs/walkthrough/01-scaffold-from-ncat.md'
    PackageWalkthrough = 'docs/walkthrough/02-add-asibackbone-packages.md'
}

$documents = @{}
foreach ($entry in $requiredFiles.GetEnumerator()) {
    $path = Join-Path $RepositoryRoot $entry.Value
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required version source is missing: $($entry.Value)"
    }

    $documents[$entry.Key] = Get-Content -LiteralPath $path -Raw
}

$failures = @(Test-VersionDocuments @documents)
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host 'Application, NCAT baseline, and AsiBackbone package versions are consistent.'
