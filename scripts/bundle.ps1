# Copies the built toolchain into the prefix. It replaces the prefix's bin, lib and share\oxygen\stdlib folders and leaves other files
# in the prefix alone.

[CmdletBinding()]
param(
    [string]$Prefix = "",
    [string]$Stage = ""
)

. (Join-Path $PSScriptRoot "common.ps1")

if ([string]::IsNullOrWhiteSpace($Prefix)) {
    $Prefix = Get-DefaultPrefix
}
if ([string]::IsNullOrWhiteSpace($Stage)) {
    $Stage = Get-DefaultStage
}
Assert-Prefix $Prefix
$Prefix = Get-FullPath $Prefix
$Stage = Get-FullPath $Stage

$compiler = Get-CompilerExecutable $Stage
$runtimeDirectory = Get-RuntimeDirectory
$libraryDirectory = Get-StandardLibraryDirectory
$sources = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot "stdlib") -Filter "*.oxy" -File)

$required = @($compiler,
    (Join-Path $runtimeDirectory "oxygenrt.dll"), (Join-Path $runtimeDirectory "oxygenrt.lib"), (Join-Path $runtimeDirectory "liboxygenrt.a"),
    (Join-Path $libraryDirectory "oxygenstd.dll"), (Join-Path $libraryDirectory "oxygenstd.lib"))
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath $_) })
if ($missing.Count -gt 0) {
    throw "Missing: $($missing -join ', '). Run publish-compiler, build-runtime and build-stdlib first."
}
if ($sources.Count -eq 0) {
    throw "The standard library has no sources in '$(Join-Path $RepositoryRoot 'stdlib')'."
}

# The DLLs go in bin, where Windows finds them through PATH. The import libraries go in lib, for the linker.
$bin = Join-Path $Prefix "bin"
$lib = Join-Path $Prefix "lib"
$share = Join-Path $Prefix (Join-Path "share" (Join-Path "oxygen" "stdlib"))
foreach ($folder in @($bin, $lib, $share)) {
    if (Test-Path -LiteralPath $folder) {
        Remove-Item -LiteralPath $folder -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
}

Copy-Item -Path (Join-Path (Split-Path -Parent $compiler) "*") -Destination $bin -Recurse -Force
Copy-Item -LiteralPath (Join-Path $runtimeDirectory "oxygenrt.dll") -Destination $bin
Copy-Item -LiteralPath (Join-Path $libraryDirectory "oxygenstd.dll") -Destination $bin
Copy-Item -LiteralPath (Join-Path $runtimeDirectory "oxygenrt.lib"), (Join-Path $runtimeDirectory "liboxygenrt.a"), (Join-Path $libraryDirectory "oxygenstd.lib") -Destination $lib
Copy-Item -LiteralPath @($sources.FullName) -Destination $share

Write-Output "Installed the toolchain to $Prefix"
