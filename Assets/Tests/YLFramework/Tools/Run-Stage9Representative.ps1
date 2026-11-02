#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $DeliveryRoot,
    [string] $Receiver='ReceiverB',
    [ValidateSet('Mono','Il2Cpp')][string] $Backend='Mono',
    [string] $BuildRun='ugui1',
    [ValidatePattern('^[a-z0-9-]+$')][string] $Prefix='mono',
    [string] $ExistingSmokeRun='smoke4',
    [string] $SmokeStore='mono-first-4'
)
Set-StrictMode -Version 3.0
$ErrorActionPreference='Stop'
$runner=Join-Path $PSScriptRoot 'Invoke-Stage9Delivery.ps1'
$cases=@(
    @{ Run=$ExistingSmokeRun; Command='smoke'; Store=$SmokeStore; Expected=1 },
    @{ Run=($Prefix+'-update1'); Command='update'; Store=($Prefix+'-update'); Expected=1 },
    @{ Run=($Prefix+'-restart2'); Command='probe'; Store=($Prefix+'-update'); Expected=2 },
    @{ Run=($Prefix+'-rollback1'); Command='rollback'; Store=($Prefix+'-update'); Expected=2 },
    @{ Run=($Prefix+'-restart1'); Command='probe'; Store=($Prefix+'-update'); Expected=1 },
    @{ Run=($Prefix+'-encrypted'); Command='encrypted'; Store=($Prefix+'-encrypted'); Expected=1 },
    @{ Run=($Prefix+'-missing-key'); Command='missing-key'; Store=($Prefix+'-missing-key'); Expected=1 },
    @{ Run=($Prefix+'-sample'); Command='sample'; Store=($Prefix+'-sample'); Expected=1 }
)
foreach ($case in $cases) {
    $runRoot=Join-Path $DeliveryRoot ('Runs/'+$Receiver+'-'+$Backend+'-'+$case.Run)
    $operation=if (Test-Path -LiteralPath $runRoot) { 'RecoverPlayer' } else { 'Player' }
    & $runner -DeliveryRoot $DeliveryRoot -Receiver $Receiver -Operation $operation -Backend $Backend -BuildRun $BuildRun `
        -Run $case.Run -Command $case.Command -Store $case.Store -ExpectedVersion $case.Expected
}
'All representative cases collected from the same immutable build; historical failures remain preserved.'
