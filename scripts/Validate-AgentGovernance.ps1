[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$agentDirectory = Join-Path $repositoryRoot '.github/agents'
$profiles = @(
    @{ File = 'architecture-integration.agent.md'; Scope = 'identity normalization' },
    @{ File = 'quality-assurance.agent.md'; Scope = 'end-to-end allow, deny' },
    @{ File = 'documentation-adoption.agent.md'; Scope = 'Production-ready' }
)

$requiredProfileText = @(
    'target: github-copilot',
    'disable-model-invocation: true',
    'user-invocable: true',
    '## Prohibited actions',
    'Do not edit files',
    'execute commands',
    'access secrets',
    'approve work',
    '## Review output',
    'severity',
    'file/line evidence',
    'actionable',
    'Uncertainties',
    'validation not performed',
    'Never fabricate'
)

$errors = [System.Collections.Generic.List[string]]::new()
$expectedFiles = @($profiles | ForEach-Object { $_.File })
$actualFiles = @(
    Get-ChildItem -LiteralPath $agentDirectory -File -Filter '*.agent.md' |
        ForEach-Object { $_.Name }
)

foreach ($unexpected in @($actualFiles | Where-Object { $_ -notin $expectedFiles })) {
    $errors.Add("Unexpected custom-agent profile is not governed by this validator: $unexpected")
}

foreach ($profile in $profiles) {
    $path = Join-Path $agentDirectory $profile.File
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $errors.Add("Missing custom-agent profile: $($profile.File)")
        continue
    }

    $content = Get-Content -LiteralPath $path -Raw
    $frontmatter = [regex]::Match(
        $content,
        '\A---\r?\n(?<yaml>.*?)\r?\n---\r?\n',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $frontmatter.Success) {
        $errors.Add("$($profile.File) must start with YAML frontmatter.")
    }
    else {
        $toolLines = @(
            $frontmatter.Groups['yaml'].Value -split '\r?\n' |
                Where-Object { $_ -match '^tools:' }
        )
        if ($toolLines.Count -ne 1 -or $toolLines[0] -cne 'tools: ["read", "search"]') {
            $errors.Add("$($profile.File) must declare exactly tools: [`"read`", `"search`"].")
        }
    }

    foreach ($required in $requiredProfileText + $profile.Scope) {
        if (-not $content.Contains($required, [System.StringComparison]::OrdinalIgnoreCase)) {
            $errors.Add("$($profile.File) is missing required text: $required")
        }
    }

    foreach ($forbidden in @('mcp-servers:', '"edit"', '"execute"', '"web"', '"agent"', 'tools: ["*"]')) {
        if ($content.Contains($forbidden, [System.StringComparison]::OrdinalIgnoreCase)) {
            $errors.Add("$($profile.File) contains forbidden tool or MCP configuration: $forbidden")
        }
    }
}

$agentsPath = Join-Path $repositoryRoot 'AGENTS.md'
if (-not (Test-Path -LiteralPath $agentsPath -PathType Leaf)) {
    $errors.Add('Missing root AGENTS.md.')
}
else {
    $agents = Get-Content -LiteralPath $agentsPath -Raw
    foreach ($required in @(
        'practical integration example',
        'not as a product',
        'eng/ncat-baseline.json',
        'docs/walkthrough/refresh-ncat-baseline.md',
        'Do not run `dotnet new --force`',
        'dotnet restore ReferenceApplication.slnx --locked-mode',
        'dotnet build ReferenceApplication.slnx --configuration Release --no-restore',
        'dotnet format ReferenceApplication.slnx --verify-no-changes --no-restore --verbosity minimal',
        'dotnet test --solution ReferenceApplication.slnx --configuration Release --no-build',
        'Never copy real credentials',
        'must not edit files',
        'fabricate validation evidence'
    )) {
        if (-not $agents.Contains($required, [System.StringComparison]::OrdinalIgnoreCase)) {
            $errors.Add("AGENTS.md is missing required text: $required")
        }
    }
}

$guidePath = Join-Path $repositoryRoot 'docs/repository-agent-assurance.md'
if (-not (Test-Path -LiteralPath $guidePath -PathType Leaf)) {
    $errors.Add('Missing repository assurance guide.')
}
else {
    $guide = Get-Content -LiteralPath $guidePath -Raw
    foreach ($required in @(
        'Example safe prompts',
        'Prohibited-action handling',
        'Confirm the actual session',
        'configuration validation, not a live Copilot permission test',
        'Architecture and Integration Assurance',
        'Quality Assurance',
        'Documentation and Adoption Assurance',
        'Demonstration and Production boundary',
        'NCAT refresh and rollback',
        'Learning#534'
    )) {
        if (-not $guide.Contains($required, [System.StringComparison]::OrdinalIgnoreCase)) {
            $errors.Add("Repository assurance guide is missing required text: $required")
        }
    }
}

$refreshPath = Join-Path $repositoryRoot 'docs/walkthrough/refresh-ncat-baseline.md'
if (-not (Test-Path -LiteralPath $refreshPath -PathType Leaf)) {
    $errors.Add('Missing canonical NCAT baseline refresh procedure.')
}
else {
    $refresh = Get-Content -LiteralPath $refreshPath -Raw
    foreach ($required in @(
        'Do not run `dotnet new --force` over the integrated branch',
        'Verify reproducibility',
        'Prepare the baseline worktree',
        'Stage and review the new scaffold',
        'Overlay it onto the integrated application',
        'Keep ReferenceApplication-specific and AsiBackbone package entries',
        'Do not hand-merge lock files'
    )) {
        if (-not $refresh.Contains($required, [System.StringComparison]::OrdinalIgnoreCase)) {
            $errors.Add("NCAT baseline refresh procedure is missing required boundary text: $required")
        }
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "Validated $($profiles.Count) read-only assurance profiles, repository guidance and NCAT refresh boundaries."
