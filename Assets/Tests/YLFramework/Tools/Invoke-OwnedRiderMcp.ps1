#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $RiderRoot,
    [Parameter(Mandatory)][ValidateRange(1024,65535)][int] $Port,
    [Parameter(Mandatory)][ValidateSet('list','projects','problems','lint','open')][string] $Action,
    [ValidateSet('Assets/AnalyzerConsumers/IdeDiagnosticProbe.cs','Assets/IdeDefaultProbe.cs')][string] $FilePath='Assets/AnalyzerConsumers/IdeDiagnosticProbe.cs',
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+\.json$')][string] $EvidenceName
)
Set-StrictMode -Version 3.0
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($RiderRoot)
for ($path=$root; $path; $path=[IO.Path]::GetDirectoryName($path)) {
    if (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked Rider evidence root.' }
}
$owner=Get-Content -LiteralPath (Join-Path $root 'ready.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String
function Test-OwnedRiderFile([string] $Actual,[string] $Expected) {
    if ([string]::IsNullOrWhiteSpace($Actual)) {return $false}
    $actualPath=[IO.Path]::GetFullPath([IO.Path]::Combine($owner.Project,$Actual))
    $expectedPath=[IO.Path]::GetFullPath([IO.Path]::Combine($owner.Project,$Expected))
    $actualPath.StartsWith($owner.Project+'\',[StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($actualPath,$expectedPath,[StringComparison]::OrdinalIgnoreCase)
}
function Assert-OwnedRiderProcess {
    $process=Get-Process -Id $owner.Pid -ErrorAction Stop
    if ($owner.Purpose -cne 'Stage9-owned-rider' -or -not $owner.StartupOwnershipVerified -or $owner.Root -cne $root -or
        -not [string]::Equals($process.Path,$owner.Executable,[StringComparison]::OrdinalIgnoreCase) -or
        $process.StartTime.ToUniversalTime() -ne [DateTime]::Parse($owner.StartUtc).ToUniversalTime()) { throw 'Rider PID/executable/start-time ownership changed.' }
    $sockets=@(Get-NetTCPConnection -OwningProcess $owner.Pid -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    if ($sockets.Count -eq 0 -or @($sockets | Where-Object { $_.LocalAddress -notin @('127.0.0.1','::1') }).Count -ne 0) { throw 'The selected loopback port is not owned by this exact Rider process.' }
}
Assert-OwnedRiderProcess
$destination=Join-Path $root $EvidenceName
if (Test-Path -LiteralPath $destination) { throw 'Never overwrite Rider observations.' }
$client=[Net.Http.HttpClient]::new()
$client.Timeout=[TimeSpan]::FromSeconds(170)
$client.DefaultRequestHeaders.Accept.ParseAdd('text/event-stream')
$reader=$null; $stream=$null; $initialize=$null; $response=$null; $arguments=@{}; $tool=$null; $failure=$null
$deadline=[DateTime]::UtcNow.AddSeconds(160)
function Read-OwnedRiderEvent {
    $event=''; $data=[Collections.Generic.List[string]]::new()
    while ($true) {
        $pending=$reader.ReadLineAsync()
        $remaining=$deadline-[DateTime]::UtcNow
        if ($remaining.TotalSeconds -le 0 -or -not $pending.Wait($remaining)) { throw 'Rider SSE deadline expired.' }
        $line=$pending.GetAwaiter().GetResult()
        if ($null -eq $line) { throw 'Rider SSE stream closed.' }
        if ($line -ceq '') { if ($data.Count) {return @{Event=$event; Data=($data -join "`n")}}; continue }
        if ($line.StartsWith('event:',[StringComparison]::Ordinal)) {$event=$line.Substring(6).Trim()}
        elseif ($line.StartsWith('data:',[StringComparison]::Ordinal)) {$data.Add($line.Substring(5).TrimStart())}
    }
}
function Send-OwnedRiderMessage($Message) {
    $content=[Net.Http.StringContent]::new(($Message | ConvertTo-Json -Depth 20 -Compress),[Text.Encoding]::UTF8,'application/json')
    try {
        $posted=$client.PostAsync($postUri,$content).GetAwaiter().GetResult()
        try {if (-not $posted.IsSuccessStatusCode) {throw ('Rider MCP POST rejected: '+[int]$posted.StatusCode)}} finally {$posted.Dispose()}
    } finally {$content.Dispose()}
}
function Read-OwnedRiderResponse([int] $Id) {
    while ($true) {
        $event=Read-OwnedRiderEvent
        $message=$event.Data | ConvertFrom-Json -AsHashtable
        if ($message.ContainsKey('id') -and $message.id -eq $Id) {return $message}
    }
}
try {
    $base=[Uri]('http://127.0.0.1:'+$Port)
    $stream=$client.GetAsync([Uri]::new($base,'/sse'),[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    $null=$stream.EnsureSuccessStatusCode()
    $reader=[IO.StreamReader]::new($stream.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
    $endpoint=Read-OwnedRiderEvent
    if ($endpoint.Event -cne 'endpoint') {throw 'Unexpected Rider SSE handshake.'}
    $postUri=[Uri]::new($base,$endpoint.Data)
    if ($postUri.Scheme -cne 'http' -or $postUri.Host -cne '127.0.0.1' -or $postUri.Port -ne $Port -or $postUri.AbsolutePath -cne '/message') {throw 'SSE endpoint escaped the owned listener.'}
    Send-OwnedRiderMessage @{jsonrpc='2.0';id=1;method='initialize';params=@{protocolVersion='2024-11-05';capabilities=@{};clientInfo=@{name='YL U63 owned evidence';version='1'}}}
    $initialize=Read-OwnedRiderResponse 1
    if ($initialize.ContainsKey('error') -or -not $initialize.ContainsKey('result')) {throw 'Rider initialization failed.'}
    Send-OwnedRiderMessage @{jsonrpc='2.0';method='notifications/initialized';params=@{}}
    if ($Action -ceq 'list') {
        Send-OwnedRiderMessage @{jsonrpc='2.0';id=2;method='tools/list';params=@{}}
    } else {
        $arguments=@{rootFolder=$owner.Project}
        if ($Action -ceq 'projects') {$tool='get_solution_projects'}
        elseif ($Action -ceq 'problems') {$tool='get_file_problems';$arguments.filePath=$FilePath;$arguments.errorsOnly=$false;$arguments.timeout=120000}
        elseif ($Action -ceq 'open') {$tool='open_file_in_editor';$arguments.filePath=$FilePath}
        else {$tool='lint_files';$arguments.files=@('Assets/AnalyzerConsumers/IdeDiagnosticProbe.cs','Assets/IdeDefaultProbe.cs');$arguments.min_severity='warning';$arguments.timeout=120000}
        Send-OwnedRiderMessage @{jsonrpc='2.0';id=2;method='tools/call';params=@{name=$tool;arguments=$arguments}}
    }
    $response=Read-OwnedRiderResponse 2
    if ($response.ContainsKey('error') -or -not $response.ContainsKey('result') -or $response.result -isnot [Collections.IDictionary] -or
        ($response.result.ContainsKey('isError') -and $response.result.isError -eq $true)) {throw 'Rider returned a protocol/tool error; not a clean diagnostic result.'}
    if ($Action -cnotin @('list','open')) {
        $payload=if ($response.result.ContainsKey('structuredContent')) {$response.result.structuredContent} else {$null}
        if ($null -eq $payload) {
            $text=@($response.result.content | Where-Object { $_.type -ceq 'text' })
            if ($text.Count -ne 1) {throw 'Rider tool payload is missing or ambiguous.'}
            $payload=$text[0].text | ConvertFrom-Json -AsHashtable
        }
        if ($payload -isnot [Collections.IDictionary] -or
            ($payload.ContainsKey('timedOut') -and $payload.timedOut -eq $true) -or
            ($payload.ContainsKey('more') -and $payload.more -eq $true) -or
            ($payload.ContainsKey('notAnalyzedReason') -and $payload.notAnalyzedReason)) {throw 'Rider result is incomplete.'}
        if ($Action -ceq 'projects') {
            foreach ($name in @('Analyzer.Acceptance.Consumer','Assembly-CSharp','YLFramework.Runtime')) {
                if (@($payload.modules | Where-Object name -CEQ $name).Count -ne 1) {throw ('Rider actual project graph missing '+$name)}
            }
        } elseif ($Action -ceq 'problems') {
            if (-not $payload.ContainsKey('filePath') -or -not (Test-OwnedRiderFile $payload.filePath $FilePath) -or -not $payload.ContainsKey('errors')) {throw 'Rider diagnostic file identity/schema differs.'}
        } else {
            foreach ($name in $arguments.files) {
                $item=@($payload.items | Where-Object { Test-OwnedRiderFile $_.filePath $name })
                if ($item.Count -ne 1 -or -not $item[0].ContainsKey('problems') -or
                    ($item[0].ContainsKey('timedOut') -and $item[0].timedOut -eq $true) -or
                    ($item[0].ContainsKey('notAnalyzedReason') -and $item[0].notAnalyzedReason)) {throw 'Rider lint omitted or did not finish a target file.'}
            }
        }
    }
    Assert-OwnedRiderProcess
} catch {$failure=$_.Exception.Message; throw}
finally {
    $record=@{Project=$owner.Project; Pid=$owner.Pid; StartUtc=$owner.StartUtc; Port=$Port; Action=$Action; Tool=$tool; Arguments=$arguments;
        Initialize=$initialize; Response=$response; Failure=$failure; CapturedUtc=[DateTime]::UtcNow.ToString('O'); ProcessOwnershipVerified=$true; EditorStateChanging=($Action -ceq 'open')}
    [IO.File]::WriteAllText($destination,($record | ConvertTo-Json -Depth 50))
    if ($null -ne $reader) {$reader.Dispose()}; if ($null -ne $stream) {$stream.Dispose()}; $client.Dispose()
}
if ($Action -ceq 'list') {
    [pscustomobject]@{Server=$initialize.result.serverInfo; Tools=@($response.result.tools | ForEach-Object name); Evidence=$destination}
} else {$response | ConvertTo-Json -Depth 30 -Compress}
