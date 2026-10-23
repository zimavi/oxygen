# Installs, updates or uninstalls Oxygen, from the archive that this script came in. The archive keeps bin, lib and share beside this
# script. Install copies them into a prefix, which is %LOCALAPPDATA%\Oxygen by default, and copies this script to share\oxygen, so the
# prefix can be uninstalled after the archive is deleted. It does not build anything, so it needs no sources or toolchain.
#
#   .\install.ps1 [-Prefix DIR] [-Update] [-AddToPath]
#   <prefix>\share\oxygen\install.ps1 -Uninstall [-Prefix DIR]
#
# Install refuses a prefix that already has an installation. -Update replaces an installation, and prints the version it replaced.
# -Uninstall removes what install put there: the folders it owns, and the bin folder's entry on the user PATH. Other files in the prefix
# are kept. -AddToPath adds the prefix's bin folder to the user PATH, which programs need to run, since oxygenrt.dll and oxygenstd.dll
# are found through PATH.

[CmdletBinding()]
param(
    [string]$Prefix = "",
    [switch]$Update,
    [switch]$AddToPath,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

if ($Uninstall -and ($Update -or $AddToPath)) {
    throw "-Uninstall cannot be combined with -Update or -AddToPath."
}
if ([string]::IsNullOrWhiteSpace($Prefix)) {
    $Prefix = Join-Path $env:LOCALAPPDATA "Oxygen"
}
$Prefix = [System.IO.Path]::GetFullPath($Prefix)
if ([System.IO.Path]::GetPathRoot($Prefix) -eq $Prefix) {
    throw "The prefix '$Prefix' is a drive root. Choose a folder of its own."
}

# What an installation owns. Only these are replaced or removed.
$owned = @("bin", "lib", "share\oxygen\stdlib", "share\oxygen\install.ps1")
$bin = Join-Path $Prefix "bin"
$compiler = Join-Path $bin "oxygenc.exe"

# Removes a folder only when nothing is in it, so the folders that hold other files stay.
function Remove-FolderIfEmpty([string]$Folder) {
    if ((Test-Path -LiteralPath $Folder) -and -not (Get-ChildItem -LiteralPath $Folder -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $Folder
    }
}

# Removes the bin folder from the user PATH, if it is there.
function Remove-FromUserPath {
    $entries = @([Environment]::GetEnvironmentVariable("Path", "User") -split ';' | Where-Object { $_.Trim() -ne '' })
    $kept = @($entries | Where-Object { $_.TrimEnd('\') -ine $bin })
    if ($kept.Count -ne $entries.Count) {
        [Environment]::SetEnvironmentVariable("Path", $kept -join ';', "User")
        Write-Output "Removed $bin from the user PATH."
    }
}

if ($Uninstall) {
    if (-not (Test-Path -LiteralPath $compiler)) {
        throw "No Oxygen installation at $Prefix to uninstall."
    }
    Remove-FromUserPath
    foreach ($part in $owned) {
        $path = Join-Path $Prefix $part
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
    # The folders that the installation made are removed when they are empty, deepest first.
    foreach ($folder in @((Join-Path $Prefix "share\oxygen"), (Join-Path $Prefix "share"), $bin, (Join-Path $Prefix "lib"), $Prefix)) {
        Remove-FolderIfEmpty $folder
    }
    if (Test-Path -LiteralPath $Prefix) {
        Write-Output "Uninstalled Oxygen from $Prefix. Other files in it were kept."
    } else {
        Write-Output "Uninstalled Oxygen from $Prefix."
    }
    $global:LASTEXITCODE = 0
    return
}

$here = $PSScriptRoot
foreach ($part in @("bin", "lib", "share\oxygen\stdlib", "install.ps1")) {
    if (-not (Test-Path -LiteralPath (Join-Path $here $part))) {
        throw "The archive is incomplete: '$(Join-Path $here $part)' is missing. Run this script from the unpacked archive."
    }
}

$oldVersion = $null
if (Test-Path -LiteralPath $compiler) {
    if (-not $Update) {
        throw "$Prefix already has an installation. Use -Update to replace it."
    }
    $oldVersion = & $compiler --version | Select-Object -First 1
} elseif ($Update) {
    throw "No installation at $Prefix to update. Run without -Update to install there."
}

foreach ($part in $owned) {
    $path = Join-Path $Prefix $part
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
New-Item -ItemType Directory -Force -Path $bin, (Join-Path $Prefix "lib"), (Join-Path $Prefix "share\oxygen\stdlib") | Out-Null
Copy-Item -Path (Join-Path $here "bin\*") -Destination $bin -Recurse -Force
Copy-Item -Path (Join-Path $here "lib\*") -Destination (Join-Path $Prefix "lib") -Recurse -Force
Copy-Item -Path (Join-Path $here "share\oxygen\stdlib\*") -Destination (Join-Path $Prefix "share\oxygen\stdlib") -Recurse -Force
Copy-Item -LiteralPath (Join-Path $here "install.ps1") -Destination (Join-Path $Prefix "share\oxygen\install.ps1")

if ($AddToPath) {
    $entries = @([Environment]::GetEnvironmentVariable("Path", "User") -split ';' | Where-Object { $_.Trim() -ne '' })
    if (@($entries | Where-Object { $_.TrimEnd('\') -ieq $bin }).Count -eq 0) {
        [Environment]::SetEnvironmentVariable("Path", (@($entries) + $bin) -join ';', "User")
        Write-Output "Added $bin to the user PATH. Open a new terminal to use it."
    }
}

# A native exit status from the compiler above must not stay in $LASTEXITCODE, since the PowerShell wrapper that GitHub Actions uses
# turns it into a failure.
$global:LASTEXITCODE = 0
if ($Update) {
    $oldText = if ($oldVersion) { $oldVersion } else { "an unknown version" }
    $newVersion = & $compiler --version | Select-Object -First 1
    $global:LASTEXITCODE = 0
    Write-Output "Updated Oxygen in $Prefix from $oldText to $newVersion."
} else {
    Write-Output "Installed Oxygen to $Prefix. Put $bin on PATH to use oxygenc, or run this script with -AddToPath."
}
