#Requires -Version 7.2
<#
.SYNOPSIS
Verifies that the ncat-baseline branch is exactly what its declared NCAT release generates.

.DESCRIPTION
Reads the adopted version and generation inputs from eng/ncat-baseline.json and verifies that version against the
annotated ncat-v<version> tag on the baseline commit. It installs that exact NetCoreApplicationTemplate release into
an isolated, temporary template hive, generates the scaffold twice, and fails if the two generations differ. It then
mirrors the scaffold onto a detached worktree of the baseline commit and stages it, so Git normalizes line endings
through the baseline's own .gitattributes, and fails with a focused diff when any material change remains.

No generated value is normalized or ignored. Line-ending differences are reported separately because the baseline's
.gitattributes, not this script, defines them as non-material; generated files excluded by the baseline's .gitignore
are reported because the baseline commit could never have contained them.

The script never changes a branch, tag, or the locally installed templates. It requires git and the .NET SDK
selected by global.json; -SelfTest requires only git.

.PARAMETER Ref
The baseline commit to verify. Defaults to the manifest's baselineRef, origin/ncat-baseline. Fetch it and the tags
first.

.PARAMETER TemplateVersion
The NCAT version to verify against. Omit it to read the version from the ncat-v<version> tag on the commit. Pass it
only to verify an untagged refresh candidate before merge; a tagged commit must agree with it.

.PARAMETER ManifestPath
The generation manifest. Defaults to eng/ncat-baseline.json.

.PARAMETER KeepWorkDirectory
Keep the temporary directory holding the template hive and generated scaffolds for inspection.

.PARAMETER SelfTest
Exercise the tag resolution, determinism, and comparison logic against a synthetic repository without the .NET SDK.

.EXAMPLE
./scripts/Test-NcatBaselineReproducibility.ps1

.EXAMPLE
./scripts/Test-NcatBaselineReproducibility.ps1 -Ref origin/my-baseline-refresh -TemplateVersion 2.11.6
#>
[CmdletBinding()]
param(
    [Parameter()]
    [string] $Ref,

    [Parameter()]
    [string] $TemplateVersion,

    [Parameter()]
    [string] $ManifestPath,

    [Parameter()]
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),

    [Parameter()]
    [switch] $KeepWorkDirectory,

    [Parameter()]
    [switch] $SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$versionPattern = '\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?'

function Invoke-Git {
    param(
        [Parameter(Mandatory)]
        [string] $Repository,

        [Parameter(Mandatory)]
        [string[]] $Arguments,

        [switch] $AllowFailure
    )

    # Line endings must depend only on the repository's .gitattributes, never on the caller's Git configuration.
    $output = & git -C $Repository -c core.autocrlf=false -c core.safecrlf=false -c core.quotePath=false @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "git $($Arguments -join ' ') failed with exit code $exitCode."
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Lines    = @($output | Where-Object { -not [string]::IsNullOrEmpty($_) } | ForEach-Object { [string] $_ })
    }
}

