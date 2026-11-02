#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $HostRoot,
    [Parameter(Mandatory)][ValidateSet('info','load','open','buffer','reset','snapshot','stop')][string] $Action,
    [ValidateSet('custom','default')][string] $Probe = 'custom',
    [ValidateSet('bad','inject-fixed','all-fixed','suppressed')][string] $Scenario = 'bad',
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]*$')][string] $ProbeNonce = 'Initial',
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+\.json$')][string] $EvidenceName
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
function Same-OwnedVsPath([string] $First, [string] $Second) {
    [string]::Equals([IO.Path]::GetFullPath($First),[IO.Path]::GetFullPath($Second),[StringComparison]::OrdinalIgnoreCase)
}
$root=[IO.Path]::GetFullPath($HostRoot)
for ($path=$root; $path; $path=[IO.Path]::GetDirectoryName($path)) {
    if (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked VS host root.' }
}
$config=Get-Content -LiteralPath (Join-Path $root 'host.json') -Raw | ConvertFrom-Json
$ready=Get-Content -LiteralPath (Join-Path $root 'ready.json') -Raw | ConvertFrom-Json -DateKind String
if ($config.Purpose -cne 'Stage9-owned-vs-host' -or -not (Same-OwnedVsPath $config.Root $root) -or -not $ready.Owned -or $ready.UserControl -or
    -not (Same-OwnedVsPath $ready.ProjectRoot $config.Project) -or -not (Same-OwnedVsPath $ready.Executable $config.VsExecutable) -or $ready.BuildInvokedByHarness) { throw 'VS readiness does not match this owner.' }
$process=Get-Process -Id $ready.Pid -ErrorAction Stop
$hostProcess=Get-Process -Id $ready.HostPid -ErrorAction Stop
if (-not (Same-OwnedVsPath $process.Path $config.VsExecutable) -or $process.StartTime.ToUniversalTime() -ne [DateTime]::Parse($ready.StartTimeUtc).ToUniversalTime() -or
    -not (Same-OwnedVsPath $hostProcess.Path (Join-Path $root 'OwnedVsDteHost.exe')) -or
    (Get-FileHash -LiteralPath $hostProcess.Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $config.ExeSha256) { throw 'Owned VS or host process identity changed.' }
$destination=Join-Path $root $EvidenceName
$requestEvidence=[IO.Path]::ChangeExtension($destination,'.request.json')
if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $requestEvidence)) { throw 'Preserve previous IDE evidence.' }
$request=@{action=$Action; nonce=$config.Nonce}
if ($Action -in @('open','buffer','reset')) {
    $request.file=if ($Probe -ceq 'custom') {'Assets/AnalyzerConsumers/IdeDiagnosticProbe.cs'} else {'Assets/IdeDefaultProbe.cs'}
}
if ($Action -ceq 'buffer') {
    $member=if ($Scenario -in @('inject-fixed','all-fixed')) {"[Inject] public object Good_$ProbeNonce { get; private set; }"} else {"[Inject] public static object Bad_$ProbeNonce;"}
    if ($Scenario -ceq 'suppressed') { $member="#pragma warning disable YL0008`n$member`n#pragma warning restore YL0008" }
    $load=if ($Scenario -ceq 'all-fixed') {'owner.LoadPanel<Panel>("name");'} else {'owner.LoadView<Panel>("name", loadViewArgs: null);'}
    $wait=if ($Scenario -ceq 'all-fixed') {'yield return new WaitUntil(() => request.HasStarted || request.IsDone);'} else {'yield return request;'}
    $request.content="using System.Collections;`nusing UnityEngine;`nusing YLFramework.Core;`nusing YLFramework.Audio;`nusing YLFramework.DependencyInjection;`n" +
        "internal sealed class VsLiveProbe_$ProbeNonce`n{`n$member`n    public void Load(Module owner) { $load }`n    public IEnumerator Wait(AudioPlayRequest request) { $wait }`n}`n"
}
$record=@{ Action=$Action; Probe=$Probe; Scenario=$Scenario; ProbeNonce=$ProbeNonce; Project=$config.Project; Pid=$ready.Pid;
    HostPid=$ready.HostPid; RequestedUtc=[DateTime]::UtcNow.ToString('O'); CallerSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant() }
if ($request.ContainsKey('file')) { $record.File=$request.file }
if ($request.ContainsKey('content')) { $record.Content=$request.content }
$output=[IO.FileStream]::new($requestEvidence,[IO.FileMode]::CreateNew)
try { $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($record | ConvertTo-Json -Depth 5)); $output.Write($bytes,0,$bytes.Length) } finally { $output.Dispose() }
$client=[IO.Pipes.NamedPipeClientStream]::new('.',$config.Pipe,[IO.Pipes.PipeDirection]::InOut)
try {
    $client.Connect(15000)
    $writer=[IO.StreamWriter]::new($client,[Text.UTF8Encoding]::new($false),65536,$true)
    $reader=[IO.StreamReader]::new($client,[Text.Encoding]::UTF8,$true,65536,$true)
    $writer.AutoFlush=$true
    $writer.WriteLine(($request | ConvertTo-Json -Depth 5 -Compress))
    $pending=$reader.ReadLineAsync()
    if (-not $pending.Wait([TimeSpan]::FromSeconds(120))) { throw 'Owned VS command exceeded its deadline.' }
    $text=$pending.GetAwaiter().GetResult()
    if ($null -eq $text) { throw 'Owned VS host closed without a reply.' }
    $output=[IO.FileStream]::new($destination,[IO.FileMode]::CreateNew)
    try { $bytes=[Text.UTF8Encoding]::new($false).GetBytes($text); $output.Write($bytes,0,$bytes.Length) } finally { $output.Dispose() }
    $reply=$text | ConvertFrom-Json
    if (-not $reply.Ok) { throw $reply.Error }
    if ($reply.Info.Pid -ne $ready.Pid -or $reply.Info.HostPid -ne $ready.HostPid -or -not (Same-OwnedVsPath $reply.Info.ProjectRoot $config.Project) -or
        -not $reply.Info.Owned -or $reply.Info.UserControl) { throw 'IDE response identity changed.' }
    $text
} finally { $client.Dispose() }
