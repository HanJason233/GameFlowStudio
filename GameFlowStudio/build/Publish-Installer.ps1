param(
    [string]$Version,
    [switch]$TestSign,
    [switch]$TrustTestCertificate
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $repositoryRoot 'artifacts'
$editorDirectory = Join-Path $repositoryRoot 'src\GameFlowStudio.Editor'
$desktopProject = Join-Path $repositoryRoot 'src\GameFlowStudio.Desktop\GameFlowStudio.Desktop.csproj'
$installerProject = Join-Path $repositoryRoot 'installer\GameFlowStudio.Installer.wixproj'
$publishDirectory = Join-Path $artifactRoot 'publish\win-x64'
$installerDirectory = Join-Path $artifactRoot 'installer'

function Reset-ArtifactDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (!$resolvedPath.StartsWith($resolvedArtifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the artifacts directory: $resolvedPath"
    }

    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolvedPath -Force | Out-Null
}

function Get-TestSigningCertificate {
    $subject = 'CN=GameFlow Studio Test Publisher'
    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object {
            $_.Subject -eq $subject -and
            $_.HasPrivateKey -and
            $_.NotAfter -gt (Get-Date).AddDays(30)
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if ($null -eq $certificate) {
        $certificate = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject $subject `
            -FriendlyName 'GameFlow Studio Local Test Signing' `
            -CertStoreLocation Cert:\CurrentUser\My `
            -KeyAlgorithm RSA `
            -KeyLength 3072 `
            -HashAlgorithm SHA256 `
            -NotAfter (Get-Date).AddYears(2)
    }

    return $certificate
}

function Sign-TestFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Certificate
    )

    $signature = Set-AuthenticodeSignature `
        -FilePath $Path `
        -Certificate $Certificate `
        -HashAlgorithm SHA256
    if ($null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $Certificate.Thumbprint) {
        throw "Test signing failed: $Path"
    }
    if ($TrustTestCertificate -and $signature.Status -ne 'Valid') {
        throw "The trusted test signature is not valid: $Path ($($signature.Status))"
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (& dotnet msbuild $desktopProject -nologo -getProperty:Version).Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "The installer version must use major.minor.patch format: $Version"
}
if ($TrustTestCertificate -and !$TestSign) {
    throw 'TrustTestCertificate requires TestSign.'
}

Reset-ArtifactDirectory $publishDirectory
Reset-ArtifactDirectory $installerDirectory

Push-Location $editorDirectory
try {
    & npm.cmd ci --prefer-offline --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
    & npm.cmd run lint
    if ($LASTEXITCODE -ne 0) { throw 'Frontend lint failed.' }
    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
}
finally {
    Pop-Location
}

& dotnet test (Join-Path $repositoryRoot 'GameFlowStudio.sln') -c Release
if ($LASTEXITCODE -ne 0) { throw '.NET tests failed.' }

& dotnet publish $desktopProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -p:PublishReadyToRun=true `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw '.NET publish failed.' }

$testCertificate = $null
if ($TestSign) {
    $testCertificate = Get-TestSigningCertificate
    $certificatePath = Join-Path $installerDirectory 'GameFlowStudio-TestPublisher.cer'
    Export-Certificate -Cert $testCertificate -FilePath $certificatePath -Force | Out-Null
    if ($TrustTestCertificate) {
        Import-Certificate -FilePath $certificatePath -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
        Import-Certificate -FilePath $certificatePath -CertStoreLocation Cert:\CurrentUser\TrustedPublisher | Out-Null
    }

    Sign-TestFile `
        -Path (Join-Path $publishDirectory 'GameFlowStudio.Desktop.exe') `
        -Certificate $testCertificate
}

& dotnet build $installerProject `
    -c Release `
    -p:AppVersion=$Version `
    -p:PublishDir=$publishDirectory `
    -p:OutputPath=$installerDirectory
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }

$installer = Get-ChildItem -LiteralPath $installerDirectory -Filter '*.msi' -File |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $installer) { throw 'MSI build completed without producing an installer.' }

if ($TestSign) {
    Sign-TestFile -Path $installer.FullName -Certificate $testCertificate
}

$hash = Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256
$checksumPath = "$($installer.FullName).sha256"
"$($hash.Hash.ToLowerInvariant())  $($installer.Name)" |
    Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Output "Installer: $($installer.FullName)"
Write-Output "SHA256:    $($hash.Hash.ToLowerInvariant())"
