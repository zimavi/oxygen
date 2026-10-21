[CmdletBinding()]
param(
    [string]$Clang = "clang",
    [string]$Archiver = "",
    [string]$OutputDirectory = "",
    [string[]]$Target = @(),
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$runtimeRoot = $PSScriptRoot
$sourceDirectory = Join-Path $runtimeRoot "src"
$includeDirectory = Join-Path $runtimeRoot "include"
$supportedTargets = @("x86_64-pc-windows-msvc", "x86_64-unknown-linux-gnu")

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

# With no -Target, the archive is built for the host. A cross build needs the target's C headers, which the host may not have.
if ($Target.Count -eq 0) {
    $Target = @(
        if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
            "x86_64-pc-windows-msvc"
        } else {
            "x86_64-unknown-linux-gnu"
        })
}

foreach ($triple in $Target) {
    if ($supportedTargets -notcontains $triple) {
        throw "Unsupported runtime target '$triple'. Supported targets are: $($supportedTargets -join ', ')."
    }
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

# Each target has its own directory, runtime/build/<triple>/, which the CLI reads by target.
foreach ($triple in $Target) {
    $targetDirectory = Join-Path $OutputDirectory $triple
    New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
    $library = Join-Path $targetDirectory "liboxygenrt.a"
    $objects = @()

    foreach ($source in Get-ChildItem -Path $sourceDirectory -Filter "*.c" -File | Sort-Object Name) {
        $object = Join-Path $targetDirectory "$($source.BaseName).o"
        # -fPIC is an ELF option: COFF code is position independent already, and clang rejects the flag for the Windows target.
        $pic = if ($triple -like "*-windows-*") { @() } else { @("-fPIC") }
        $clangArguments = @("--target=$triple", "-std=c17", "-O2", "-g") + $pic +
            @("-Wall", "-Wextra", "-I", $includeDirectory, "-c", $source.FullName, "-o", $object)
        & $Clang @clangArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Clang failed to compile '$($source.Name)' for the $triple Oxygen runtime."
        }
        $objects += $object
    }

    & $Archiver rcs $library @objects
    if ($LASTEXITCODE -ne 0) {
        throw "The static-library archiver failed to create '$library'."
    }

    Write-Output $library
}
