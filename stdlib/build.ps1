[CmdletBinding()]
param(
    [string]$Target = "",
    [string]$Configuration = "Release",
    [string]$OutputDirectory = "",
    [switch]$Clean
)

# Builds the shared runtime, then the standard library as one shared library, into build/<triple>/ next to these sources:
#   oxygenstd.dll and its import library oxygenstd.lib on Windows, liboxygenstd.so elsewhere.
#
#   ./build.ps1                          the host target
#   ./build.ps1 -Target x86_64-pc-windows-msvc
#   ./build.ps1 -Clean                   removes the build directory
#
# The standard library is built with --no-std, since it is the library that the other programs load.

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$supportedTargets = @("x86_64-pc-windows-msvc", "x86_64-unknown-linux-gnu")

if ([string]::IsNullOrWhiteSpace($Target)) {
    $Target = if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
        "x86_64-pc-windows-msvc"
    } else {
        "x86_64-unknown-linux-gnu"
    }
}
if ($supportedTargets -notcontains $Target) {
    throw "Unsupported target '$Target'. Supported targets are: $($supportedTargets -join ', ')."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot "build"
}
$targetDirectory = Join-Path $OutputDirectory $Target

if ($Clean) {
    if (Test-Path -LiteralPath $OutputDirectory) {
        Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
    }
    Write-Output "Cleaned $OutputDirectory"
    exit 0
}

# The runtime comes first: the standard library links it, so its import library must exist.
& (Join-Path $repositoryRoot "runtime/build.ps1") -Target $Target
if ($LASTEXITCODE -ne 0) {
    throw "Building the runtime for $Target failed."
}
$runtimeLibrary = Join-Path $repositoryRoot "runtime/build/$Target/oxygenrt.lib"
if (-not (Test-Path -LiteralPath $runtimeLibrary)) {
    throw "The shared runtime was not produced at '$runtimeLibrary'."
}

# The compiler is published for the host, not built: the LLVM library is a native file for the host's runtime, and publish copies it
# beside the executable. The compiler runs on the host even when the standard library is built for another target.
$compilerProject = Join-Path $repositoryRoot "src/Oxygen.Cli/Oxygen.Cli.csproj"
$compilerDirectory = Join-Path $targetDirectory "compiler"
$hostRuntime = if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) { "win-x64" } else { "linux-x64" }
dotnet publish $compilerProject --configuration $Configuration --runtime $hostRuntime --self-contained false --output $compilerDirectory --nologo --verbosity quiet -p:PublishAot=false
if ($LASTEXITCODE -ne 0) {
    throw "Publishing the compiler failed."
}
$compiler = Join-Path $compilerDirectory "oxygenc.exe"

New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
$output = Join-Path $targetDirectory "oxygenstd"
$sources = Get-ChildItem -LiteralPath $PSScriptRoot -Filter "*.oxy" | ForEach-Object FullName
# --target takes the triple with underscores, as the CLI documents it.
$cliTarget = $Target.Replace("-", "_")

& $compiler build @sources --library --dynamic --no-std --target $cliTarget -r $runtimeLibrary -o $output
if ($LASTEXITCODE -ne 0) {
    throw "Building the standard library for $Target failed with exit code $LASTEXITCODE."
}

Write-Output "Built the standard library: $output.dll (import library $output.lib)"
