# Builds the standard library as a shared library, oxygenstd.dll with its import library oxygenstd.lib, with stdlib/build.ps1. That script builds the
# runtime it links and publishes its own copy of the compiler, so the compiler from publish-compiler is not used here.

. (Join-Path $PSScriptRoot "common.ps1")

& (Join-Path $RepositoryRoot "stdlib/build.ps1")

$libraryDirectory = Get-StandardLibraryDirectory
if (-not (Test-AllPresent @((Join-Path $libraryDirectory "oxygenstd.dll"), (Join-Path $libraryDirectory "oxygenstd.lib")))) {
    throw "The standard library for $Triple is incomplete in '$libraryDirectory'."
}

Write-Output "Built the standard library in $libraryDirectory"