function Get-FileMap {
    param([Parameter(Mandatory)] [string] $Root)

    $rootPath = [System.IO.Path]::GetFullPath($Root)
    $map = [System.Collections.Generic.SortedDictionary[string, string]]::new([System.StringComparer]::Ordinal)
    foreach ($file in [System.IO.Directory]::EnumerateFiles($rootPath, '*', [System.IO.SearchOption]::AllDirectories)) {
        $relative = [System.IO.Path]::GetRelativePath($rootPath, $file).Replace('\', '/')
        if ($relative -eq '.git' -or $relative.StartsWith('.git/', [System.StringComparison]::Ordinal)) {
            continue
        }

        $map[$relative] = $file
    }

    # The unary comma keeps the pipeline from enumerating the dictionary, so the caller receives the dictionary itself.
    # Write-Output -NoEnumerate is not equivalent: assigned from a function, it yields a one-element array.
    return , $map
}

function Compare-DirectoryBytes {
    param(
        [Parameter(Mandatory)] [string] $Left,
        [Parameter(Mandatory)] [string] $Right
    )

    $leftFiles = Get-FileMap $Left
    $rightFiles = Get-FileMap $Right
    $paths = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($path in @($leftFiles.Keys) + @($rightFiles.Keys)) {
        [void] $paths.Add($path)
    }

    $differences = foreach ($path in $paths) {
        if (-not $leftFiles.ContainsKey($path) -or -not $rightFiles.ContainsKey($path)) {
            $path
            continue
        }

        $leftHash = (Get-FileHash -LiteralPath $leftFiles[$path] -Algorithm SHA256).Hash
        $rightHash = (Get-FileHash -LiteralPath $rightFiles[$path] -Algorithm SHA256).Hash
        if ($leftHash -ne $rightHash) {
            $path
        }
    }

    return @($differences)
}

function Sync-Scaffold {
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $Destination
    )

    Get-ChildItem -LiteralPath $Destination -Force |
        Where-Object { $_.Name -ne '.git' } |
        Remove-Item -Recurse -Force

    $files = Get-FileMap $Source
    foreach ($relative in $files.Keys) {
        $target = Join-Path $Destination $relative
        [void] [System.IO.Directory]::CreateDirectory((Split-Path -Parent $target))
        [System.IO.File]::Copy($files[$relative], $target, $true)
    }
}

function Resolve-BaselineVersion {
    param(
        [Parameter(Mandatory)] [string] $Repository,
        [Parameter(Mandatory)] [string] $Commit,
        [Parameter(Mandatory)] [string] $TagPrefix,
        [string] $RequestedVersion
    )

    $tagPattern = '^' + [regex]::Escape($TagPrefix) + "(?<version>$versionPattern)$"
    $tags = @((Invoke-Git $Repository @('tag', '--list', "$TagPrefix*", '--points-at', $Commit)).Lines |
        Where-Object { $_ -match $tagPattern })

    if ($tags.Count -gt 1) {
        throw ("Commit $Commit has more than one baseline tag ($($tags -join ', ')); a baseline commit records " +
            'exactly one NCAT release.')
    }

    $tag = $null
    $tagVersion = $null
    if ($tags.Count -eq 1) {
        $tag = $tags[0]
        $tagType = (Invoke-Git $Repository @('cat-file', '-t', "refs/tags/$tag")).Lines[0]
        if ($tagType -ne 'tag') {
            throw "Tag $tag is lightweight. Baseline tags must be annotated, as the refresh guide creates them."
        }

        $tagVersion = $tag.Substring($TagPrefix.Length)
    }

    if (-not [string]::IsNullOrWhiteSpace($RequestedVersion)) {
        if ($null -ne $tagVersion -and $tagVersion -ne $RequestedVersion) {
            throw "Commit $Commit is tagged $tag, which records NCAT $tagVersion, but -TemplateVersion is $RequestedVersion."
        }

        return [pscustomobject]@{
            Version = $RequestedVersion
            Tag     = $tag
            Source  = if ($null -ne $tag) { "tag $tag" } else { 'parameter; untagged candidate' }
        }
    }

    if ($null -eq $tagVersion) {
        throw ("Commit $Commit has no annotated $TagPrefix<version> tag, so it records no NCAT release. Tag the " +
            'merged baseline as the refresh guide describes, or pass -TemplateVersion to verify an untagged candidate.')
    }

    return [pscustomobject]@{
        Version = $tagVersion
        Tag     = $tag
        Source  = "tag $tag"
    }
}

