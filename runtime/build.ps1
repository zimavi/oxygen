[CmdletBinding()]
param(
    [string]$Clang = "clang",
    [string]$Archiver = "",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

$runtimeRoot = $PSScriptRoot
$sourceDirectory = Join-Path $runtimeRoot "src"
$includeDirectory = Join-Path $runtimeRoot "include"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $runtimeRoot "build"
}

if ([string]::IsNullOrWhiteSpace($Archiver)) {
    $archiveCandidates = @("llvm-ar", "ar")

    foreach ($candidate in $archiveCandidates) {
        if (Get-Command $candidate -ErrorAction SilentlyContinue) {
            $Archiver = $candidate
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($Archiver)) {
    throw "No compatible static-library archiver was found. Install llvm-lib, llvm-ar, or ar, or pass -Archiver explicitly."
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$library = Join-Path $OutputDirectory "liboxygenrt.a"
$objects = @()

foreach ($source in Get-ChildItem -Path $sourceDirectory -Filter "*.c" -File | Sort-Object Name) {
    $object = Join-Path $OutputDirectory "$($source.BaseName).o"
    & $Clang -std=c17 -O2 -g -I $includeDirectory -c $source.FullName -o $object
    if ($LASTEXITCODE -ne 0) {
        throw "Clang failed to compile '$($source.Name)' for the Oxygen runtime."
    }
    $objects += $object
}

& $Archiver rcs $library @objects
if ($LASTEXITCODE -ne 0) {
    throw "The static-library archiver failed to create '$library'."
}

Write-Output $library
