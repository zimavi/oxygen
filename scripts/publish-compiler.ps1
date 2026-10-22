# Publishes the compiler for Windows into <stage>\compiler. The compiler is published, not built, because its LLVM library is a native
# file that publish copies beside the executable.

[CmdletBinding()]
param([string]$Stage = "")

. (Join-Path $PSScriptRoot "common.ps1")

if ([string]::IsNullOrWhiteSpace($Stage)) {
    $Stage = Get-DefaultStage
}
$compilerDirectory = Join-Path $Stage "compiler"
# Publish keeps the files of an earlier publish, so the folder is rebuilt.
if (Test-Path -LiteralPath $compilerDirectory) {
    Remove-Item -LiteralPath $compilerDirectory -Recurse -Force
}

$project = Join-Path $RepositoryRoot "src/Oxygen.Cli/Oxygen.Cli.csproj"
dotnet publish $project --configuration Release --runtime $RuntimeIdentifier --self-contained false --output $compilerDirectory `
    --nologo --verbosity quiet -p:PublishAot=false
if ($LASTEXITCODE -ne 0) {
    throw "Publishing the compiler failed."
}

Write-Output "Published the compiler to $compilerDirectory"