function Compare-BaselineToScaffold {
    param(
        [Parameter(Mandatory)] [string] $Repository,
        [Parameter(Mandatory)] [string] $Commit,
        [Parameter(Mandatory)] [string] $ScaffoldDirectory,
        [Parameter(Mandatory)] [string] $WorkDirectory
    )

    $worktree = Join-Path $WorkDirectory 'baseline'
    [void] (Invoke-Git $Repository @('worktree', 'add', '--quiet', '--detach', $worktree, $Commit))
    try {
        $byteDifferences = @(Compare-DirectoryBytes $worktree $ScaffoldDirectory)

        Sync-Scaffold $ScaffoldDirectory $worktree
        [void] (Invoke-Git $worktree @('add', '--all'))
        [void] (Invoke-Git $worktree @('add', '--renormalize', '.'))

        $material = (Invoke-Git $worktree @('diff', '--cached', '--no-renames', '--name-only')).Lines
        $ignored = (Invoke-Git $worktree @('ls-files', '--others', '--ignored', '--exclude-standard')).Lines

        $stat = @()
        $diff = @()
        if ($material.Count -gt 0) {
            $stat = (Invoke-Git $worktree @('diff', '--cached', '--no-renames', '--no-color', '--stat')).Lines
            $diff = (Invoke-Git $worktree @('diff', '--cached', '--no-renames', '--no-color')).Lines
        }

        return [pscustomobject]@{
            Material        = @($material)
            LineEndingsOnly = @($byteDifferences | Where-Object { $_ -notin $material -and $_ -notin $ignored })
            Ignored         = @($ignored)
            Stat            = @($stat)
            Diff            = @($diff)
        }
    }
    finally {
        [void] (Invoke-Git $Repository @('worktree', 'remove', '--force', $worktree) -AllowFailure)
        Remove-Item -LiteralPath $worktree -Recurse -Force -ErrorAction SilentlyContinue
        [void] (Invoke-Git $Repository @('worktree', 'prune') -AllowFailure)
    }
}

function Read-Manifest {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Generation manifest is missing: $Path"
    }

    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    foreach ($property in 'templatePackage', 'version', 'templateShortName', 'name', 'options', 'baselineRef', 'tagPrefix') {
        if ($null -eq $manifest.PSObject.Properties[$property] -or
            [string]::IsNullOrWhiteSpace([string] $manifest.$property)) {
            throw "Generation manifest $Path does not define '$property'."
        }
    }

    if ([string] $manifest.version -notmatch "^$versionPattern$") {
        throw "Generation manifest version '$($manifest.version)' is not a semantic version."
    }

    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.AddRange([string[]] @('--name', [string] $manifest.name))
    foreach ($option in $manifest.options.PSObject.Properties) {
        if ($option.Name -notmatch '^[A-Za-z][A-Za-z0-9]*$' -or [string] $option.Value -notmatch '^[A-Za-z0-9._-]+$') {
            throw "Generation manifest option '$($option.Name)' is not a plain template option and value."
        }

        $arguments.AddRange([string[]] @("--$($option.Name)", [string] $option.Value))
    }

    return [pscustomobject]@{
        TemplatePackage   = [string] $manifest.templatePackage
        Version           = [string] $manifest.version
        TemplateShortName = [string] $manifest.templateShortName
        BaselineRef       = [string] $manifest.baselineRef
        TagPrefix         = [string] $manifest.tagPrefix
        Arguments         = $arguments.ToArray()
    }
}

function Invoke-Dotnet {
    param([Parameter(Mandatory)] [string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function New-Scaffold {
    param(
        [Parameter(Mandatory)] $Manifest,
        [Parameter(Mandatory)] [string] $Version,
        [Parameter(Mandatory)] [string] $Hive,
        [Parameter(Mandatory)] [string] $Output
    )

    $arguments = @('new', $Manifest.TemplateShortName) + $Manifest.Arguments +
        @('--output', $Output, '--debug:custom-hive', $Hive)
    Invoke-Dotnet $arguments

    # NCAT records its own release in the generated Directory.Build.props; a prerelease suffix is recorded separately.
    $versionPrefix = $Version.Split('-')[0]
    $props = Join-Path $Output 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $props -PathType Leaf) -or
        (Get-Content -LiteralPath $props -Raw) -notmatch "<VersionPrefix>$([regex]::Escape($versionPrefix))</VersionPrefix>") {
        throw "The generated scaffold does not record NCAT $Version in Directory.Build.props; the wrong template package was used."
    }
}

function Write-Evidence {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string[]] $Lines
    )

    $Lines | ForEach-Object { Write-Host $_ }
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        @('```text') + $Lines + @('```') | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
    }
}

