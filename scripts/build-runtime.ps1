# Builds the runtime for Windows: the static archive and the shared runtime, which runtime/build.ps1 builds for the host.

. (Join-Path $PSScriptRoot "common.ps1")

& (Join-Path $RepositoryRoot "runtime/build.ps1")

$runtimeDirectory = Get-RuntimeDirectory
if (-not (Test-AllPresent @((Join-Path $runtimeDirectory "oxygenrt.dll"), (Join-Path $runtimeDirectory "oxygenrt.lib"), (Join-Path $runtimeDirectory "liboxygenrt.a")))) {
    throw "The runtime for $Triple is incomplete in '$runtimeDirectory'."
}

Write-Output "Built the runtime in $runtimeDirectory"
