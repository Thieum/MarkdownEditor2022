<#
.SYNOPSIS
    Updates the bundled MIT PlantUML JavaScript engine and full offline resources.
.DESCRIPTION
    Requires PowerShell 5.1+, tar.exe, and internet access (no Java or npm).
    Downloads a pinned stable @plantuml/core package and matching GitHub release.
    Includes the standard-library source archive with its original license notices.
    Stages and validates the complete bundle before replacing the installed copy.
.EXAMPLE
    .\scripts\download-latest-plantumljs.ps1
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Margin\plantuml')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Sha256([string] $Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-PackageIntegrity([string] $Path, [string] $Integrity) {
    if ($Integrity -notmatch '^sha512-(.+)$') { throw 'Expected SHA-512 npm package integrity.' }
    $expected = $Matches[1]
    $hash = [Security.Cryptography.SHA512]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { $actual = [Convert]::ToBase64String($hash.ComputeHash($stream)) }
    finally { $stream.Dispose(); $hash.Dispose() }
    if ($actual -ne $expected) { throw 'PlantUML package integrity check failed.' }
}

function Install-Bundle([string] $Staged, [string] $Target) {
    $backup = "$Target.backup-$([Guid]::NewGuid().ToString('N'))"
    $hadPrevious = Test-Path -LiteralPath $Target
    if ($hadPrevious) { Move-Item -LiteralPath $Target -Destination $backup }
    try { Move-Item -LiteralPath $Staged -Destination $Target }
    catch {
        if ($hadPrevious) { Move-Item -LiteralPath $backup -Destination $Target }
        throw
    }
    if ($hadPrevious) { Remove-Item -LiteralPath $backup -Recurse -Force }
}