function Format-PathList {
    param([string[]] $Paths, [int] $Limit = 10)

    $shown = @($Paths | Select-Object -First $Limit | ForEach-Object { "    $_" })
    if ($Paths.Count -gt $Limit) {
        $shown += "    ... and $($Paths.Count - $Limit) more"
    }

    return $shown
}

function Invoke-SelfTest {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) "ncat-baseline-selftest-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $repository = Join-Path $root 'repository'
    $failures = [System.Collections.Generic.List[string]]::new()

    function Write-TestFile([string] $Directory, [string] $Path, [string] $Content) {
        $target = Join-Path $Directory $Path
        [void] [System.IO.Directory]::CreateDirectory((Split-Path -Parent $target))
        [System.IO.File]::WriteAllText($target, $Content)
    }

    function New-TestScaffold([string] $Name, [hashtable] $Extra = @{}) {
        $directory = Join-Path $root $Name
        Write-TestFile $directory '.gitattributes' "* text=auto eol=lf`n*.cs text eol=crlf`n"
        Write-TestFile $directory '.gitignore' "bin/`n"
        Write-TestFile $directory 'Directory.Build.props' "<VersionPrefix>1.2.3</VersionPrefix>`n"
        Write-TestFile $directory 'src/Program.cs' "class Program { }`n"
        Write-TestFile $directory 'bin/output.txt' "generated build output`n"
        foreach ($path in $Extra.Keys) {
            Write-TestFile $directory $path $Extra[$path]
        }

        return $directory
    }

    function Add-Commit([string] $Message) {
        [void] (Invoke-Git $repository @('add', '--all'))
        [void] (Invoke-Git $repository @('commit', '--quiet', '--no-verify', '-m', $Message))
        return (Invoke-Git $repository @('rev-parse', 'HEAD')).Lines[0]
    }

    function Assert-Case([string] $Name, [scriptblock] $Check) {
        try {
            if (-not (& $Check)) {
                $failures.Add("Self-test '$Name' failed.")
            }
        }
        catch {
            $failures.Add("Self-test '$Name' threw: $($_.Exception.Message)")
        }
    }

    function Assert-Throws([string] $Name, [scriptblock] $Action) {
        try {
            & $Action | Out-Null
            $failures.Add("Self-test '$Name' expected an error but succeeded.")
        }
        catch {
            # Expected.
        }
    }

    try {
        [void] [System.IO.Directory]::CreateDirectory($repository)
        [void] (Invoke-Git $repository @('init', '--quiet', '--initial-branch', 'main'))
        [void] (Invoke-Git $repository @('config', 'user.name', 'Self Test'))
        [void] (Invoke-Git $repository @('config', 'user.email', 'self-test@example.invalid'))
        [void] (Invoke-Git $repository @('config', 'commit.gpgsign', 'false'))
        [void] (Invoke-Git $repository @('config', 'tag.gpgsign', 'false'))

        $scaffold = New-TestScaffold 'scaffold'
        Sync-Scaffold $scaffold $repository
        $baseline = Add-Commit 'Raw scaffold'
        [void] (Invoke-Git $repository @('tag', '--annotate', 'ncat-v1.2.3', '--message', 'Raw scaffold', $baseline))

        Write-TestFile $repository 'src/Program.cs' "class Program { int edited; }`n"
        $edited = Add-Commit 'Manual edit'
        [void] (Invoke-Git $repository @('tag', 'ncat-v9.9.9', $edited))

        Write-TestFile $repository 'NOTES.md' "Not generated.`n"
        $added = Add-Commit 'Manual addition'

        $removedScaffold = New-TestScaffold 'scaffold-with-new-file' @{ 'src/Added.cs' = "class Added { }`n" }
        $work = Join-Path $root 'work'
        [void] [System.IO.Directory]::CreateDirectory($work)

        Assert-Case 'reproducible baseline has no material change' {
            $result = Compare-BaselineToScaffold $repository $baseline $scaffold $work
            $result.Material.Count -eq 0 -and
                $result.LineEndingsOnly -contains 'src/Program.cs' -and
                $result.Ignored -contains 'bin/output.txt'
        }
        Assert-Case 'manual edit is material' {
            (Compare-BaselineToScaffold $repository $edited $scaffold $work).Material -contains 'src/Program.cs'
        }
        Assert-Case 'manual addition is material' {
            (Compare-BaselineToScaffold $repository $added $scaffold $work).Material -contains 'NOTES.md'
        }
        Assert-Case 'missing generated file is material' {
            (Compare-BaselineToScaffold $repository $baseline $removedScaffold $work).Material -contains 'src/Added.cs'
        }
        Assert-Case 'comparison removes its worktree' {
            (Invoke-Git $repository @('worktree', 'list', '--porcelain')).Lines.Where({ $_ -like 'worktree *' }).Count -eq 1
        }
        Assert-Case 'version comes from the annotated tag' {
            (Resolve-BaselineVersion $repository $baseline 'ncat-v').Version -eq '1.2.3'
        }
        Assert-Case 'matching version parameter is accepted on a tagged commit' {
            (Resolve-BaselineVersion $repository $baseline 'ncat-v' '1.2.3').Tag -eq 'ncat-v1.2.3'
        }
        Assert-Case 'untagged candidate is accepted with a version parameter' {
            $resolved = Resolve-BaselineVersion $repository $added 'ncat-v' '1.2.4'
            $resolved.Version -eq '1.2.4' -and $null -eq $resolved.Tag
        }
        Assert-Throws 'untagged commit without a version parameter' { Resolve-BaselineVersion $repository $added 'ncat-v' }
        Assert-Throws 'version parameter that disagrees with the tag' {
            Resolve-BaselineVersion $repository $baseline 'ncat-v' '1.2.4'
        }
        Assert-Throws 'lightweight baseline tag' { Resolve-BaselineVersion $repository $edited 'ncat-v' }
        Assert-Case 'identical generations are deterministic' {
            @(Compare-DirectoryBytes $scaffold (New-TestScaffold 'scaffold-repeat')).Count -eq 0
        }
        Assert-Case 'differing generations are detected' {
            $nondeterministic = New-TestScaffold 'scaffold-nondeterministic' @{ 'src/Program.cs' = "class Program { }`r`n" }
            @(Compare-DirectoryBytes $scaffold $nondeterministic) -contains 'src/Program.cs'
        }
    }
    finally {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($failures.Count -gt 0) {
        $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
        exit 1
    }

    Write-Host 'NCAT baseline reproducibility self-test passed.'
    exit 0
}

