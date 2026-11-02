#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([Parameter(Mandatory)][string] $TemporaryRoot)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Process.ps1')
. (Join-Path $PSScriptRoot '../Common/Results.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$archive = Join-Path $repository 'Temp/Extensions.Data.Excel/Dependency/miniexcel.1.31.2.nupkg'
$license = Join-Path $repository 'Temp/Extensions.Data.Excel/Dependency/MiniExcel-LICENSE.txt'
if (-not [IO.File]::Exists($archive) -or -not [IO.File]::Exists($license)) { throw 'Restore-MiniExcel.ps1 -DownloadOnly must supply the pinned upstream inputs first.' }
$root = Join-Path (Get-YLTestPath $TemporaryRoot) ('yl-miniexcel-' + [Guid]::NewGuid().ToString('N'))
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot $root -Repository $repository
$checks = 0
try {
    foreach ($fault in @('none', 'guid', 'editor', 'manifest')) {
        $cache = New-YLTestDirectory -Workspace $workspace -Kind Cache -Key $fault
        $ownedRepository = Join-Path $cache.path 'Repository'
        $tool = Join-Path $ownedRepository 'Tools/Extensions/Data/Excel/Restore-MiniExcel.ps1'
        $package = Join-Path $ownedRepository 'PackageSources/com.kano.ylframework.excel'
        $meta = Join-Path $package 'Editor/Plugins/MiniExcel.dll.meta'
        $old = Join-Path $ownedRepository 'Assets/YLFramework/UserData.txt'
        foreach ($path in @($tool, $meta, $old)) { $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) }
        [IO.File]::Copy((Join-Path $repository 'Tools/Extensions/Data/Excel/Restore-MiniExcel.ps1'), $tool)
        [IO.File]::WriteAllText($old, 'preserved old local directory')
        [IO.File]::WriteAllText((Join-Path $package 'package.json'), ('{"name":"' + $(if ($fault -ceq 'manifest') { 'com.unrelated.package' } else { 'com.kano.ylframework.excel' }) + '"}'))
        $text = [IO.File]::ReadAllText((Join-Path $repository 'PackageSources/com.kano.ylframework.excel/Editor/Plugins/MiniExcel.dll.meta'))
        if ($fault -ceq 'guid') { $text = $text.Replace('c7540153c69cf324883a3adbf1efeeb3', '00000000000000000000000000000000') }
        if ($fault -ceq 'editor') { $text = $text.Replace('enabled: 1', 'enabled: 0') }
        [IO.File]::WriteAllText($meta, $text)
        $metaHash = (Get-FileHash -LiteralPath $meta).Hash
        $dependency = Join-Path $ownedRepository 'Temp/Extensions.Data.Excel/Dependency'
        $null = [IO.Directory]::CreateDirectory($dependency)
        [IO.File]::Copy($archive, (Join-Path $dependency 'miniexcel.1.31.2.nupkg'))
        [IO.File]::Copy($license, (Join-Path $dependency 'MiniExcel-LICENSE.txt'))
        $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
        $start.UseShellExecute = $false; $start.WorkingDirectory = $ownedRepository
        foreach ($arg in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $tool)) { $start.ArgumentList.Add($arg) }
        $fact = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $cache.path 'restore.log') -TimeoutMilliseconds 60000
        if ($fault -ceq 'none') {
            Assert-YLTestProcessSucceeded $fact
            $actual = (Get-FileHash -LiteralPath (Join-Path $package 'Editor/Plugins/MiniExcel.dll')).Hash
            $expected = (Get-FileHash -LiteralPath (Join-Path $repository 'PackageSources/com.kano.ylframework.excel/Editor/Plugins/MiniExcel.dll')).Hash
            if ($actual -cne $expected) { throw 'The maintained package did not receive the pinned SDK bytes.' }
        } elseif ($fact.exitCode -eq 0 -or [IO.File]::Exists((Join-Path $package 'Editor/Plugins/MiniExcel.dll'))) {
            throw "Invalid restore identity was accepted: $fault"
        }
        if ((Get-FileHash -LiteralPath $meta).Hash -cne $metaHash -or [IO.File]::ReadAllText($old) -cne 'preserved old local directory') { throw 'Restore modified an existing metadata identity or old user directory.' }
        if ([IO.File]::Exists((Join-Path $ownedRepository 'Assets/YLFramework/Integrations/Excel/Editor/Plugins/MiniExcel.dll'))) { throw 'Restore recreated the retired source tree.' }
        $checks++
    }
    "PASS: $checks pinned MiniExcel restore, package identity and metadata-preservation contracts."
} finally {
    if ([IO.File]::Exists((Join-Path $root '.yl-workspace.json'))) { [IO.Directory]::Delete($root, $true) }
}