function Update-PlantUml {
    $target = [IO.Path]::GetFullPath($Destination)
    if ([IO.Path]::GetFileName($target.TrimEnd('\')) -ne 'plantuml') {
        throw 'The destination must be a dedicated directory named plantuml.'
    }
    $parent = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $parent)) { throw "Missing destination parent: $parent" }
    if (Test-Path -LiteralPath $target) {
        if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'The PlantUML destination must not be a symbolic link or junction.'
        }
        if (@(Get-ChildItem -LiteralPath $target -Force).Count -gt 0 -and
            (-not (Test-Path -LiteralPath (Join-Path $target 'version.json')) -or
             -not (Test-Path -LiteralPath (Join-Path $target 'plantuml.js')))) {
            throw 'Refusing to replace a non-empty directory that is not a versioned PlantUML bundle.'
        }
    }
    $stage = Join-Path $parent ('.plantuml-update-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    try {
        $tag = if ($Version) { $Version } else { 'latest' }
        $package = Invoke-RestMethod "https://registry.npmjs.org/@plantuml/core/$tag" -UseBasicParsing
        $resolvedVersion = $package.version
        if ($resolvedVersion -notmatch '^\d+\.\d+\.\d+$' -or
            [version]$resolvedVersion -lt [version]'1.2026.6' -or $package.license -ne 'MIT') {
            throw 'Only stable MIT-licensed PlantUML releases 1.2026.6 or later are supported.'
        }
        $release = Invoke-RestMethod "https://api.github.com/repos/plantuml/plantuml/releases/tags/v$resolvedVersion" -UseBasicParsing
        $asset = @($release.assets | Where-Object name -EQ "js-plantuml-$resolvedVersion.zip")
        if ($asset.Count -ne 1) { throw "Missing matching JavaScript release for $resolvedVersion." }

        Write-Host "Downloading PlantUML $resolvedVersion and offline libraries..."
        $tgz = Join-Path $stage 'core.tgz'
        Invoke-WebRequest $package.dist.tarball -OutFile $tgz -UseBasicParsing
        Test-PackageIntegrity $tgz $package.dist.integrity
        & tar -xzf $tgz -C $stage
        if ($LASTEXITCODE -ne 0) { throw 'Could not extract the PlantUML npm package.' }
        $zip = Join-Path $stage 'release.zip'
        Invoke-WebRequest $asset[0].browser_download_url -OutFile $zip -UseBasicParsing
        Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $stage 'release')
        $bundle = Join-Path $stage 'bundle'
        New-Item -ItemType Directory -Path $bundle | Out-Null
        foreach ($name in @('plantuml.js', 'viz-global.js', 'themes.js', 'emoji.js', 'openiconic.js', 'LICENSE')) {
            $source = Join-Path $stage "package\$name"
            if (-not (Test-Path -LiteralPath $source) -or (Get-Item -LiteralPath $source).Length -eq 0) {
                throw "Missing required MIT package asset: $name"
            }
            Copy-Item -LiteralPath $source -Destination (Join-Path $bundle $name)
        }
        $engine = [IO.File]::ReadAllText((Join-Path $bundle 'plantuml.js'))
        if ($engine -notmatch 'as renderToString' -or $engine -notmatch 'PLANTUML_STDLIB_BASE') {
            throw 'The published PlantUML API changed; review the adapter before updating.'
        }
        $libraries = @(Get-ChildItem (Join-Path $stage 'release') -Filter '*.min.js' -File)
        if ($libraries.Count -eq 0 -or -not ($libraries.Name -contains 'c4.min.js')) {
            throw 'The matching release does not contain the expected standard libraries.'
        }
        $libraries | Copy-Item -Destination $bundle
        Invoke-WebRequest 'https://unpkg.com/open-iconic@1.1.1/ICON-LICENSE' `
            -OutFile (Join-Path $bundle 'openiconic-LICENSE.txt') -UseBasicParsing
        Invoke-WebRequest 'https://raw.githubusercontent.com/twitter/twemoji/v14.0.2/LICENSE-GRAPHICS' `
            -OutFile (Join-Path $bundle 'twemoji-LICENSE.txt') -UseBasicParsing

        $vizText = [IO.File]::ReadAllText((Join-Path $bundle 'viz-global.js'))
        if ($vizText -notmatch 'Viz\.js (\d+\.\d+\.\d+)') { throw 'Could not identify the bundled Viz.js release.' }
        $vizVersion = $Matches[1]
        $viz = Invoke-RestMethod "https://registry.npmjs.org/@viz-js/viz/$vizVersion" -UseBasicParsing
        Invoke-WebRequest "https://raw.githubusercontent.com/mdaines/viz-js/$($viz.gitHead)/LICENSE" `
            -OutFile (Join-Path $bundle 'viz-LICENSE.txt') -UseBasicParsing
        $provenance = Invoke-RestMethod "https://unpkg.com/@viz-js/viz@$vizVersion/lib/provenance.json" -UseBasicParsing
        foreach ($dependency in @('graphviz', 'expat')) {
            $materials = @($provenance.predicate.materials | Where-Object { $_.uri -match "$dependency-[0-9.]+\.tar\.gz$" })
            if ($materials.Count -ne 1) { throw "Missing $dependency source provenance in Viz.js." }
            $sourceFile = Join-Path $bundle "$dependency-sources.tar.gz"
            Invoke-WebRequest $materials[0].uri -OutFile $sourceFile -UseBasicParsing
            if ((Get-Sha256 $sourceFile) -ne $materials[0].digest.sha256) {
                throw "Source integrity check failed for $dependency."
            }
        }

        # Ship original library sources and notices, not just generated sprite bundles.
        $until = [Uri]::EscapeDataString($release.published_at)
        $commits = @(Invoke-RestMethod "https://api.github.com/repos/plantuml/plantuml-stdlib/commits?until=$until&per_page=1" -UseBasicParsing)
        if ($commits.Count -ne 1) { throw 'Could not pin the standard-library source snapshot.' }
        $stdlibCommit = $commits[0].sha
        $sourceUrl = "https://github.com/plantuml/plantuml-stdlib/archive/$stdlibCommit.zip"
        Invoke-WebRequest $sourceUrl -OutFile (Join-Path $bundle 'stdlib-sources.zip') -UseBasicParsing
        $notice = @"
PlantUML JavaScript engine: @plantuml/core $resolvedVersion, MIT (see LICENSE).
Viz.js: see viz-LICENSE.txt. Graphviz/Expat original sources and their license
notices are supplied in graphviz-sources.tar.gz and expat-sources.tar.gz.
Themes, emoji and OpenIconic: see the engine LICENSE and original resource headers.
OpenIconic graphics: Copyright (c) 2014 Waybury, MIT (openiconic-LICENSE.txt).
Twemoji graphics: Copyright Twitter, Inc. and other contributors, CC-BY-4.0
(twemoji-LICENSE.txt; https://github.com/twitter/twemoji and
https://creativecommons.org/licenses/by/4.0/). PlantUML converts these graphics
into its bundled sprite representation.
Standard libraries: separately licensed; their original metadata is retained in each
*.min.js bundle. Original sources, copyright and license notices are supplied in
stdlib-sources.zip (plantuml/plantuml-stdlib commit $stdlibCommit).
The engine MIT license does not relicense those libraries or their icons.
Release resources: $($asset[0].browser_download_url)
Standard-library sources: $sourceUrl
"@
        [IO.File]::WriteAllText((Join-Path $bundle 'NOTICE.txt'), $notice)
        $files = @(Get-ChildItem -LiteralPath $bundle -File | Sort-Object Name | ForEach-Object {
            [ordered]@{ name = $_.Name; sha256 = Get-Sha256 $_.FullName; size = $_.Length }
        })
        $manifest = [ordered]@{
            version = $resolvedVersion
            package = $package.dist.tarball
            integrity = $package.dist.integrity
            release = $asset[0].browser_download_url
            releaseSha256 = Get-Sha256 $zip
            stdlibCommit = $stdlibCommit
            viz = [ordered]@{
                version = $vizVersion
                commit = $viz.gitHead
                sources = @($provenance.predicate.materials | Where-Object { $_.uri -match '(graphviz|expat)-[0-9.]+\.tar\.gz$' })
            }
            files = $files
        }
        [IO.File]::WriteAllText((Join-Path $bundle 'version.json'), ($manifest | ConvertTo-Json -Depth 5))
        Install-Bundle $bundle $target
        Write-Host "PlantUML $resolvedVersion installed in $target"
    }
    finally { Remove-Item -LiteralPath $stage -Recurse -Force }
}

if ($MyInvocation.InvocationName -ne '.') { Update-PlantUml }
