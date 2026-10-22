# Checks that the .NET 10 SDK, which the compiler projects target, is installed. It does not install it.

. (Join-Path $PSScriptRoot "common.ps1")

$hasSdk = $false
if ($null -ne (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $hasSdk = @(& dotnet --list-sdks | Where-Object { $_ -match '^10\.' }).Count -gt 0
}
if (-not $hasSdk) {
    throw "The .NET 10 SDK is required, and it was not found. Install it with: winget install Microsoft.DotNet.SDK.10"
}

Write-Output "Found the .NET 10 SDK."
