#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([string] $TemporaryRoot = [IO.Path]::GetTempPath())
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Packages.ps1')
. (Join-Path $PSScriptRoot '../../Packaging/Build-YLRegistryPackages.ps1')
$root = Join-Path (Get-YLTestPath $TemporaryRoot) ('yl-registry-sdk-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
$checks = 0
function Check([bool] $Value, [string] $Reason) { if (-not $Value) { throw $Reason }; $script:checks++ }
function Reject([scriptblock] $Action, [string] $Reason) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception -is [Management.Automation.CommandNotFoundException] -or $_.Exception.Message -notmatch $Reason) { throw }
        $script:checks++; return
    }
    throw "Expected refusal: $Reason"
}
function Save([string] $Path, [object] $Value) { [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 30), [Text.UTF8Encoding]::new($false)) }
try {
    $id = 'com.code-philosophy.hybridclr'; $revision = '6cadd24d101f4cbd00cd80190106f6621132d24d'
    $approval = Join-Path $root 'Dependencies.json'
    Save $approval @{ package = @{ name = $id; version = '9.0.3'; commit = $revision; bundled = $false; url = 'https://gitee.com/focus-creative-games/hybridclr_unity.git#v9.0.3'
        registry = @{ url = 'https://package.openupm.com'; metadataUrl = 'https://package.openupm.com/com.code-philosophy.hybridclr/9.0.3'
            repositoryUrl = 'git+https://github.com/focus-creative-games/hybridclr_unity.git'; repositoryRevision = $revision; nativeValidated = $false } } }
    $contents = [ordered]@{ 'package.json' = '{"name":"com.code-philosophy.hybridclr","version":"9.0.3"}'; 'LICENSE' = "MIT License`nPermission is hereby granted, free of charge"; 'Editor/Fixture.cs' = '// fixture official SDK source, not Unity acceptance' }
    $sdkFixtureArchive = Join-Path $root 'sdk.tgz'
    $file = [IO.File]::Create($sdkFixtureArchive); $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionLevel]::Fastest)
    $writer = [System.Formats.Tar.TarWriter]::new($gzip)
    try {
        foreach ($entry in $contents.GetEnumerator()) {
            $tarEntry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, 'package/' + $entry.Key)
            $stream = [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($entry.Value))
            try { $tarEntry.DataStream = $stream; $writer.WriteEntry($tarEntry) } finally { $stream.Dispose() }
        }
    } finally { $writer.Dispose(); $gzip.Dispose(); $file.Dispose() }
    $integrity = 'sha512-' + [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData([IO.File]::ReadAllBytes($sdkFixtureArchive)))
    $metadata = @{ name = $id; version = '9.0.3'; repository = @{ type = 'git'; url = 'git+https://github.com/focus-creative-games/hybridclr_unity.git'; revision = $revision }
        dist = @{ integrity = $integrity; tarball = "https://package.openupm.com/$id/-/$id-9.0.3.tgz" } }
    $metadataFile = Join-Path $root 'metadata.json'; Save $metadataFile $metadata
    $script:sdkRedirectTarget = "https://download.openupm.com/verdaccio/$id/$id-9.0.3.tgz"
    function Invoke-WebRequest([string] $Uri, [string] $OutFile, [int] $TimeoutSec, [int] $MaximumRedirection, [string] $ErrorAction, [switch] $PassThru) {
        if ($Uri -ceq "https://package.openupm.com/$id/9.0.3") { [IO.File]::Copy($metadataFile, $OutFile, $false) }
        elseif ($Uri -ceq "https://package.openupm.com/$id/-/$id-9.0.3.tgz") {
            $response = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::Found); $response.Headers.Location = [uri]$script:sdkRedirectTarget
            throw [Microsoft.PowerShell.Commands.HttpResponseException]::new('Official CDN redirect', $response)
        }
        elseif ($Uri -ceq "https://download.openupm.com/verdaccio/$id/$id-9.0.3.tgz") { [IO.File]::Copy($sdkFixtureArchive, $OutFile, $false) }
        else { throw "Unexpected network input: $Uri" }
        if ($PassThru) { return @{ StatusCode = 200 } }
    }
    $sdk = Get-YLHybridClrRegistrySdk -Approval $approval -Output (Join-Path $root 'Verified') -RegistryUrl 'https://package.openupm.com'
    Check ($sdk.source -ceq 'Registry' -and $sdk.version -ceq '9.0.3' -and $sdk.repositoryRevision -ceq $revision -and $sdk.dependencies.Count -eq 0) 'SDK identity must distinguish registry metadata revision from an actual Git checkout.'
    Check ($sdk.evidence.integrity -ceq $integrity -and $sdk.files.Count -eq 3) 'SDK bytes/license were not verified against Registry integrity.'
    Check ($sdk.evidence.download.finalUrl -ceq $script:sdkRedirectTarget -and $sdk.evidence.download.redirects.Count -eq 1) 'Official CDN redirect facts were not retained.'
    Assert-YLHybridClrRegistryInstalled -Sdk $sdk -Root $sdk.root; $checks++
    $json = Join-Path $sdk.root 'package.json'; $installed = Read-YLPackageEvidenceJson $json
    $installed['_fingerprint'] = 'a' * 40; Save $json $installed
    Assert-YLHybridClrRegistryInstalled -Sdk $sdk -Root $sdk.root; $checks++
    $installed['_unknown'] = 'do not ignore'; Save $json $installed
    Reject { Assert-YLHybridClrRegistryInstalled -Sdk $sdk -Root $sdk.root } 'content|metadata|manifest|payload'
    [IO.File]::WriteAllText($json, $contents['package.json'])
    [IO.File]::AppendAllText((Join-Path $sdk.root 'LICENSE'), 'changed')
    Reject { Assert-YLHybridClrRegistryInstalled -Sdk $sdk -Root $sdk.root } 'content|file|payload'
    foreach ($fault in @('revision', 'repository', 'version', 'integrity', 'url', 'boolean-revision', 'boolean-repository', 'boolean-name')) {
        $bad = $metadata | ConvertTo-Json -Depth 20 | ConvertFrom-Json -AsHashtable
        switch ($fault) {
            revision { $bad.repository.revision = 'b' * 40 }
            repository { $bad.repository.url = 'git+https://example.invalid/sdk.git' }
            version { $bad.version = '9.0.2' }
            integrity { $bad.dist.integrity = 'sha512-' + [Convert]::ToBase64String([byte[]]::new(64)) }
            url { $bad.dist.tarball = 'https://example.invalid/sdk.tgz' }
            'boolean-revision' { $bad.repository.revision = $true }
            'boolean-repository' { $bad.repository.url = $true }
            'boolean-name' { $bad.name = $true }
        }
        Save $metadataFile $bad
        Reject { Get-YLHybridClrRegistrySdk -Approval $approval -Output (Join-Path $root $fault) -RegistryUrl 'https://package.openupm.com' } 'SDK|sdk|Registry|registry|integrity|source|version'
    }
    Save $metadataFile $metadata
    $script:sdkRedirectTarget = 'https://other.invalid/sdk.tgz'
    Reject { Get-YLHybridClrRegistrySdk -Approval $approval -Output (Join-Path $root 'foreign-cdn') -RegistryUrl 'https://package.openupm.com' } 'redirect|host|HTTPS'
    $badApproval = Read-YLPackageEvidenceJson $approval; $badApproval.package.registry.nativeValidated = $true; Save $approval $badApproval
    Reject { Get-YLHybridClrRegistrySdk -Approval $approval -Output (Join-Path $root 'approval') -RegistryUrl 'https://package.openupm.com' } 'approval|native'
    "PASS: $checks NoUnity HybridCLR Registry metadata, license, tarball and installed payload contracts. NOT native or hot-update Player verification."
} finally {
    if ([IO.Directory]::Exists($root)) { $null = @(Get-YLTestTreeFiles $root); [IO.Directory]::Delete($root, $true) }
}
