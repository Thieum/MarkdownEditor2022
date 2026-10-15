$ErrorActionPreference = 'Stop'
$updater = Join-Path $PSScriptRoot '..\scripts\download-latest-plantumljs.ps1'
. (Resolve-Path -LiteralPath $updater).Path

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Assert-Throws([scriptblock] $Action, [string] $Message, [string] $ExpectedMessage = '') {
    $threw = $false
    try { & $Action }
    catch {
        $threw = $true
        if ($ExpectedMessage) {
            Assert-True ($_.Exception.Message -ceq $ExpectedMessage) "Expected exception message: $ExpectedMessage"
        }
    }
    Assert-True $threw $Message
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('MarkdownEditor2022PlantUmlUpdater-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null

try {
    $packagePath = Join-Path $fixtureRoot 'fixture-package.tgz'
    [IO.File]::WriteAllBytes($packagePath, [byte[]](0..127))
    $sha512 = [Security.Cryptography.SHA512]::Create()
    try {
        $validDigest = [Convert]::ToBase64String($sha512.ComputeHash([IO.File]::ReadAllBytes($packagePath)))
    }
    finally {
        $sha512.Dispose()
    }

    Test-PackageIntegrity -Path $packagePath -Integrity "sha512-$validDigest"
    Write-Output 'PASS Test_PackageIntegrity_ValidSha512Accepted'
    Assert-Throws {
        Test-PackageIntegrity -Path $packagePath -Integrity "sha256-$validDigest"
    } 'A non-SHA-512 integrity algorithm must be rejected.' 'Expected SHA-512 npm package integrity.'
    Write-Output 'PASS Test_PackageIntegrity_NonSha512Rejected'
    Assert-Throws {
        Test-PackageIntegrity -Path $packagePath -Integrity ('sha512-' + [Convert]::ToBase64String([byte[]](1..64)))
    } 'A mismatched SHA-512 digest must be rejected.' 'PlantUML package integrity check failed.'
    Write-Output 'PASS Test_PackageIntegrity_MismatchedSha512Rejected'

    $target = Join-Path $fixtureRoot 'installed-bundle'
    $missingStage = Join-Path $fixtureRoot 'missing-staged-bundle'
    New-Item -ItemType Directory -Path $target | Out-Null
    $marker = Join-Path $target 'previous-version.txt'
    [IO.File]::WriteAllText($marker, 'keep previous bundle')

    Assert-Throws {
        Install-Bundle -Staged $missingStage -Target $target
    } 'An unavailable staged bundle must cause installation to fail.'
    Assert-True (Test-Path -LiteralPath $target -PathType Container) 'The previous target folder must be restored.'
    Assert-True ([IO.File]::ReadAllText($marker) -ceq 'keep previous bundle') 'Rollback must preserve the prior target contents.'
    Assert-True (@(Get-ChildItem -LiteralPath $fixtureRoot -Filter 'installed-bundle.backup-*').Count -eq 0) 'Rollback must not leave a backup folder behind.'
    Write-Output 'PASS Install_Bundle_FailedMoveRestoresPreviousContents'

    $validStage = Join-Path $fixtureRoot 'valid-staged-bundle'
    New-Item -ItemType Directory -Path $validStage | Out-Null
    [IO.File]::WriteAllText((Join-Path $validStage 'new-version.txt'), 'install replacement bundle')
    Install-Bundle -Staged $validStage -Target $target
    Assert-True ([IO.File]::ReadAllText((Join-Path $target 'new-version.txt')) -ceq 'install replacement bundle') 'Successful replacement must install staged contents.'
    Assert-True (-not (Test-Path -LiteralPath $marker)) 'Previous bundle contents must not leak into replacement.'
    Assert-True (-not (Test-Path -LiteralPath $validStage)) 'Successful installation must move the staged bundle.'
    Assert-True (@(Get-ChildItem -LiteralPath $fixtureRoot -Filter 'installed-bundle.backup-*').Count -eq 0) 'Successful replacement must remove its backup.'
    Write-Output 'PASS Install_Bundle_SuccessReplacesPreviousContents'

    $firstStage = Join-Path $fixtureRoot 'first-staged-bundle'
    $firstTarget = Join-Path $fixtureRoot 'first-installed-bundle'
    New-Item -ItemType Directory -Path $firstStage | Out-Null
    [IO.File]::WriteAllText((Join-Path $firstStage 'first-version.txt'), 'first bundle')
    Install-Bundle -Staged $firstStage -Target $firstTarget
    Assert-True ([IO.File]::ReadAllText((Join-Path $firstTarget 'first-version.txt')) -ceq 'first bundle') 'First installation must preserve staged contents.'
    Assert-True (-not (Test-Path -LiteralPath $firstStage)) 'First installation must move the stage.'
    Write-Output 'PASS Install_Bundle_FirstInstallMovesStagedContents'
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
