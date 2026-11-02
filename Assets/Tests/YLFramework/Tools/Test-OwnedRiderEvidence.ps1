#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [string] $RiderRoot,
    [ValidatePattern('^[a-z0-9][a-z0-9-]*$')][string] $Run
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $PSVersionTable.PSVersion.Major -ne 7 -or $PSVersionTable.PSVersion.Minor -ne 6) {
    throw 'Windows PowerShell 7.6.x is required.'
}

function Confirm-OwnedRiderEvidence([bool] $Condition, [string] $Code, [string] $Reason) {
    if (-not $Condition) { throw ('Rider evidence [' + $Code + ']: ' + $Reason) }
}

function Assert-OwnedRiderKeys($Value, [string[]] $Keys, [string] $At) {
    Confirm-OwnedRiderEvidence ($Value -is [Collections.IDictionary]) 'schema' ($At + ' must be an object')
    foreach ($key in $Keys) {
        Confirm-OwnedRiderEvidence ($Value.ContainsKey($key)) 'schema' ($At + ' is missing ' + $key)
    }
}

function ConvertTo-OwnedRiderPath($Value, [string] $Base = '') {
    Confirm-OwnedRiderEvidence ($Value -is [string] -and -not [string]::IsNullOrWhiteSpace($Value)) 'schema' 'path must be a nonempty string'
    if ([IO.Path]::IsPathRooted($Value) -or -not $Base.Length) {
        Confirm-OwnedRiderEvidence ([IO.Path]::IsPathFullyQualified($Value)) 'file-set' 'path must be fully qualified or solution-relative'
        return [IO.Path]::GetFullPath($Value).TrimEnd('\')
    }
    return [IO.Path]::GetFullPath($Value, $Base).TrimEnd('\')
}

function ConvertTo-OwnedRiderUtc($Value, [string] $At) {
    Confirm-OwnedRiderEvidence ($Value -is [string] -and $Value -cmatch '^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?Z$') 'schema' ($At + ' must be a UTC timestamp')
    try { return [DateTimeOffset]::Parse($Value, [Globalization.CultureInfo]::InvariantCulture) }
    catch { throw ('Rider evidence [schema]: invalid UTC timestamp at ' + $At) }
}

function Assert-OwnedRiderComplete($Value, [string] $At) {
    Assert-OwnedRiderKeys $Value @() $At
    if ($Value.ContainsKey('error')) { throw ('Rider evidence [rpc-error]: ' + $At + ' contains an error') }
    foreach ($flag in @('isError', 'timedOut', 'more')) {
        if (-not $Value.ContainsKey($flag)) { continue }
        if ($flag -ceq 'timedOut' -and $null -eq $Value[$flag]) { continue }
        Confirm-OwnedRiderEvidence ($Value[$flag] -is [bool]) 'schema' ($At + '.' + $flag + ' must be boolean')
        Confirm-OwnedRiderEvidence (-not $Value[$flag]) 'incomplete' ($At + '.' + $flag + ' is true')
    }
    if ($Value.ContainsKey('notAnalyzedReason')) {
        $reason = $Value.notAnalyzedReason
        Confirm-OwnedRiderEvidence ($null -eq $reason -or $reason -is [string]) 'schema' ($At + '.notAnalyzedReason must be string or null')
        Confirm-OwnedRiderEvidence ($null -eq $reason -or $reason.Length -eq 0) 'incomplete' ($At + ' was not analyzed: ' + $reason)
    }
}

# Only the type/required/properties/items vocabulary used by the three saved tool schemas is needed.
function Assert-OwnedRiderSchema($Value, $Schema, [string] $At) {
    Assert-OwnedRiderKeys $Schema @('type') ('saved schema for ' + $At)
    $matches = $false
    foreach ($type in @($Schema.type)) {
        $matches = $matches -or $(switch -CaseSensitive ($type) {
            'object' { $Value -is [Collections.IDictionary] }
            'array' { $Value -is [array] }
            'string' { $Value -is [string] }
            'integer' { $Value -is [int] -or $Value -is [long] }
            'boolean' { $Value -is [bool] }
            'null' { $null -eq $Value }
            default { throw ('Rider evidence [schema]: unsupported saved schema type at ' + $At) }
        })
    }
    Confirm-OwnedRiderEvidence $matches 'schema' ($At + ' has the wrong JSON type')
    if ($Value -is [Collections.IDictionary]) {
        if ($Schema.ContainsKey('required')) {
            Confirm-OwnedRiderEvidence ($Schema.required -is [array]) 'schema' ('invalid required list at ' + $At)
            Assert-OwnedRiderKeys $Value $Schema.required $At
        }
        if ($Schema.ContainsKey('properties')) {
            Assert-OwnedRiderKeys $Schema.properties @() ('schema properties at ' + $At)
            foreach ($key in $Schema.properties.Keys) {
                if ($Value.ContainsKey($key)) { Assert-OwnedRiderSchema $Value[$key] $Schema.properties[$key] ($At + '.' + $key) }
            }
        }
    } elseif ($Value -is [array]) {
        Assert-OwnedRiderKeys $Schema @('items') ('array schema at ' + $At)
        for ($index = 0; $index -lt $Value.Count; $index++) {
            Assert-OwnedRiderSchema $Value[$index] $Schema.items ($At + '[' + $index + ']')
        }
    }
}

function Assert-OwnedRiderRecord($Record, $Ready, [int] $Port, [string] $Action, $Tool, [bool] $FailedCapture = $false) {
    Assert-OwnedRiderKeys $Record @('Project', 'Pid', 'StartUtc', 'Port', 'Action', 'Tool', 'Arguments', 'Failure', 'ProcessOwnershipVerified', 'CapturedUtc', 'Initialize', 'Response') $Action
    Assert-OwnedRiderComplete $Record $Action
    $project = ConvertTo-OwnedRiderPath $Ready.Project
    Confirm-OwnedRiderEvidence ([string]::Equals((ConvertTo-OwnedRiderPath $Record.Project), $project, [StringComparison]::OrdinalIgnoreCase)) 'ownership' 'record project differs'
    Confirm-OwnedRiderEvidence (($Record.Pid -is [int] -or $Record.Pid -is [long]) -and $Record.Pid -eq $Ready.Pid -and
        ($Record.Port -is [int] -or $Record.Port -is [long]) -and $Record.Port -eq $Port -and
        $Record.ProcessOwnershipVerified -is [bool] -and $Record.ProcessOwnershipVerified) 'ownership' 'record PID/port/recorded ownership differs'
    Confirm-OwnedRiderEvidence ((ConvertTo-OwnedRiderUtc $Record.StartUtc 'record.StartUtc') -eq (ConvertTo-OwnedRiderUtc $Ready.StartUtc 'ready.StartUtc')) 'ownership' 'record start time differs'
    $toolTypeMatches = if ($null -eq $Tool) { $null -eq $Record.Tool } else { $Record.Tool -is [string] }
    Confirm-OwnedRiderEvidence ($Record.Action -is [string] -and $toolTypeMatches -and
        $Record.Action -ceq $Action -and $Record.Tool -ceq $Tool) 'schema' 'record action/tool differs or is not scalar'
    if ($FailedCapture) {
        Confirm-OwnedRiderEvidence ($Record.Failure -is [string] -and $Record.Failure.Length -gt 0) 'capture-failure' 'historical failed wrapper must remain a failure'
    } else {
        Confirm-OwnedRiderEvidence ($null -eq $Record.Failure) 'capture-failure' ('recorded failure: ' + $Record.Failure)
    }
    if ($Record.ContainsKey('EditorStateChanging')) {
        Confirm-OwnedRiderEvidence ($Record.EditorStateChanging -is [bool] -and -not $Record.EditorStateChanging) 'schema' 'read-only record cannot change Editor state'
    }
    Assert-OwnedRiderKeys $Record.Arguments @() 'Arguments'
    if ($Action -ceq 'list') {
        Confirm-OwnedRiderEvidence ($Record.Arguments.Count -eq 0) 'schema' 'tools/list has no rootFolder arguments'
    } else {
        Assert-OwnedRiderKeys $Record.Arguments @('rootFolder') 'Arguments'
        Confirm-OwnedRiderEvidence ([string]::Equals((ConvertTo-OwnedRiderPath $Record.Arguments.rootFolder), $project, [StringComparison]::OrdinalIgnoreCase)) 'ownership' 'Arguments.rootFolder differs'
    }
    foreach ($entry in @(@{ Value = $Record.Initialize; Id = 1 }, @{ Value = $Record.Response; Id = 2 })) {
        Assert-OwnedRiderComplete $entry.Value 'JSON-RPC response'
        Assert-OwnedRiderKeys $entry.Value @('id', 'jsonrpc', 'result') 'JSON-RPC response'
        Confirm-OwnedRiderEvidence (($entry.Value.id -is [int] -or $entry.Value.id -is [long]) -and $entry.Value.id -eq $entry.Id -and
            $entry.Value.jsonrpc -is [string] -and $entry.Value.jsonrpc -ceq '2.0') 'schema' 'JSON-RPC identity differs'
        Assert-OwnedRiderComplete $entry.Value.result 'JSON-RPC result'
    }
    $initialize = $Record.Initialize.result
    Assert-OwnedRiderKeys $initialize @('protocolVersion', 'serverInfo') 'Initialize.result'
    Assert-OwnedRiderKeys $initialize.serverInfo @('name', 'version') 'Initialize.result.serverInfo'
    Confirm-OwnedRiderEvidence ($initialize.protocolVersion -is [string] -and $initialize.serverInfo.name -is [string] -and
        $initialize.serverInfo.version -is [string]) 'schema' 'MCP server identity fields must be strings'
    Confirm-OwnedRiderEvidence ($initialize.protocolVersion -ceq '2024-11-05' -and $initialize.serverInfo.name -ceq 'JetBrains Rider MCP Server' -and
        $initialize.serverInfo.version -ceq $Ready.Version) 'ownership' 'saved MCP server identity differs'
    $at = ConvertTo-OwnedRiderUtc $Record.CapturedUtc 'CapturedUtc'
    Confirm-OwnedRiderEvidence ($at -gt (ConvertTo-OwnedRiderUtc $Ready.StartUtc 'StartUtc')) 'chronology' 'capture must follow process start'
    return $at
}

function Assert-OwnedRiderSnapshot($Record, $Ready, $ToolSchema, $Spec) {
    $project = ConvertTo-OwnedRiderPath $Ready.Project
    $custom = 'Assets/AnalyzerConsumers/IdeDiagnosticProbe.cs'
    $default = 'Assets/IdeDefaultProbe.cs'
    $isLint = $Spec.Mode -ceq 'lint'
    $arguments = $Record.Arguments
    Assert-OwnedRiderSchema $arguments $ToolSchema.inputSchema 'Arguments'
    Assert-OwnedRiderKeys $arguments @('timeout') 'Arguments'
    Confirm-OwnedRiderEvidence (($arguments.timeout -is [int] -or $arguments.timeout -is [long]) -and $arguments.timeout -eq 120000) 'schema' 'unexpected analysis budget'
    Assert-OwnedRiderKeys $Record.Response.result @('structuredContent') 'Response.result'
    $payload = $Record.Response.result.structuredContent
    Assert-OwnedRiderComplete $payload 'structuredContent'
    Assert-OwnedRiderSchema $payload $ToolSchema.outputSchema 'structuredContent'
    $targets = if ($isLint) { @($custom, $default) } elseif ($null -ne $Spec.C) { @($custom) } else { @($default) }
    if ($isLint) {
        Assert-OwnedRiderKeys $arguments @('files', 'min_severity') 'Arguments'
        Assert-OwnedRiderKeys $payload @('items') 'structuredContent'
        Confirm-OwnedRiderEvidence ($arguments.min_severity -ceq 'warning') 'schema' 'lint must retain IDE warnings and hints'
        Confirm-OwnedRiderEvidence ($arguments.files -is [array] -and $payload.items -is [array] -and
            $arguments.files.Count -eq 2 -and $payload.items.Count -eq 2) 'file-set' 'lint requires exactly both probes'
        $requested = @($arguments.files | ForEach-Object { ConvertTo-OwnedRiderPath $_ $project })
        $items = $payload.items
        $diagnosticKey = 'problems'
        $lineKey = 'lineText'
    } else {
        Assert-OwnedRiderKeys $arguments @('filePath', 'errorsOnly') 'Arguments'
        Assert-OwnedRiderKeys $payload @('filePath', 'errors') 'structuredContent'
        Confirm-OwnedRiderEvidence ($arguments.errorsOnly -is [bool] -and -not $arguments.errorsOnly) 'schema' 'get_file_problems must retain warnings'
        $requested = @((ConvertTo-OwnedRiderPath $arguments.filePath $project))
        $items = @($payload)
        $diagnosticKey = 'errors'
        $lineKey = 'lineContent'
    }
    $returned = @(
        foreach ($item in $items) {
            Assert-OwnedRiderKeys $item @('filePath', $diagnosticKey) 'file result'
            Assert-OwnedRiderComplete $item 'file result'
            Confirm-OwnedRiderEvidence ($item[$diagnosticKey] -is [array]) 'schema' 'completed file requires a diagnostic array, not null'
            ConvertTo-OwnedRiderPath $item.filePath $project
        }
    )
    $files = [Collections.Generic.List[object]]::new()
    foreach ($target in $targets) {
        $absolute = ConvertTo-OwnedRiderPath $target $project
        Confirm-OwnedRiderEvidence (@($requested | Where-Object { [string]::Equals($_, $absolute, [StringComparison]::OrdinalIgnoreCase) }).Count -eq 1 -and
            @($returned | Where-Object { [string]::Equals($_, $absolute, [StringComparison]::OrdinalIgnoreCase) }).Count -eq 1) 'file-set' ('missing, duplicate or wrong absolute probe path: ' + $target)
        $index = 0
        while (-not [string]::Equals($returned[$index], $absolute, [StringComparison]::OrdinalIgnoreCase)) { $index++ }
        $item = $items[$index]
        $isCustom = $target -ceq $custom
        $expected = @(if ($isCustom) { $Spec.C } else { $Spec.D })
        $anchors = @(if ($isCustom) { $Spec.CA } else { $Spec.DA })
        $errorFamily = if ($isCustom) { $Spec.CE } else { $Spec.DE }
        $associations = [Collections.Generic.List[object]]::new()
        $others = [Collections.Generic.List[object]]::new()
        foreach ($diagnostic in $item[$diagnosticKey]) {
            Assert-OwnedRiderKeys $diagnostic @('severity', 'description', $lineKey, 'line', 'column') 'diagnostic'
            Assert-OwnedRiderComplete $diagnostic 'diagnostic'
            Confirm-OwnedRiderEvidence ($diagnostic.description -is [string] -and -not [string]::IsNullOrWhiteSpace($diagnostic.description) -and
                $diagnostic[$lineKey] -is [string] -and -not [string]::IsNullOrWhiteSpace($diagnostic[$lineKey])) 'schema' 'diagnostic text is missing'
            Confirm-OwnedRiderEvidence (($diagnostic.line -is [int] -or $diagnostic.line -is [long]) -and
                ($diagnostic.column -is [int] -or $diagnostic.column -is [long]) -and $diagnostic.line -gt 0 -and $diagnostic.column -gt 0) 'location' 'diagnostic location must be positive and integral'
            Confirm-OwnedRiderEvidence ($diagnostic.severity -cin @('ERROR', 'WARNING', 'WEAK_WARNING', 'WEAK WARNING', 'INFO', 'INFORMATION', 'HINT', 'SUGGESTION')) 'severity' 'unknown diagnostic severity'
            # These are associations from this Rider's descriptions, never raw diagnostic IDs.
            $family = if ($diagnostic.description -cmatch '^\u6210\u5458 .* \u7684 Inject \u4e0d\u652f\u6301') { 8 }
                elseif ($diagnostic.description -cmatch '^YLFramework\.Core\.Panel \u786e\u5b9a\u5c5e\u4e8e Panel') { 9 }
                elseif ($diagnostic.description -cmatch '^AudioPlayRequest \u662f\u666e\u901a\u5bf9\u8c61') { 10 }
                else { 0 }
            if ($family) {
                $associations.Add(@{ AssociatedFamily = $family; Diagnostic = $diagnostic })
            } else {
                Confirm-OwnedRiderEvidence ($diagnostic.severity -cne 'ERROR') 'unexpected-error' ('unassociated ERROR: ' + $diagnostic.description)
                $others.Add($diagnostic)
            }
        }
        Confirm-OwnedRiderEvidence ((@($associations | ForEach-Object { $_.AssociatedFamily } | Sort-Object) -join ',') -ceq
            (@($expected | Sort-Object) -join ',')) 'families' ('exact associated family/control set differs: ' + $target)
        foreach ($association in $associations) {
            $severity = if ($association.AssociatedFamily -eq $errorFamily) { 'ERROR' } else { 'WARNING' }
            Confirm-OwnedRiderEvidence ($association.Diagnostic.severity -ceq $severity) 'severity' ('family/control severity differs: ' + $target)
        }
        $className = if ($isCustom) { 'IdeDiagnosticProbe' } else { 'IdeDefaultProbe' }
        $sourceLines = @(
            'using System.Collections;'
            'using UnityEngine;'
            'using YLFramework.Core;'
            'using YLFramework.Audio;'
            'using YLFramework.DependencyInjection;'
            ('public sealed class ' + $className)
            '{'
            if ($anchors -ccontains 'setter') { '    [Inject] public object Dependency { get; private set; }' }
            else { '    [Inject] public static object Bad;' }
            if ($expected -contains 9) { '    public void Load(Module owner) { owner.LoadView<Panel>("name", loadViewArgs: null); }' }
            else { '    public void Load(Module owner) { owner.LoadPanel<Panel>("name"); }' }
            if ($expected -contains 10) { '    public IEnumerator Run(AudioPlayRequest request) { yield return request; }' }
            else { '    public IEnumerator Run(AudioPlayRequest request) { yield return new WaitUntil(() => request.HasStarted || request.IsDone); }' }
            '}'
        )
        $pragma = $Spec.Name -ceq 'custom-error-pragma-problems.json'
        if ($pragma) {
            $sourceLines = @($sourceLines[0..6]) + '#pragma warning disable YL0008' + $sourceLines[7] +
                '#pragma warning restore YL0008' + @($sourceLines[8..10])
        }
        foreach ($diagnostic in $item[$diagnosticKey]) {
            Confirm-OwnedRiderEvidence ($diagnostic.line -le $sourceLines.Count -and
                $diagnostic[$lineKey] -ceq $sourceLines[$diagnostic.line - 1] -and
                $diagnostic.column -le $sourceLines[$diagnostic.line - 1].Length + 1) 'location' ('diagnostic differs from controlled probe source: ' + $target)
        }
        $familyLocations = if ($pragma) { @{ 8 = @(9, 35); 9 = @(11, 38); 10 = @(12, 69) } }
            else { @{ 8 = @(8, 35); 9 = @(9, 38); 10 = @(10, 69) } }
        foreach ($association in $associations) {
            $location = $familyLocations[$association.AssociatedFamily]
            Confirm-OwnedRiderEvidence ($association.Diagnostic.line -eq $location[0] -and
                $association.Diagnostic.column -eq $location[1]) 'location' ('family is not at its controlled source location: ' + $target)
            if ($association.AssociatedFamily -eq 8) {
                Confirm-OwnedRiderEvidence ($association.Diagnostic.description -cmatch
                    ('^\u6210\u5458 ' + [regex]::Escape($className) + '\.Bad \u7684 Inject \u4e0d\u652f\u6301')) 'location' ('Inject description names another probe/member: ' + $target)
            }
        }
        foreach ($anchor in $anchors) {
            $sourceLine = switch -CaseSensitive ($anchor) {
                'using' { 'using UnityEngine;' }
                'setter' { '[Inject] public object Dependency { get; private set; }' }
                default { throw 'Rider evidence [schema]: unknown IDE evidence anchor' }
            }
            Confirm-OwnedRiderEvidence (@($others | Where-Object { $_[$lineKey].Trim() -ceq $sourceLine -and $_.severity -ceq 'WARNING' }).Count -eq 1) 'ide-evidence' ('saved IDE warning lost or falsely empty: ' + $target + '/' + $anchor)
            $anchorLocation = if ($anchor -ceq 'using') { @(2, 1) } else { @(8, 46) }
            $warning = @($others | Where-Object { $_[$lineKey].Trim() -ceq $sourceLine -and $_.severity -ceq 'WARNING' })[0]
            Confirm-OwnedRiderEvidence ($warning.line -eq $anchorLocation[0] -and $warning.column -eq $anchorLocation[1]) 'location' ('IDE warning is not at its controlled source location: ' + $target + '/' + $anchor)
        }
        $files.Add(@{
            File = $absolute; ReturnedPath = $item.filePath; RawDiagnostics = $item[$diagnosticKey]
            FamilyAssociations = $associations.ToArray(); OtherDiagnostics = $others.ToArray()
            ExpectedFamilies = $expected; ExpectedErrorFamily = $errorFamily
            SourceLocationsVerified = $true; ControlledProbeSourceLines = $sourceLines
        })
    }
    return @{ Name = $Spec.Name; Tool = $Record.Tool; CapturedUtc = $Record.CapturedUtc; Files = $files.ToArray(); PayloadSource = 'Response.result.structuredContent' }
}

# No file, process, IDE, network or configuration operations occur in this validator or its assertion helpers.
function Assert-OwnedRiderEvidenceBundle($Bundle, [string] $RiderRoot) {
    Assert-OwnedRiderKeys $Bundle @('Ready', 'Owner', 'Records', 'CurrentInputs', 'SolutionSha256', 'AnalyzerSha256', 'Sources') 'bundle'
    $root = ConvertTo-OwnedRiderPath $RiderRoot
    $ready = $Bundle.Ready
    $owner = $Bundle.Owner
    Assert-OwnedRiderKeys $ready @('Purpose', 'Root', 'Project', 'Pid', 'StartUtc', 'RequestedUtc', 'StartupOwnershipVerified', 'Version', 'Solution', 'SolutionSha256', 'PackageSha256') 'ready'
    Assert-OwnedRiderKeys $owner @('Purpose', 'Project', 'PackageSha256', 'AnalyzerSha256', 'Inputs') 'owner'
    Confirm-OwnedRiderEvidence ($ready.Version -is [string] -and -not [string]::IsNullOrWhiteSpace($ready.Version)) 'schema' 'ready.Version must be a string'
    $project = ConvertTo-OwnedRiderPath $owner.Project
    Confirm-OwnedRiderEvidence ($ready.Purpose -ceq 'Stage9-owned-rider' -and $owner.Purpose -ceq 'Stage9-owned-analyzer-inputs' -and
        [string]::Equals((ConvertTo-OwnedRiderPath $ready.Root), $root, [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals((ConvertTo-OwnedRiderPath $ready.Project), $project, [StringComparison]::OrdinalIgnoreCase) -and
        ($ready.Pid -is [int] -or $ready.Pid -is [long]) -and $ready.Pid -gt 0 -and
        $ready.StartupOwnershipVerified -is [bool] -and $ready.StartupOwnershipVerified -and
        $ready.PackageSha256 -is [string] -and $ready.PackageSha256 -cmatch '^[a-f0-9]{64}$' -and
        $ready.PackageSha256 -ceq $owner.PackageSha256) 'ownership' 'ready/root/project/package ownership binding differs'
    Confirm-OwnedRiderEvidence ((ConvertTo-OwnedRiderUtc $ready.RequestedUtc 'RequestedUtc') -le (ConvertTo-OwnedRiderUtc $ready.StartUtc 'StartUtc')) 'chronology' 'launch request must not follow process start'
    $solution = ConvertTo-OwnedRiderPath $ready.Solution
    Confirm-OwnedRiderEvidence ($solution.StartsWith($project + '\', [StringComparison]::OrdinalIgnoreCase) -and
        $ready.SolutionSha256 -is [string] -and $ready.SolutionSha256 -cmatch '^[a-f0-9]{64}$' -and
        $Bundle.SolutionSha256 -ceq $ready.SolutionSha256 -and $Bundle.AnalyzerSha256 -ceq $owner.AnalyzerSha256) 'baseline' 'solution/analyzer bytes differ from recorded owner'
    $inputPaths = @(
        'Assets/AnalyzerConsumers/Analyzer.Acceptance.Consumer.asmdef',
        'Assets/AnalyzerConsumers/IdeDiagnosticProbe.cs', 'Assets/IdeDefaultProbe.cs',
        'Assets/AnalyzerConsumers/Analyzer.Acceptance.Consumer.ruleset', 'Assets/Assembly-CSharp.ruleset'
    )
    Confirm-OwnedRiderEvidence ($owner.Inputs -is [array] -and $Bundle.CurrentInputs -is [array] -and
        $owner.Inputs.Count -eq 5 -and $Bundle.CurrentInputs.Count -eq 5) 'baseline' 'exactly five immutable baseline inputs are required'
    foreach ($path in $inputPaths) {
        $absolute = ConvertTo-OwnedRiderPath $path $project
        $entries = @(
            foreach ($set in @(@{ Rows = $owner.Inputs }, @{ Rows = $Bundle.CurrentInputs })) {
                foreach ($row in $set.Rows) { Assert-OwnedRiderKeys $row @('Path', 'Sha256', 'MetaSha256', 'Guid') 'baseline input' }
                $found = @($set.Rows | Where-Object { [string]::Equals((ConvertTo-OwnedRiderPath $_.Path $project), $absolute, [StringComparison]::OrdinalIgnoreCase) })
                Confirm-OwnedRiderEvidence ($found.Count -eq 1) 'baseline' ('missing or duplicate baseline path: ' + $path)
                $found[0]
            }
        )
        foreach ($field in @('Sha256', 'MetaSha256', 'Guid')) {
            $pattern = if ($field -ceq 'Guid') { '^[a-f0-9]+$' } else { '^[a-f0-9]{64}$' }
            Confirm-OwnedRiderEvidence ($entries[0][$field] -is [string] -and $entries[0][$field] -cmatch $pattern -and
                $entries[1][$field] -ceq $entries[0][$field]) 'baseline' ($path + ' ' + $field + ' differs from immutable owner')
        }
    }
    $records = $Bundle.Records
    Assert-OwnedRiderKeys $records @('tools-confirmed.json', 'projects-initial.json', 'tools-initial.json', 'custom-baseline-problems.json', 'custom-error-pragma-problems.json') 'records'
    $list = $records['tools-confirmed.json']
    Assert-OwnedRiderKeys $list @('Port') 'tools-confirmed.json'
    Confirm-OwnedRiderEvidence (($list.Port -is [int] -or $list.Port -is [long]) -and $list.Port -ge 1024 -and $list.Port -le 65535) 'ownership' 'invalid recorded loopback port'
    $port = [int] $list.Port
    $previous = Assert-OwnedRiderRecord $list $ready $port 'list' $null
    Assert-OwnedRiderKeys $list.Response.result @('tools') 'tools/list result'
    Confirm-OwnedRiderEvidence ($list.Response.result.tools -is [array]) 'schema' 'tools/list must contain its real tools array'
    $schemas = @{}
    foreach ($name in @('get_file_problems', 'lint_files', 'get_solution_projects')) {
        $tools = @($list.Response.result.tools | Where-Object { $_ -is [Collections.IDictionary] -and $_.ContainsKey('name') -and $_.name -is [string] -and $_.name -ceq $name })
        Confirm-OwnedRiderEvidence ($tools.Count -eq 1) 'schema' ('missing or duplicate discovered tool: ' + $name)
        Assert-OwnedRiderKeys $tools[0] @('inputSchema', 'outputSchema') ('tool ' + $name)
        foreach ($schema in @($tools[0].inputSchema, $tools[0].outputSchema)) {
            Assert-OwnedRiderKeys $schema @('type', 'properties') ('schema for ' + $name)
            Confirm-OwnedRiderEvidence ($schema.type -ceq 'object') 'schema' ('tool schema must describe an object: ' + $name)
        }
        $schemas[$name] = $tools[0]
    }
    $projects = $records['projects-initial.json']
    $at = Assert-OwnedRiderRecord $projects $ready $port 'projects' 'get_solution_projects'
    Confirm-OwnedRiderEvidence ($at -gt $previous) 'chronology' 'project discovery must follow tool discovery'
    $previous = $at
    Assert-OwnedRiderSchema $projects.Arguments $schemas.get_solution_projects.inputSchema 'projects.Arguments'
    Assert-OwnedRiderKeys $projects.Response.result @('structuredContent') 'projects.Response.result'
    $graph = $projects.Response.result.structuredContent
    Assert-OwnedRiderComplete $graph 'project graph'
    Assert-OwnedRiderSchema $graph $schemas.get_solution_projects.outputSchema 'project graph'
    Assert-OwnedRiderKeys $graph @('modules') 'project graph'
    foreach ($name in @('YLFramework.Runtime', 'Analyzer.Acceptance.Consumer', 'Assembly-CSharp')) {
        Confirm-OwnedRiderEvidence (@($graph.modules | Where-Object { $_.name -ceq $name }).Count -eq 1) 'project-graph' ('missing or duplicate actual module: ' + $name)
    }
    $specs = @(
        @{ Name = 'custom-baseline-confirmed.json'; Mode = 'problems'; C = @(8,9,10); D = $null; CA = @('using'); DA = @(); CE = 0; DE = 0 },
        @{ Name = 'default-baseline-problems.json'; Mode = 'problems'; C = $null; D = @(8,9,10); CA = @(); DA = @('using'); CE = 0; DE = 0 },
        @{ Name = 'custom-inject-fixed-problems.json'; Mode = 'problems'; C = @(9,10); D = $null; CA = @('using','setter'); DA = @(); CE = 0; DE = 0 },
        @{ Name = 'custom-all-fixed-problems.json'; Mode = 'problems'; C = @(); D = $null; CA = @('setter'); DA = @(); CE = 0; DE = 0 },
        @{ Name = 'custom-positive-default-control-lint.json'; Mode = 'lint'; C = @(); D = @(8,9,10); CA = @('setter'); DA = @('using'); CE = 0; DE = 0 },
        @{ Name = 'default-inject-fixed-problems.json'; Mode = 'problems'; C = $null; D = @(9,10); CA = @(); DA = @('using','setter'); CE = 0; DE = 0 },
        @{ Name = 'both-all-fixed-lint.json'; Mode = 'lint'; C = @(); D = @(); CA = @('setter'); DA = @('setter'); CE = 0; DE = 0 },
        @{ Name = 'restored-bad-both-lint.json'; Mode = 'lint'; C = @(8,9,10); D = @(8,9,10); CA = @('using'); DA = @('using'); CE = 0; DE = 0 },
        @{ Name = 'custom-error-default-warning-lint.json'; Mode = 'lint'; C = @(8,9,10); D = @(8,9,10); CA = @('using'); DA = @('using'); CE = 8; DE = 0 },
        @{ Name = 'custom-error-warning-restored-lint.json'; Mode = 'lint'; C = @(8,9,10); D = @(8,9,10); CA = @('using'); DA = @('using'); CE = 0; DE = 0 },
        @{ Name = 'default-error-custom-warning-lint.json'; Mode = 'lint'; C = @(8,9,10); D = @(8,9,10); CA = @('using'); DA = @('using'); CE = 0; DE = 8 },
        @{ Name = 'custom-none-default-warning-lint.json'; Mode = 'lint'; C = @(9,10); D = @(8,9,10); CA = @('using'); DA = @('using'); CE = 0; DE = 0 },
        @{ Name = 'custom-inherit-both-warning-lint.json'; Mode = 'lint'; C = @(8,9,10); D = @(8,9,10); CA = @('using'); DA = @('using'); CE = 0; DE = 0 }
    )
    # Require the complete batch before validating rows, so missing cases are never silently skipped.
    Assert-OwnedRiderKeys $records @($specs | ForEach-Object { $_.Name }) 'records'
    $checks = [Collections.Generic.List[object]]::new()
    foreach ($spec in $specs) {
        $record = $records[$spec.Name]
        $tool = if ($spec.Mode -ceq 'lint') { 'lint_files' } else { 'get_file_problems' }
        $at = Assert-OwnedRiderRecord $record $ready $port $spec.Mode $tool
        $check = Assert-OwnedRiderSnapshot $record $ready $schemas[$tool] $spec
        Confirm-OwnedRiderEvidence ($at -gt $previous) 'chronology' ('case must follow preceding observation: ' + $spec.Name)
        $previous = $at
        $checks.Add($check)
    }
    $excluded = [Collections.Generic.List[object]]::new()
    foreach ($entry in @(
        @{ Name = 'tools-initial.json'; Action = 'list'; Tool = $null; Replacement = 'tools-confirmed.json' },
        @{ Name = 'custom-baseline-problems.json'; Action = 'problems'; Tool = 'get_file_problems'; Replacement = 'custom-baseline-confirmed.json' }
    )) {
        $record = $records[$entry.Name]
        $at = Assert-OwnedRiderRecord $record $ready $port $entry.Action $entry.Tool $true
        Confirm-OwnedRiderEvidence ($at -lt (ConvertTo-OwnedRiderUtc $records[$entry.Replacement].CapturedUtc 'replacement capture')) 'chronology' 'failed wrapper must precede its confirmed replacement'
        $excluded.Add(@{ Name = $entry.Name; Accepted = $false; Reason = 'Historical wrapper failure, not successful evidence'; Failure = $record.Failure })
    }
    $pragma = $records['custom-error-pragma-problems.json']
    $at = Assert-OwnedRiderRecord $pragma $ready $port 'problems' 'get_file_problems'
    Confirm-OwnedRiderEvidence ($at -gt (ConvertTo-OwnedRiderUtc $records['custom-error-default-warning-lint.json'].CapturedUtc 'error-policy capture') -and
        $at -lt (ConvertTo-OwnedRiderUtc $records['custom-error-warning-restored-lint.json'].CapturedUtc 'warning-restored capture')) 'chronology' 'pragma observation must remain between Error policy and Warning restoration'
    $pragmaSpec = @{ Name = 'custom-error-pragma-problems.json'; Mode = 'problems'; C = @(8,9,10); D = $null; CA = @('using'); DA = @(); CE = 8; DE = 0 }
    $pragmaObservation = Assert-OwnedRiderSnapshot $pragma $ready $schemas.get_file_problems $pragmaSpec
    $pragmaObservation.Accepted = $false
    $pragmaObservation.Reason = 'MCP still reports Inject ERROR under pragma; native pragma/UI suppression was not verified'
    $excluded.Add($pragmaObservation)
    foreach ($entry in @($checks.ToArray()) + @($excluded.ToArray())) {
        $path = Join-Path $root $entry.Name
        $reference = @($Bundle.Sources | Where-Object { [string]::Equals($_.Path, $path, [StringComparison]::OrdinalIgnoreCase) })
        Confirm-OwnedRiderEvidence ($reference.Count -eq 1) 'schema' ('raw evidence reference missing: ' + $entry.Name)
        $entry.RawEvidence = $reference[0]
    }
    return [ordered]@{
        ProtocolSubsetPassed = $true; Scope = 'Saved Rider MCP protocol subset only; not full Rider, native UI/pragma, lifecycle, restart or Goal acceptance'
        FullRiderAcceptance = $false; NativePragmaVerified = $false; NativeUiVerified = $false; RestartVerified = $false; GoalPassed = $false
        Project = $project; Pid = $ready.Pid; StartUtc = $ready.StartUtc; Port = $port
        BaselineRestored = $true; ImmutableInputs = $Bundle.CurrentInputs; SolutionSha256 = $Bundle.SolutionSha256
        AnalyzerSha256 = $Bundle.AnalyzerSha256; PackageReferenceSha256 = $owner.PackageSha256; PackageArchiveRehashed = $false
        RecordedProcessOwnershipVerified = $true; LiveProcessOwnershipRechecked = $false
        RawDiagnosticIdFieldAvailable = $false; DiagnosticFamiliesInferredFromDescriptions = $true
        TextCopiesUsedAsFallback = $false; AnalysisReadyFlagNotUsedAsCompletion = $true
        ProjectModules = $graph.modules; Cases = $checks.ToArray(); NonAcceptanceEvidence = $excluded.ToArray()
        ToolDiscovery = @{ RawEvidencePath = (Join-Path $root 'tools-confirmed.json'); PayloadSource = 'Response.result.tools'; SelectedSchemas = $schemas }
        ProjectDiscovery = @{ RawEvidencePath = (Join-Path $root 'projects-initial.json'); PayloadSource = 'Response.result.structuredContent.modules' }
    }
}

function Read-OwnedRiderEvidenceBundle([string] $RiderRoot) {
    $root = ConvertTo-OwnedRiderPath $RiderRoot
    $sources = [Collections.Generic.List[object]]::new()
    $load = {
        param([string] $Path, [bool] $Json = $false, [bool] $Text = $false)
        for ($ancestor = $Path; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
            Confirm-OwnedRiderEvidence ((([IO.File]::GetAttributes($ancestor)) -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'ownership' ('linked input refused: ' + $ancestor)
        }
        $bytes = [IO.File]::ReadAllBytes($Path)
        $sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        $sources.Add(@{ Path = $Path; Sha256 = $sha256 })
        $textValue = if ($Json -or $Text) { [Text.UTF8Encoding]::new($false, $true).GetString($bytes).TrimStart([char]0xFEFF) } else { $null }
        $data = if ($Json) { ConvertFrom-Json -InputObject $textValue -AsHashtable -DateKind String } else { $null }
        return @{ Data = $data; Text = $(if ($Text) { $textValue } else { $null }); Sha256 = $sha256 }
    }
    $ready = (& $load (Join-Path $root 'ready.json') $true).Data
    $owner = (& $load (Join-Path ([IO.Path]::GetDirectoryName($root)) 'owner.json') $true).Data
    Assert-OwnedRiderKeys $ready @('Root', 'Project', 'Solution') 'ready'
    Assert-OwnedRiderKeys $owner @('Project') 'owner'
    $project = ConvertTo-OwnedRiderPath $owner.Project
    Confirm-OwnedRiderEvidence ([string]::Equals((ConvertTo-OwnedRiderPath $ready.Root), $root, [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals((ConvertTo-OwnedRiderPath $ready.Project), $project, [StringComparison]::OrdinalIgnoreCase)) 'ownership' 'input root/project binding differs'
    $solution = ConvertTo-OwnedRiderPath $ready.Solution
    Confirm-OwnedRiderEvidence ($solution.StartsWith($project + '\', [StringComparison]::OrdinalIgnoreCase)) 'ownership' 'solution escaped owned project'
    $solutionHash = (& $load $solution).Sha256
    $analyzerHash = (& $load (Join-Path $project 'Assets/YLFramework/Runtime/Core/Analyzers/YLFramework.Analyzers.dll')).Sha256
    $current = @(
        foreach ($relative in @(
            'Assets/AnalyzerConsumers/Analyzer.Acceptance.Consumer.asmdef',
            'Assets/AnalyzerConsumers/IdeDiagnosticProbe.cs', 'Assets/IdeDefaultProbe.cs',
            'Assets/AnalyzerConsumers/Analyzer.Acceptance.Consumer.ruleset', 'Assets/Assembly-CSharp.ruleset'
        )) {
            $path = Join-Path $project $relative
            $file = & $load $path
            $meta = & $load ($path + '.meta') $false $true
            $guids = [regex]::Matches($meta.Text, '(?m)^guid: ([a-f0-9]+)\r?$')
            Confirm-OwnedRiderEvidence ($guids.Count -eq 1) 'baseline' ('missing or ambiguous meta GUID: ' + $relative)
            @{ Path = $relative; Sha256 = $file.Sha256; MetaSha256 = $meta.Sha256; Guid = $guids[0].Groups[1].Value }
        }
    )
    $records = @{}
    foreach ($name in @(
        'tools-confirmed.json', 'projects-initial.json', 'custom-baseline-confirmed.json', 'default-baseline-problems.json',
        'custom-inject-fixed-problems.json', 'custom-all-fixed-problems.json', 'custom-positive-default-control-lint.json',
        'default-inject-fixed-problems.json', 'both-all-fixed-lint.json', 'restored-bad-both-lint.json',
        'custom-error-default-warning-lint.json', 'custom-error-warning-restored-lint.json', 'default-error-custom-warning-lint.json',
        'custom-none-default-warning-lint.json', 'custom-inherit-both-warning-lint.json',
        'tools-initial.json', 'custom-baseline-problems.json', 'custom-error-pragma-problems.json'
    )) {
        $records[$name] = (& $load (Join-Path $root $name) $true).Data
    }
    return @{ Ready = $ready; Owner = $owner; Records = $records; CurrentInputs = $current; SolutionSha256 = $solutionHash; AnalyzerSha256 = $analyzerHash; Sources = $sources.ToArray() }
}

if ($MyInvocation.InvocationName -eq '.') { return }
Confirm-OwnedRiderEvidence (-not [string]::IsNullOrWhiteSpace($RiderRoot) -and -not [string]::IsNullOrWhiteSpace($Run)) 'schema' 'explicit RiderRoot and unique Run are required'
$root = ConvertTo-OwnedRiderPath $RiderRoot
Confirm-OwnedRiderEvidence (Test-Path -LiteralPath $root -PathType Container) 'ownership' 'evidence root must exist'
for ($ancestor = $root; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
    Confirm-OwnedRiderEvidence ((([IO.File]::GetAttributes($ancestor)) -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'ownership' 'linked receipt root refused'
}
$destination = Join-Path $root ($Run + '.json')
$stream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$failure = $null
$sources = @()
$receipt = [ordered]@{
    Run = $Run; Kind = 'OwnedRiderSavedProtocolEvidence'; ProtocolSubsetPassed = $false
    FullRiderAcceptance = $false; NativePragmaVerified = $false; NativeUiVerified = $false; RestartVerified = $false; GoalPassed = $false
    LiveProcessOwnershipRechecked = $false; InputEvidenceUnchanged = $false; McpRequestsSent = 0; IdeStateChanged = $false
}
try {
    $bundle = Read-OwnedRiderEvidenceBundle -RiderRoot $root
    $sources = @($bundle.Sources)
    $receipt.Validation = Assert-OwnedRiderEvidenceBundle -Bundle $bundle -RiderRoot $root
    foreach ($source in $sources) {
        Confirm-OwnedRiderEvidence ((Get-FileHash -LiteralPath $source.Path -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $source.Sha256) 'baseline' ('input changed while checking: ' + $source.Path)
    }
    $receipt.InputEvidenceUnchanged = $true
    $receipt.ProtocolSubsetPassed = $true
} catch { $failure = $_.Exception.Message }
finally {
    $receipt.Sources = $sources
    $receipt.Failure = $failure
    $receipt.CapturedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
    try { $writer.Write((ConvertTo-Json -InputObject $receipt -Depth 60)) }
    finally { $writer.Dispose(); $stream.Dispose() }
}
[pscustomobject]@{
    ProtocolSubsetPassed = $receipt.ProtocolSubsetPassed; FullRiderAcceptance = $false
    Receipt = $destination; Sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
}
if ($null -ne $failure) { throw $failure }
