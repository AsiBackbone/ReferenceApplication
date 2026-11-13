[CmdletBinding()]
param(
    [Parameter()]
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),

    [Parameter()]
    [string] $Tag,

    [Parameter()]
    [string] $Repository = 'AsiBackbone/ReferenceApplication',

    [Parameter()]
    [AllowEmptyCollection()]
    [string[]] $AssetName,

    [Parameter()]
    [switch] $SelfTest
)

# Enforces the release-artifact signing decision recorded in docs/release-artifact-signing.md (#129).
#
# While the decision is "Deferred", a published release must carry no attached assets: GitHub's auto-generated source
# archives are the only distributables, and they are not uploaded assets. If a release gains an attached asset, the
# deferral no longer describes reality and this check fails until the decision is revisited and signing is implemented.
#
# Asset names come from -AssetName when supplied, otherwise from `gh release view <Tag>`. Any failure to read the
# decision or the release fails the check; nothing is treated as "no assets" by default.
#
# Run with -SelfTest to exercise the decision parsing and every pass/fail path without network access.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$decisionRelativePath = 'docs/release-artifact-signing.md'

function Get-SigningDecision([string] $DecisionText) {
    $match = [regex]::Match($DecisionText, '(?m)^\*\*Decision:\*\*\s*(?<decision>[A-Za-z]+)\b')
    if (-not $match.Success) {
        throw "Release artifact signing check failed: $decisionRelativePath does not record a '**Decision:**' line."
    }

    return $match.Groups['decision'].Value
}

function Assert-ReleaseAssetsMatchDecision([string] $Decision, [string] $ReleaseTag, [string[]] $Assets) {
    $attached = @($Assets | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

    switch ($Decision) {
        'Deferred' {
            if ($attached.Count -gt 0) {
                throw ("Release artifact signing check failed: release '$ReleaseTag' has attached asset(s) " +
                    "[$($attached -join ', ')], but $decisionRelativePath records signing as Deferred because no " +
                    'release assets are distributed. Revisit the decision and sign the exact published bytes before ' +
                    'publishing release assets.')
            }

            return "Release '$ReleaseTag' has no attached assets; the Deferred signing decision still holds."
        }
        default {
            throw ("Release artifact signing check failed: decision '$Decision' is not supported by this validator. " +
                'Implementing signing must extend this check to verify each asset''s Sigstore bundle and signer ' +
                'identity before the decision can change.')
        }
    }
}

function Get-PublishedAssetNames([string] $ReleaseTag, [string] $ReleaseRepository) {
    $names = & gh release view $ReleaseTag --repo $ReleaseRepository --json assets --jq '.assets[].name'
    if ($LASTEXITCODE -ne 0) {
        throw "Release artifact signing check failed: unable to read release '$ReleaseTag' from '$ReleaseRepository'."
    }

    return @($names)
}

function Invoke-SelfTest {
    $script:assertions = 0

    function Assert-Passes([string] $Name, [scriptblock] $Action) {
        try {
            $null = & $Action
        }
        catch {
            throw "Self-test '$Name' failed unexpectedly: $($_.Exception.Message)"
        }

        $script:assertions++
    }

    function Assert-Fails([string] $Name, [string] $ExpectedFragment, [scriptblock] $Action) {
        try {
            $null = & $Action
        }
        catch {
            if (-not $_.Exception.Message.Contains($ExpectedFragment, [StringComparison]::Ordinal)) {
                throw "Self-test '$Name' failed with an unexpected message: $($_.Exception.Message)"
            }

            $script:assertions++
            return
        }

        throw "Self-test '$Name' passed but was expected to fail."
    }

    Assert-Passes 'checked-in decision parses as Deferred' {
        $text = Get-Content -LiteralPath (Join-Path $RepositoryRoot $decisionRelativePath) -Raw
        if ((Get-SigningDecision $text) -ne 'Deferred') {
            throw 'The checked-in decision is not Deferred.'
        }
    }
    Assert-Fails 'missing decision line' "does not record a '**Decision:**' line" {
        Get-SigningDecision "# Release artifact signing`n`nNo decision here.`n"
    }
    Assert-Passes 'deferred with no assets' {
        Assert-ReleaseAssetsMatchDecision 'Deferred' 'v1.0.1' @()
    }
    Assert-Passes 'deferred with blank asset output' {
        Assert-ReleaseAssetsMatchDecision 'Deferred' 'v1.0.1' @('', ' ')
    }
    Assert-Fails 'deferred with an attached archive' 'has attached asset(s) [ReferenceApplication-1.0.2.zip]' {
        Assert-ReleaseAssetsMatchDecision 'Deferred' 'v1.0.2' @('ReferenceApplication-1.0.2.zip')
    }
    Assert-Fails 'deferred with an archive and its bundle' 'has attached asset(s)' {
        Assert-ReleaseAssetsMatchDecision 'Deferred' 'v1.0.2' @('app.zip', 'app.zip.sigstore.json')
    }
    Assert-Fails 'unsupported decision fails closed' "decision 'Implemented' is not supported" {
        Assert-ReleaseAssetsMatchDecision 'Implemented' 'v1.0.2' @('app.zip', 'app.zip.sigstore.json')
    }

    Write-Host "Release artifact signing self-test passed ($script:assertions assertions)."
}

$RepositoryRoot = (Resolve-Path $RepositoryRoot).Path

if ($SelfTest) {
    Invoke-SelfTest
    return
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    throw 'Release artifact signing check failed: -Tag vMAJOR.MINOR.PATCH is required unless -SelfTest is specified.'
}

$decisionPath = Join-Path $RepositoryRoot $decisionRelativePath
if (-not (Test-Path -LiteralPath $decisionPath -PathType Leaf)) {
    throw "Release artifact signing check failed: required file is missing: $decisionPath"
}

$decision = Get-SigningDecision (Get-Content -LiteralPath $decisionPath -Raw)
$assets = if ($PSBoundParameters.ContainsKey('AssetName')) { @($AssetName) } else { Get-PublishedAssetNames $Tag $Repository }

Write-Host (Assert-ReleaseAssetsMatchDecision $decision $Tag $assets)
