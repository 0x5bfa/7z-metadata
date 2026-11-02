[CmdletBinding()]
param(
    [string] $SevenZipTag,
    [string] $PackageVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sevenZipRoot = Join-Path $repositoryRoot 'external\7zip'
$sourceHistoryPath = Join-Path $sevenZipRoot 'DOC\src-history.txt'
$metadataProjectPath = Join-Path $repositoryRoot 'generation\SevenZip\SevenZip.proj'
$projectPath = Join-Path $repositoryRoot 'src\SevenZip.Interop\SevenZip.Interop.csproj'
$winmdPath = Join-Path $repositoryRoot 'metadata\SevenZip.winmd'
$artifactRoot = Join-Path $repositoryRoot 'artifacts'
$nativeBuildRoot = Join-Path $artifactRoot 'native-build'
$packageInputRoot = Join-Path $artifactRoot 'package-input'
$packageOutputRoot = Join-Path $artifactRoot 'nupkg'
$packageLicensePath = Join-Path $packageInputRoot 'LICENSES\PackageLicenses.txt'

if (-not (Test-Path -LiteralPath $sourceHistoryPath)) {
    throw "7-Zip submodule is missing. Run 'git submodule update --init --recursive' first."
}

if ([string]::IsNullOrWhiteSpace($SevenZipTag)) {
    $SevenZipTag = (& git -C $sevenZipRoot describe --tags --exact-match HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($SevenZipTag)) {
        throw 'Could not determine the 7-Zip release tag. Pass -SevenZipTag explicitly.'
    }
}

$tagMatch = [regex]::Match($SevenZipTag, '^v?(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?$')
if (-not $tagMatch.Success) {
    throw "The 7-Zip tag '$SevenZipTag' is not a numeric release version."
}

$majorVersion = $tagMatch.Groups['major'].Value
$minorVersion = [int] $tagMatch.Groups['minor'].Value
$patchVersion = if ($tagMatch.Groups['patch'].Success) { [int] $tagMatch.Groups['patch'].Value } else { 0 }
if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    $PackageVersion = "$majorVersion.$minorVersion.$patchVersion"
}
$sourceCommit = (& git -C $sevenZipRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommit)) {
    throw 'Could not determine the checked-out 7-Zip commit.'
}

Write-Host "Generating WinMD from 7-Zip $SevenZipTag ($sourceCommit)..."
& dotnet build $metadataProjectPath --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw 'WinMD generation failed.'
}
if (-not (Test-Path -LiteralPath $winmdPath) -or (Get-Item -LiteralPath $winmdPath).Length -eq 0) {
    throw "WinMD generation did not produce '$winmdPath'."
}

$vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswherePath)) {
    throw 'vswhere.exe was not found. Install Visual Studio C++ build tools.'
}

$visualStudioRoot = (& $vswherePath -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($visualStudioRoot)) {
    throw 'Visual Studio C++ build tools were not found.'
}

$builds = @(
    [pscustomobject]@{ Platform = 'x86'; Rid = 'win-x86'; Vcvars = 'vcvarsamd64_x86.bat'; Machine = [uint16] 0x014c }
    [pscustomobject]@{ Platform = 'x64'; Rid = 'win-x64'; Vcvars = 'vcvars64.bat'; Machine = [uint16] 0x8664 }
    [pscustomobject]@{ Platform = 'arm64'; Rid = 'win-arm64'; Vcvars = 'vcvarsamd64_arm64.bat'; Machine = [uint16] 0xAA64 }
)

