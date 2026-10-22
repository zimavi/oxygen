# Installs the Oxygen toolchain for Windows into a prefix (default %LOCALAPPDATA%\Oxygen). It runs the steps in scripts/ in order:
# check-dotnet, publish-compiler, build-runtime, build-stdlib and bundle, and path when -AddToPath is given.
#
#   scripts\install.ps1                        the default steps
#   scripts\install.ps1 -AddToPath             also put the prefix's bin folder on the user PATH
#   scripts\install.ps1 -Only bundle,path      run only the named steps
#   scripts\install.ps1 -SkipBuild             reuse the outputs that already exist, and only copy them
#   scripts\install.ps1 -Update                rebuild the installation that is already at the prefix, and fail when there is none
#
# Each step is also a script of its own, so any step can be rerun alone.

[CmdletBinding()]
param(
    [string]$Prefix = "",
    [string]$Stage = "",
    [string[]]$Only = @(),
    [switch]$SkipBuild,
    [switch]$AddToPath,
    [switch]$Update
)

. (Join-Path $PSScriptRoot "common.ps1")

$allSteps = @("check-dotnet", "publish-compiler", "build-runtime", "build-stdlib", "bundle", "path")
if ([string]::IsNullOrWhiteSpace($Prefix)) {
    $Prefix = Get-DefaultPrefix
}
if ([string]::IsNullOrWhiteSpace($Stage)) {
    $Stage = Get-DefaultStage
}
Assert-Prefix $Prefix
$Prefix = Get-FullPath $Prefix
$Stage = Get-FullPath $Stage

$steps = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ -ne '' })
foreach ($step in $steps) {
    if ($allSteps -notcontains $step) {
        throw "Unknown step '$step'. The steps are: $($allSteps -join ', ')."
    }
}
if ($steps.Count -eq 0) {
    $steps = @("check-dotnet", "publish-compiler", "build-runtime", "build-stdlib", "bundle")
    if ($AddToPath) {
        $steps += "path"
    }
}
# The steps run in pipeline order, whatever order -Only names them in.
$steps = @($allSteps | Where-Object { $steps -contains $_ })

# The version of the compiler installed at the prefix, or nothing when there is none.
function Get-InstalledVersion {
    $compiler = Get-CompilerInPrefix $Prefix
    if (Test-Path -LiteralPath $compiler) {
        & $compiler --version | Select-Object -First 1
    }
}

$oldVersion = $null
if ($Update) {
    if (-not (Test-Path -LiteralPath (Get-CompilerInPrefix $Prefix))) {
        throw "No installation at '$Prefix' to update. Run without -Update to install there."
    }
    if ($SkipBuild) {
        throw "-Update rebuilds every step, so it cannot be combined with -SkipBuild."
    }
    $oldVersion = Get-InstalledVersion
}

# Whether a step's outputs already exist, which -SkipBuild reuses instead of building them again.
function Test-StepOutput([string]$Step) {
    switch ($Step) {
        "publish-compiler" { return Test-Path -LiteralPath (Get-CompilerExecutable $Stage) }
        "build-runtime" {
            $runtime = Get-RuntimeDirectory
            return Test-AllPresent @((Join-Path $runtime "oxygenrt.dll"), (Join-Path $runtime "oxygenrt.lib"), (Join-Path $runtime "liboxygenrt.a"))
        }
        "build-stdlib" {
            $library = Get-StandardLibraryDirectory
            return Test-AllPresent @((Join-Path $library "oxygenstd.dll"), (Join-Path $library "oxygenstd.lib"))
        }
        default { return $false }
    }
}

foreach ($step in $steps) {
    if ($SkipBuild -and (Test-StepOutput $step)) {
        Write-Output "Skipping ${step}: its outputs exist."
        continue
    }
    Write-Output "== $step"
    switch ($step) {
        "check-dotnet" { & (Join-Path $PSScriptRoot "check-dotnet.ps1") }
        "publish-compiler" { & (Join-Path $PSScriptRoot "publish-compiler.ps1") -Stage $Stage }
        "build-runtime" { & (Join-Path $PSScriptRoot "build-runtime.ps1") }
        "build-stdlib" { & (Join-Path $PSScriptRoot "build-stdlib.ps1") }
        "bundle" { & (Join-Path $PSScriptRoot "bundle.ps1") -Prefix $Prefix -Stage $Stage }
        "path" { & (Join-Path $PSScriptRoot "path.ps1") -Prefix $Prefix }
    }
}

if ($steps -contains "bundle") {
    if ($Update) {
        $oldText = if ($oldVersion) { $oldVersion } else { "an unknown version" }
        Write-Output "Updated Oxygen in $Prefix from $oldText to $(Get-InstalledVersion)."
    } else {
        Write-Output "Installed Oxygen to $Prefix. Programs need $(Join-Path $Prefix 'bin') on PATH to run; use -AddToPath, or add it yourself."
    }
}
