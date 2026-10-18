[CmdletBinding()]
param(
    [string]$Clang = "clang",
    [string]$Archiver = "",
    [string]$OutputDirectory = "",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$runtimeRoot = $PSScriptRoot
$sourceDirectory = Join-Path $runtimeRoot "src"
$includeDirectory = Join-Path $runtimeRoot "include"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $runtimeRoot "build"
}

if ($Clean) {
    if (Test-Path -LiteralPath $OutputDirectory) {
        Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
    }
    Write-Output "Cleaned $OutputDirectory"
    exit 0
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

foreach ($target in @(
    @{
        Name = "posix"
        LibraryName = "liboxygenrt.a"
        Flags = @()
    },
    @{
        Name = "windows"
        LibraryName = "liboxygenrt.a"
        Flags = @("--target=x86_64-pc-windows-msvc")
    }
)) {
    $targetDirectory = Join-Path $OutputDirectory $target.Name
    New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
    $library = Join-Path $targetDirectory $target.LibraryName
    $objects = @()

    foreach ($source in Get-ChildItem -Path $sourceDirectory -Filter "*.c" -File | Sort-Object Name) {
        $object = Join-Path $targetDirectory "$($source.BaseName).o"
        & $Clang @($target.Flags) -std=c17 -O2 -g -I $includeDirectory -c $source.FullName -o $object
        if ($LASTEXITCODE -ne 0) {
            throw "Clang failed to compile '$($source.Name)' for the $($target.Name) Oxygen runtime."
        }
        $objects += $object
    }

    & $Archiver rcs $library @objects
    if ($LASTEXITCODE -ne 0) {
        throw "The static-library archiver failed to create '$library'."
    }

    Write-Output $library
}
