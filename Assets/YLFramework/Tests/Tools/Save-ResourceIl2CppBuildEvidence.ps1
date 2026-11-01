#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EvidenceRoot,
    [Parameter(Mandatory)][string] $BuildName,
    [ValidateSet('','2022.3.62f3c1','6000.3.27f1')][string] $ExpectedUnityVersion = ''
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($EvidenceRoot)
if ($BuildName -notmatch '^[A-Za-z0-9-]+$') { throw 'An explicit simple build name is required.' }
$carrier = (Get-Content -LiteralPath (Join-Path $root 'carrier-location.json') -Raw | ConvertFrom-Json).Carrier
$owner = Get-Content -LiteralPath (Join-Path $carrier 'r810-owner.json') -Raw | ConvertFrom-Json
if ($owner.Purpose -cne 'Resource-8.10-owned-carrier' -or $owner.Evidence -cne $root -or $owner.Carrier -cne $carrier) { throw 'Owned carrier mismatch.' }
if (@(Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" | Where-Object { $_.CommandLine -like ('*' + $carrier + '*') }).Count) { throw 'Wait for this exact carrier build to finish before archiving.' }
$build = Join-Path $carrier ('Builds/' + $BuildName)
$report = Get-Content -LiteralPath (Join-Path $build 'BuildReport.json') -Raw | ConvertFrom-Json
$ownerUnity = if ($owner.PSObject.Properties['UnityVersion']) { [string]$owner.UnityVersion } else { '2022.3.62f3c1' }
if ($ExpectedUnityVersion -and $ExpectedUnityVersion -cne $ownerUnity) { throw 'Expected Unity version differs from the carrier owner.' }
if ($report.Result -cne 'Succeeded' -or $report.Errors -ne 0 -or $report.Backend -cne 'IL2CPP' -or
    $report.Unity -cne $ownerUnity -or $report.ManagedStripping -cne 'Low' -or $report.CppConfiguration -cne 'Release') {
    throw 'A successful actual IL2CPP Low/Release report matching the carrier Unity version is required.'
}
$snapshotPath = Join-Path $root ('build-' + $BuildName + '-source.json')
function Hash([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
if ((Hash $snapshotPath) -cne $report.SourceIdentitySha256) { throw 'The recorded build source identity differs.' }
$managed = Join-Path $carrier 'Library/Bee/artifacts/WinPlayerBuildProgram/ManagedStripped'
$cpp = Join-Path $carrier 'Library/Bee/artifacts/WinPlayerBuildProgram/il2cppOutput/cpp'
if (-not (Test-Path -LiteralPath $managed -PathType Container) -or
    -not (Test-Path -LiteralPath (Join-Path $managed 'YLFramework.Runtime.dll') -PathType Leaf)) { throw 'Actual UnityLinker ManagedStripped Runtime output is missing.' }
if (-not (Test-Path -LiteralPath $cpp -PathType Container) -or
    @(Get-ChildItem -LiteralPath $cpp -File -Recurse).Count -eq 0) { throw 'Actual IL2CPP generated C++ output is missing.' }
foreach ($path in @('GameAssembly.dll','ResourceAcceptance.exe','UnityPlayer.dll','ResourceAcceptance_Data/il2cpp_data/Metadata/global-metadata.dat')) {
    if (-not (Test-Path -LiteralPath (Join-Path $build $path) -PathType Leaf)) { throw "Actual IL2CPP artifact missing before archiving: $path" }
}
$editorLog = Join-Path $root ('Build-' + $BuildName + '.log')
if (-not (Test-Path -LiteralPath $editorLog -PathType Leaf)) { throw 'The actual build log is missing before archiving.' }
$snapshot = Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json
foreach ($file in $snapshot) {
    if ((Hash (Join-Path $carrier $file.Path)) -cne $file.Sha256) { throw "Archive before synchronizing different source: $($file.Path)" }
}
$destination = Join-Path $root ('BuildEvidence/' + $BuildName)
if (Test-Path -LiteralPath $destination) { throw 'Never overwrite archived build evidence.' }
$null = [IO.Directory]::CreateDirectory($destination)
foreach ($file in $snapshot) {
    $source = Join-Path $carrier $file.Path
    $target = Join-Path $destination ('Sources/' + $file.Path)
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    [IO.File]::Copy($source,$target)
    if ((Hash $target) -cne $file.Sha256) { throw 'Source archive hash mismatch.' }
}
$null = [IO.Directory]::CreateDirectory((Join-Path $destination 'ManagedStripped'))
$assemblies = @(foreach ($file in Get-ChildItem -LiteralPath $managed -File) {
    [IO.File]::Copy($file.FullName,(Join-Path $destination ('ManagedStripped/' + $file.Name)))
    $references = @()
    if ($file.Extension -eq '.dll') {
        $input = [IO.File]::OpenRead($file.FullName)
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($input)
        try {
            $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $references = @($metadata.AssemblyReferences | ForEach-Object { $metadata.GetString($metadata.GetAssemblyReference($_).Name) })
        } finally { $pe.Dispose(); $input.Dispose() }
    }
    @{ Name=$file.Name; Length=$file.Length; Sha256=(Hash $file.FullName); References=$references }
})
$cppFiles = @(foreach ($file in Get-ChildItem -LiteralPath $cpp -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($cpp,$file.FullName)
    $target = Join-Path $destination ('GeneratedCpp/' + $relative)
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    [IO.File]::Copy($file.FullName,$target)
    @{ Path=$relative; Length=$file.Length; Sha256=(Hash $target) }
})
foreach ($file in @('BuildReport.json')) { [IO.File]::Copy((Join-Path $build $file),(Join-Path $destination $file)) }
[IO.File]::Copy($snapshotPath,(Join-Path $destination 'source-identity.json'))
[IO.File]::Copy((Join-Path $root ('Build-' + $BuildName + '.log')),(Join-Path $destination 'Editor.log'))
$nativeReport = Join-Path $carrier 'Library/LastBuild.buildreport'
if (Test-Path -LiteralPath $nativeReport) { [IO.File]::Copy($nativeReport,(Join-Path $destination 'LastBuild.buildreport')) }
$native = @(foreach ($path in @('GameAssembly.dll','ResourceAcceptance.exe','UnityPlayer.dll','ResourceAcceptance_Data/il2cpp_data/Metadata/global-metadata.dat')) {
    $file = Join-Path $build $path
    @{ Path=$path; Length=([IO.FileInfo]$file).Length; Sha256=(Hash $file) }
})
$runtime = @($assemblies | Where-Object Name -CEQ 'YLFramework.Runtime.dll')
if ($runtime.Count -ne 1 -or @($runtime[0].References | Where-Object {
    $_ -cnotmatch '^(?:mscorlib|netstandard|System(?:\.[A-Za-z0-9_]+)*|UnityEngine(?:\.[A-Za-z0-9_]+)*|Unity\.ugui|Microsoft\.CSharp|Microsoft\.Win32\.Primitives|Newtonsoft\.Json|BouncyCastle\.Cryptography)$' -or $_ -match 'TestRunner|Tests|Editor|Analyzer'
}).Count) { throw 'Actual stripped Runtime assembly dependency boundary failed.' }
$receipt = @{ Build=$BuildName; Backend=$report.Backend; Unity=$report.Unity; ManagedStripping=$report.ManagedStripping; CppConfiguration=$report.CppConfiguration;
    SourceIdentitySha256=$report.SourceIdentitySha256; FormalSourceFiles=@($snapshot).Count; NativeArtifacts=$native; StrippedFiles=$assemblies; StrippedFileCount=$assemblies.Count; StrippedAssemblyCount=@($assemblies | Where-Object Name -Like '*.dll').Count; GeneratedCppFiles=$cppFiles; Passed=$true;
    Note='ManagedStripped are actual linker outputs, not shipped managed Player DLLs; their hashes need not equal the approved unstripped BC binary. Native GameAssembly/metadata are actual Player artifacts.' }
[IO.File]::WriteAllText((Join-Path $destination 'identity.json'),($receipt | ConvertTo-Json -Depth 10))
[pscustomobject]@{ Build=$BuildName; Archived=$destination; SourceFiles=@($snapshot).Count; StrippedFiles=$assemblies.Count; StrippedAssemblies=@($assemblies | Where-Object Name -Like '*.dll').Count; GeneratedCppFiles=$cppFiles.Count; Passed=$true }