$bundleDirectory = Join-Path $sevenZipRoot 'CPP\7zip\Bundles\Format7zF'
foreach ($build in $builds) {
    $vcvarsPath = Join-Path $visualStudioRoot "VC\Auxiliary\Build\$($build.Vcvars)"
    if (-not (Test-Path -LiteralPath $vcvarsPath)) {
        throw "The Visual Studio environment script '$vcvarsPath' was not found."
    }

    $objectDirectory = Join-Path $nativeBuildRoot $build.Platform
    $nativeDirectory = Join-Path $packageInputRoot $build.Rid
    [System.IO.Directory]::CreateDirectory($objectDirectory) | Out-Null
    [System.IO.Directory]::CreateDirectory($nativeDirectory) | Out-Null

    $buildLogPath = Join-Path $nativeBuildRoot "$($build.Platform).log"
    $nmakeFlags = @()
    if ($build.Platform -eq 'arm64' -and -not (Get-Command clang-cl.exe -ErrorAction SilentlyContinue)) {
        $nmakeFlags += 'NO_ASM=1'
        Write-Host 'clang-cl was not found; building the ARM64 LZMA decoder without its optional assembly optimization.'
    }

    $nmakeCommand = 'call "{0}" >nul && nmake.exe /nologo PLATFORM={1} {2} "O={3}"' -f $vcvarsPath, $build.Platform, ($nmakeFlags -join ' '), $objectDirectory
    Push-Location $bundleDirectory
    try {
        & $env:ComSpec /d /c $nmakeCommand *> $buildLogPath
        $buildExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($buildExitCode -ne 0) {
        Get-Content -LiteralPath $buildLogPath -Tail 80 | Write-Host
        throw "Building 7z.dll for $($build.Rid) failed with exit code $buildExitCode."
    }

    $builtDllPath = Join-Path $objectDirectory '7z.dll'
    if (-not (Test-Path -LiteralPath $builtDllPath) -or (Get-Item -LiteralPath $builtDllPath).Length -eq 0) {
        throw "The $($build.Rid) build did not produce '$builtDllPath'."
    }

    $stream = [System.IO.File]::OpenRead($builtDllPath)
    $reader = $null
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "The $($build.Rid) output is not a valid PE image."
        }

        $machine = $reader.ReadUInt16()
        if ($machine -ne $build.Machine) {
            throw "The $($build.Rid) output has unexpected PE machine type 0x$($machine.ToString('X4'))."
        }
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        else {
            $stream.Dispose()
        }
    }

    $packagedDllPath = Join-Path $nativeDirectory '7z.dll'
    Copy-Item -LiteralPath $builtDllPath -Destination $packagedDllPath -Force
    Write-Host "Built $($build.Rid) 7z.dll ($((Get-Item -LiteralPath $packagedDllPath).Length) bytes)."
}

$licenseSources = @(
    [pscustomobject]@{ Title = 'This repository (MIT License)'; Path = (Join-Path $repositoryRoot 'LICENSE') }
    [pscustomobject]@{ Title = '7-Zip License Information'; Path = (Join-Path $sevenZipRoot 'DOC\License.txt') }
    [pscustomobject]@{ Title = 'GNU Lesser General Public License'; Path = (Join-Path $sevenZipRoot 'DOC\copying.txt') }
    [pscustomobject]@{ Title = 'unRAR License Restriction'; Path = (Join-Path $sevenZipRoot 'DOC\unRarLicense.txt') }
)

$licenseSections = [System.Collections.Generic.List[string]]::new()
foreach ($licenseSource in $licenseSources) {
    if (-not (Test-Path -LiteralPath $licenseSource.Path)) {
        throw "A license file required for packaging is missing: '$($licenseSource.Path)'."
    }

    $licenseText = [System.IO.File]::ReadAllText($licenseSource.Path).TrimEnd()
    $licenseSections.Add("$($licenseSource.Title)`r`n$('-' * $licenseSource.Title.Length)`r`n`r`n$licenseText")
}

$licenseContent = [string]::Join("`r`n`r`n", $licenseSections) + "`r`n"
[System.IO.Directory]::CreateDirectory((Split-Path -Parent $packageLicensePath)) | Out-Null
[System.IO.File]::WriteAllText($packageLicensePath, $licenseContent, [System.Text.UTF8Encoding]::new($false))

[System.IO.Directory]::CreateDirectory($packageOutputRoot) | Out-Null
$packArguments = @(
    'pack'
    $projectPath
    '--configuration'
    'Release'
    '--output'
    $packageOutputRoot
    "-p:PackageVersion=$packageVersion"
    "-p:RepositoryCommit=$sourceCommit"
    "-p:SevenZipTag=$SevenZipTag"
    "-p:SevenZipWinmdPath=$winmdPath"
    "-p:SevenZipNativeAssetRoot=$packageInputRoot"
    "-p:SevenZipPackageLicensePath=$packageLicensePath"
)
& dotnet @packArguments
if ($LASTEXITCODE -ne 0) {
    throw 'NuGet package creation failed.'
}

$packagePath = Join-Path $packageOutputRoot "SevenZip.Interop.$packageVersion.nupkg"
if (-not (Test-Path -LiteralPath $packagePath) -or (Get-Item -LiteralPath $packagePath).Length -eq 0) {
    throw "NuGet package creation did not produce '$packagePath'."
}

$requiredEntries = @(
    'lib/net8.0/SevenZip.Interop.dll'
    'metadata/SevenZip.winmd'
    'runtimes/win-x86/native/7z.dll'
    'runtimes/win-x64/native/7z.dll'
    'runtimes/win-arm64/native/7z.dll'
    'buildTransitive/SevenZip.Interop.props'
    'LICENSES/PackageLicenses.txt'
    'README.md'
)
$archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $entryNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        [void] $entryNames.Add($entry.FullName.Replace('\', '/'))
    }

    $missingEntries = @($requiredEntries | Where-Object { -not $entryNames.Contains($_) })
    if ($missingEntries.Count -gt 0) {
        throw "The package is missing required entries: $($missingEntries -join ', ')"
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Created $packagePath ($((Get-Item -LiteralPath $packagePath).Length) bytes) from 7-Zip $SevenZipTag."
