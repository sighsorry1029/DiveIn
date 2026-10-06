#Requires -Version 7.0
# One-time binary compatibility repair, not a build/runtime hook.
# Obtain the original input from git before this repair when reproducing it.
param(
    [Parameter(Mandatory)][string] $InputDll,
    [Parameter(Mandatory)][string] $OutputDll,
    [Parameter(Mandatory)][string] $GameDll,
    [Parameter(Mandatory)][string] $CecilDll
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$expectedHash = '166956302A294E224474B26F4C7D58409084AD3F48BD0AF1FEB7551F229C8F60'
if ((Get-FileHash -LiteralPath $InputDll).Hash -ne $expectedHash) {
    throw 'Unexpected ServerSync input; review the library before updating this repair.'
}
$outputPath = [IO.Path]::GetFullPath($OutputDll)
if ($outputPath -eq [IO.Path]::GetFullPath($InputDll) -or $outputPath -eq [IO.Path]::GetFullPath($GameDll)) {
    throw 'Output must be a separate file. Original game and library inputs must be preserved.'
}
Add-Type -Path $CecilDll
if ([Mono.Cecil.ModuleDefinition].Assembly.GetName().Version.ToString() -ne '0.11.6.0') {
    throw 'Use Mono.Cecil 0.11.6 for a reproducible repair.'
}
$game = [Mono.Cecil.ModuleDefinition]::ReadModule($GameDll)
$library = [Mono.Cecil.ModuleDefinition]::ReadModule($InputDll)
try {
    $field = $game.GetType('ZRoutedRpc').Fields | Where-Object Name -eq 'Everybody'
    if (!$field.IsLiteral -or $field.FieldType.FullName -ne 'System.Int64' -or [long]$field.Constant -ne 0) {
        throw 'Expected Valheim 1.0.7 const Int64 ZRoutedRpc.Everybody = 0.'
    }
    $count = 0
    foreach ($type in $library.GetTypes()) {
        foreach ($method in $type.Methods) {
            if (!$method.HasBody) { continue }
            foreach ($instruction in $method.Body.Instructions) {
                $operand = $instruction.Operand -as [Mono.Cecil.FieldReference]
                if (!$operand -or $operand.FullName -ne 'System.Int64 ZRoutedRpc::Everybody') { continue }
                if ($operand.DeclaringType.Scope.Name -ne 'assembly_valheim' -or $instruction.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Ldsfld) {
                    throw 'Unexpected Everybody access.'
                }
                $instruction.OpCode = [Mono.Cecil.Cil.OpCodes]::Ldc_I8
                $instruction.Operand = [long]$field.Constant
                $count++
            }
        }
    }
    if ($count -ne 3) { throw "Expected exactly 3 field reads, found $count." }
    $writer = [Mono.Cecil.WriterParameters]::new()
    $writer.DeterministicMvid = $true
    $library.Write($outputPath, $writer)
    Write-Host "Repaired $count ServerSync constant reads without changing RPC or configuration policy."
} finally {
    $library.Dispose()
    $game.Dispose()
}
