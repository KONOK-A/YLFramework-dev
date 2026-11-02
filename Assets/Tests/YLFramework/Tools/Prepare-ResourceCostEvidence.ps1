#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $OwnedRoot,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $Repository,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $Scope,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $Purpose
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This fixture supports Windows Editor only.' }
if ([string]::IsNullOrWhiteSpace($Scope) -or [string]::IsNullOrWhiteSpace($Purpose)) {
    throw 'Explicit scope and purpose are required; historical identities are never inferred.'
}
if (-not [IO.Path]::IsPathFullyQualified($OwnedRoot) -or -not [IO.Path]::IsPathFullyQualified($Repository)) {
    throw 'OwnedRoot and Repository must be absolute filesystem paths.'
}
$repository = [IO.Path]::GetFullPath($Repository).TrimEnd('\','/')
if (-not (Test-Path -LiteralPath $repository -PathType Container)) { throw 'Repository must already exist.' }
$sourceRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..')).TrimEnd('\','/')
$root = [IO.Path]::GetFullPath($OwnedRoot).TrimEnd('\','/')
foreach ($boundary in @($sourceRepository, $repository)) {
    if ($root.Equals($boundary,[StringComparison]::OrdinalIgnoreCase) -or
        $root.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Evidence and non-production private credentials must stay outside the repository.'
    }
}
$parent = [IO.Path]::GetDirectoryName($root)
if (-not (Test-Path -LiteralPath $parent -PathType Container) -or (Test-Path -LiteralPath $root)) {
    throw 'Choose a new owned directory under an existing external parent. Existing evidence is never replaced.'
}
for ($cursor = [IO.DirectoryInfo]::new($parent); $null -ne $cursor; $cursor = $cursor.Parent) {
    if ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked parent refused.' }
}
$null = [IO.Directory]::CreateDirectory($root)
$owner = [ordered]@{
    scope=$Scope; purpose=$Purpose; repository=$repository; ownedRoot=$root
    createdAtUtc=[DateTime]::UtcNow.ToString('O'); evidenceOnly=$true; noGitWrites=$true
}
[IO.File]::WriteAllText((Join-Path $root 'owner.json'),($owner | ConvertTo-Json -Depth 3),[Text.UTF8Encoding]::new($false))
$rsa = [Security.Cryptography.RSA]::Create(2048)
try {
    if ($rsa.KeySize -lt 2048) { throw 'Insufficient RSA size.' }
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost',$rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddDnsName('localhost'); $san.AddIpAddress([Net.IPAddress]::Loopback)
    $request.CertificateExtensions.Add($san.Build())
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5),[DateTimeOffset]::UtcNow.AddDays(2))
    try {
        # Public, non-production fixture password only; no application/content/signing key is accepted by this script.
        $bytes = $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx,'b02f03-loopback-only')
        try { [IO.File]::WriteAllBytes((Join-Path $root 'loopback.pfx'),$bytes) } finally { [Array]::Clear($bytes,0,$bytes.Length) }
        [pscustomobject]@{ OwnedRoot=$root; Repository=$repository; Scope=$Scope; Purpose=$Purpose;
            NonProduction=$true; SystemTrustChanged=$false;
            CertificateSha256=$certificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256) }
    } finally { $certificate.Dispose() }
} finally { $rsa.Dispose() }
