[CmdletBinding()]
param(
    [string]$Clang = "clang",
    [string]$Archiver = "",
    [string]$OutputDirectory = "",
    [ValidateSet("msvc", "gnu")]
    [string]$Format = ""
)

$ErrorActionPreference = "Stop"

$runtimeRoot = $PSScriptRoot
$source = Join-Path $runtimeRoot "src\main.c"
$includeDirectory = Join-Path $runtimeRoot "include"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $runtimeRoot "build"
}

if ([string]::IsNullOrWhiteSpace($Format)) {
    $Format = if ($IsWindows) { "msvc" } else { "gnu" }
}

if ([string]::IsNullOrWhiteSpace($Archiver)) {
    $archiveCandidates = if ($Format -eq "msvc") {
        @("llvm-lib", "lib", "llvm-ar", "ar")
    } else {
        @("llvm-ar", "ar")
    }

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
$archiverName = [IO.Path]::GetFileNameWithoutExtension($Archiver)
$isMsvc = $Format -eq "msvc"
$variants = @(
    @{ Suffix = ""; Flags = @("-O2") },
    @{ Suffix = "_debug"; Flags = @("-O0", "-g") }
)
$libraries = @()

foreach ($variant in $variants) {
    $objectExtension = if ($isMsvc) { ".obj" } else { ".o" }
    $libraryName = if ($isMsvc) { "oxygen_runtime$($variant.Suffix).lib" } else { "liboxygen_runtime$($variant.Suffix).a" }
    $object = Join-Path $OutputDirectory "oxygen_runtime$($variant.Suffix)$objectExtension"
    $library = Join-Path $OutputDirectory $libraryName

    & $Clang -std=c17 @($variant.Flags) -I $includeDirectory -c $source -o $object
    if ($LASTEXITCODE -ne 0) {
        throw "Clang failed to compile the Oxygen runtime variant '$($variant.Suffix)'."
    }

    if ($archiverName -in @("llvm-lib", "lib")) {
        & $Archiver /out:$library $object
    } else {
        & $Archiver rcs $library $object
    }

    if ($LASTEXITCODE -ne 0) {
        throw "The static-library archiver failed to create '$library'."
    }

    $libraries += $library
}

Write-Output $libraries