if ($SelfTest) {
    Invoke-SelfTest
}

$repositoryRootPath = (Resolve-Path -LiteralPath $RepositoryRoot).Path
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $repositoryRootPath 'eng/ncat-baseline.json'
}

$manifest = Read-Manifest $ManifestPath
if ([string]::IsNullOrWhiteSpace($Ref)) {
    $Ref = $manifest.BaselineRef
}

if ($Ref -notmatch '^[A-Za-z0-9][A-Za-z0-9._/-]*$') {
    throw "Ref '$Ref' is not a plain branch, tag, or commit name."
}

if (-not [string]::IsNullOrWhiteSpace($TemplateVersion) -and $TemplateVersion -notmatch "^$versionPattern$") {
    throw "TemplateVersion '$TemplateVersion' is not a semantic version."
}

$resolvedCommit = Invoke-Git $repositoryRootPath @('rev-parse', '--verify', '--quiet', "$Ref^{commit}") -AllowFailure
if ($resolvedCommit.ExitCode -ne 0 -or $resolvedCommit.Lines.Count -ne 1) {
    throw "Ref '$Ref' does not resolve to a commit. Run 'git fetch origin ncat-baseline --tags' first."
}

$commit = $resolvedCommit.Lines[0]
$release = Resolve-BaselineVersion $repositoryRootPath $commit $manifest.TagPrefix $TemplateVersion
if ([string]::IsNullOrWhiteSpace($TemplateVersion) -and $release.Version -ne $manifest.Version) {
    throw ("Generation manifest records NCAT $($manifest.Version), but $Ref is tagged for NCAT " +
        "$($release.Version). Update the manifest only when the adopted baseline changes.")
}
$templateReference = "$($manifest.TemplatePackage)@$($release.Version)"

$workDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "ncat-baseline-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$hive = Join-Path $workDirectory 'template-hive'
$scaffold = Join-Path $workDirectory 'scaffold'
$repeat = Join-Path $workDirectory 'scaffold-repeat'
[void] [System.IO.Directory]::CreateDirectory($hive)

Push-Location $repositoryRootPath
try {
    # The isolated hive keeps the caller's installed templates untouched and guarantees that only the pinned release
    # can satisfy the short name. Running from the repository root applies its global.json SDK and nuget.config source.
    Invoke-Dotnet @('new', 'install', $templateReference, '--debug:custom-hive', $hive)
    New-Scaffold $manifest $release.Version $hive $scaffold
    New-Scaffold $manifest $release.Version $hive $repeat
}
finally {
    Pop-Location
}

try {
    $nondeterministic = @(Compare-DirectoryBytes $scaffold $repeat)
    if ($nondeterministic.Count -gt 0) {
        $message = "NCAT $($release.Version) generated different output twice. Nothing is normalized, so these " +
            'files would make every comparison unreliable:'
        Write-Evidence (@($message) + (Format-PathList $nondeterministic))
        throw 'NCAT baseline generation is not deterministic.'
    }

    $result = Compare-BaselineToScaffold $repositoryRootPath $commit $scaffold $workDirectory
    $generatedCount = (Get-FileMap $scaffold).Count

    $evidence = @(
        'NCAT baseline reproducibility'
        "  Ref:               $Ref"
        "  Commit:            $commit"
        "  NCAT release:      $templateReference ($($release.Source))"
        "  Generation:        dotnet new $($manifest.TemplateShortName) $($manifest.Arguments -join ' ')"
        "  Generated files:   $generatedCount; two generations byte-identical"
        "  Line endings only: $($result.LineEndingsOnly.Count) file(s), normalized by the baseline's .gitattributes"
        "  Ignored:           $($result.Ignored.Count) generated file(s) excluded by the baseline's .gitignore"
        "  Material changes:  $($result.Material.Count)"
    )
    if ($result.Ignored.Count -gt 0) {
        $evidence += '  Ignored files:'
        $evidence += Format-PathList $result.Ignored
    }

    if ($result.Material.Count -gt 0) {
        $evidence += @('', 'Changes on the baseline that the declared release does not reproduce (- baseline, + regenerated):')
        $evidence += $result.Stat
        Write-Evidence $evidence
        $result.Diff | ForEach-Object { Write-Host $_ }
        throw "$Ref at $($commit.Substring(0, 7)) is not reproducible from $templateReference."
    }

    $evidence += "Result: $Ref at $($commit.Substring(0, 7)) is reproducible from $templateReference."
    Write-Evidence $evidence
}
finally {
    if ($KeepWorkDirectory) {
        Write-Host "Work directory kept: $workDirectory"
    }
    else {
        Remove-Item -LiteralPath $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
