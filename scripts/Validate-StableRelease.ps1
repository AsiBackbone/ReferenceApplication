[CmdletBinding()]
param(
    [Parameter()]
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),

    [Parameter()]
    [switch] $RequireStable,

    [Parameter()]
    [string] $Tag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string] $Message) {
    throw "Stable release validation failed: $Message"
}

$repositoryRootPath = (Resolve-Path $RepositoryRoot).Path
$propsPath = Join-Path $repositoryRootPath 'Directory.Build.props'
$readmePath = Join-Path $repositoryRootPath 'README.md'
$changelogPath = Join-Path $repositoryRootPath 'CHANGELOG.md'
$policyPath = Join-Path $repositoryRootPath 'docs/stable-release-policy-v1.md'

foreach ($path in @($propsPath, $readmePath, $changelogPath, $policyPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Fail "Required file is missing: $path"
    }
}

[xml] $props = Get-Content -LiteralPath $propsPath -Raw
$propertyGroup = @($props.Project.PropertyGroup) |
    Where-Object { $_.VersionPrefix } |
    Select-Object -First 1

if ($null -eq $propertyGroup) {
    Fail 'Directory.Build.props does not define VersionPrefix.'
}

$versionPrefix = [string] $propertyGroup.VersionPrefix
$versionSuffix = [string] $propertyGroup.VersionSuffix

if ($versionPrefix -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    Fail "VersionPrefix '$versionPrefix' is not MAJOR.MINOR.PATCH SemVer."
}

$effectiveVersion = if ([string]::IsNullOrWhiteSpace($versionSuffix)) {
    $versionPrefix
}
else {
    "$versionPrefix-$versionSuffix"
}

$readme = Get-Content -LiteralPath $readmePath -Raw
if ($readme -notmatch [regex]::Escape('docs/stable-release-policy-v1.md')) {
    Fail 'README.md does not link to docs/stable-release-policy-v1.md.'
}

$changelog = Get-Content -LiteralPath $changelogPath -Raw
if ($changelog -notmatch '(?m)^## Unreleased\s*$') {
    Fail 'CHANGELOG.md does not contain the Unreleased heading.'
}

Write-Host "Repository release metadata is coherent for version $effectiveVersion."

if (-not $RequireStable) {
    Write-Host 'Prerelease mode: stable-only version, changelog, and tag checks were not requested.'
    exit 0
}

if (-not [string]::IsNullOrWhiteSpace($versionSuffix)) {
    Fail "VersionSuffix must be empty for a stable release; found '$versionSuffix'."
}

$majorVersion = [int] ($versionPrefix.Split('.')[0])
if ($majorVersion -lt 1) {
    Fail "Stable release VersionPrefix must have major version 1 or greater; found '$versionPrefix'."
}

$escapedVersion = [regex]::Escape($versionPrefix)
$releaseHeadingPattern = "(?m)^## $escapedVersion - \d{4}-\d{2}-\d{2}\s*$"
if ($changelog -notmatch $releaseHeadingPattern) {
    Fail "CHANGELOG.md must contain '## $versionPrefix - YYYY-MM-DD'."
}

if ([string]::IsNullOrWhiteSpace($Tag) -and $env:GITHUB_REF_TYPE -eq 'tag') {
    $Tag = $env:GITHUB_REF_NAME
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    Fail 'Stable validation requires -Tag vMAJOR.MINOR.PATCH (or a GitHub tag ref).'
}

$expectedTag = "v$versionPrefix"
if ($Tag -ne $expectedTag) {
    Fail "Tag '$Tag' does not match version '$expectedTag'."
}

Push-Location $repositoryRootPath
try {
    $insideWorkTree = (& git rev-parse --is-inside-work-tree 2>$null)
    if ($LASTEXITCODE -ne 0 -or $insideWorkTree -ne 'true') {
        Fail 'RepositoryRoot is not a Git work tree.'
    }

    $status = (& git status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        Fail 'Unable to read Git working-tree status.'
    }

    if ($status) {
        Fail 'The working tree must be clean for stable tag validation.'
    }

    $tagType = (& git cat-file -t $Tag 2>$null)
    if ($LASTEXITCODE -ne 0) {
        Fail "Tag '$Tag' does not exist locally."
    }

    if ($tagType -ne 'tag') {
        Fail "Tag '$Tag' is not annotated. Stable release tags must be annotated."
    }

    $headCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        Fail 'Unable to resolve HEAD.'
    }

    $tagCommit = (& git rev-parse "$Tag^{commit}").Trim()
    if ($LASTEXITCODE -ne 0) {
        Fail "Unable to resolve tag '$Tag' to a commit."
    }

    if ($tagCommit -ne $headCommit) {
        Fail "Tag '$Tag' resolves to $tagCommit, but HEAD is $headCommit."
    }
}
finally {
    Pop-Location
}

Write-Host "Stable release metadata and annotated tag validation passed for $Tag."
