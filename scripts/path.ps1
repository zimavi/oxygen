# Adds the prefix's bin folder to the user PATH, once. A program built with the installed compiler finds oxygenrt.dll and oxygenstd.dll through
# PATH, so the folder must be on it for the program to run. Terminals that are already open keep their old PATH.

[CmdletBinding(SupportsShouldProcess)]
param([string]$Prefix = "")

. (Join-Path $PSScriptRoot "common.ps1")

if ([string]::IsNullOrWhiteSpace($Prefix)) {
    $Prefix = Get-DefaultPrefix
}
Assert-Prefix $Prefix
$bin = (Join-Path (Get-FullPath $Prefix) "bin").TrimEnd('\')

$entries = @([Environment]::GetEnvironmentVariable("Path", "User") -split ';' | Where-Object { $_.Trim() -ne '' })
if (@($entries | Where-Object { $_.TrimEnd('\') -ieq $bin }).Count -gt 0) {
    Write-Output "$bin is already on the user PATH."
    return
}

if ($PSCmdlet.ShouldProcess("user PATH", "add $bin")) {
    [Environment]::SetEnvironmentVariable("Path", (@($entries) + $bin) -join ';', "User")
    Write-Output "Added $bin to the user PATH. Open a new terminal to use it."
}
