# Checks an installed prefix the way a user meets it: builds a program with the installed compiler from a folder of its own, with no
# OXYGEN_PATH, runs it, and checks its output and exit status. CI runs it after install.ps1.
#
#   scripts\check-installed.ps1 [-Prefix DIR]

[CmdletBinding()]
param([string]$Prefix = "")

. (Join-Path $PSScriptRoot "common.ps1")

if ([string]::IsNullOrWhiteSpace($Prefix)) {
    $Prefix = Get-DefaultPrefix
}
$Prefix = Get-FullPath $Prefix
$bin = Join-Path $Prefix "bin"

foreach ($file in @((Join-Path $bin "oxygenc.exe"), (Join-Path $bin "oxygenrt.dll"), (Join-Path $bin "oxygenstd.dll"),
        (Join-Path $Prefix (Join-Path "share" (Join-Path "oxygen" (Join-Path "stdlib" "io.oxy")))))) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "The installation is incomplete: '$file' is missing."
    }
}

$callerLocation = Get-Location
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("oxygen-check-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    Set-Location -LiteralPath $work
    # Written without a byte-order mark, which the lexer would not expect.
    $source = "import std.io;`n`ni32 main() {`n    io.println_char('i');`n    return 7;`n}`n"
    [System.IO.File]::WriteAllText((Join-Path $work "hello.oxy"), $source)

    # The compiler runs with no OXYGEN_PATH. Its output is read with stderr merged, so a warning is seen.
    $savedOxygenPath = $env:OXYGEN_PATH
    try {
        Remove-Item Env:OXYGEN_PATH -ErrorAction SilentlyContinue
        $build = & (Join-Path $bin "oxygenc.exe") build hello.oxy -o hello.exe 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            throw "Building with the installed compiler failed: $build"
        }
        if ($build -match 'warning') {
            throw "Building with the installed compiler printed a warning: $build"
        }
    } finally {
        if ($null -ne $savedOxygenPath) {
            $env:OXYGEN_PATH = $savedOxygenPath
        }
    }

    # The DLLs are found through PATH, which is changed for this process only.
    $savedPath = $env:PATH
    try {
        $env:PATH = "$bin;$savedPath"
        $output = & (Join-Path $work "hello.exe")
        $status = $LASTEXITCODE
    } finally {
        $env:PATH = $savedPath
    }
    if (($output | Out-String).Trim() -ne "i") {
        throw "The program printed '$output', expected 'i'."
    }
    if ($status -ne 7) {
        throw "The program exited with $status, expected 7."
    }
} finally {
    Set-Location -LiteralPath $callerLocation
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

# The test program'''s exit status was checked above. It must not stay in $LASTEXITCODE, since the PowerShell wrapper that GitHub Actions
# uses turns a non-zero value into a failed step.
$global:LASTEXITCODE = 0
Write-Output "The installation at $Prefix builds and runs a program from a separate folder."
