# Settings shared by the install steps in this folder. Each step dot-sources this file, so any step runs alone.
# These scripts install the Windows toolchain. The Linux equivalents are the .sh files, since runtime/build.ps1 does not build the shared
# runtime for Linux.

$ErrorActionPreference = "Stop"

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw "These .ps1 scripts install the Windows toolchain. On Linux, use the .sh scripts in scripts/."
}

$RepositoryRoot = Split-Path -Parent $PSScriptRoot
$Triple = "x86_64-pc-windows-msvc"
$RuntimeIdentifier = "win-x64"

function Get-FullPath([string]$Path) {
    [System.IO.Path]::GetFullPath($Path)
}

function Get-DefaultPrefix {
    Join-Path $env:LOCALAPPDATA "Oxygen"
}

# The folder where publish-compiler puts the compiler, and where bundle reads it from. It is git-ignored.
function Get-DefaultStage {
    Join-Path (Join-Path $RepositoryRoot "artifacts") (Join-Path "install" "stage")
}

function Assert-Prefix([string]$Prefix) {
    $full = Get-FullPath $Prefix
    if ([System.IO.Path]::GetPathRoot($full) -eq $full) {
        throw "The prefix '$full' is a drive root. Choose a folder of its own."
    }
}

function Get-CompilerInPrefix([string]$Prefix) {
    Join-Path (Join-Path $Prefix "bin") "oxygenc.exe"
}

function Get-CompilerExecutable([string]$Stage) {
    Join-Path (Join-Path $Stage "compiler") "oxygenc.exe"
}

function Get-RuntimeDirectory {
    Join-Path (Join-Path (Join-Path $RepositoryRoot "runtime") "build") $Triple
}

function Get-StandardLibraryDirectory {
    Join-Path (Join-Path (Join-Path $RepositoryRoot "stdlib") "build") $Triple
}

# True when every path exists.
function Test-AllPresent([string[]]$Paths) {
    @($Paths | Where-Object { -not (Test-Path -LiteralPath $_) }).Count -eq 0
}
