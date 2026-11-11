param(
    [switch]$SkipInstaller,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$dotnet = if (Test-Path ".tools\dotnet\dotnet.exe") {
    (Resolve-Path ".tools\dotnet\dotnet.exe").Path
} else {
    "dotnet"
}

& (Join-Path $PSScriptRoot "fetch-winrt.ps1")

$env:DOTNET_CLI_HOME = Join-Path $root ".tools\dotnet-home"
$env:NUGET_PACKAGES = Join-Path $root ".tools\nuget"
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES | Out-Null

# Folder-based deployment: no single-file extraction on every launch.
$publishDir = Join-Path $root "dist\app"
Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

$publishArgs = @(
    "publish", "src\BlueHarbor\BlueHarbor.csproj",
    "-c", "Release",
    "-o", $publishDir,
    "-p:PublishSingleFile=false",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "--nologo"
)

if (-not $FrameworkDependent) {
    $publishArgs += @("-r", "win-x64", "--self-contained", "true")
    $localSource = Join-Path $root ".tools\nuget-local"
    if (Test-Path $localSource) {
        $publishArgs += @("--source", $localSource)
    }
}

& $dotnet @publishArgs
Copy-Item "README.md" (Join-Path $publishDir "README.md") -Force
Copy-Item "LICENSE" (Join-Path $publishDir "LICENSE") -Force
New-Item -ItemType Directory -Force -Path (Join-Path $publishDir "docs") | Out-Null
Copy-Item "docs\screenshot.png" (Join-Path $publishDir "docs\screenshot.png") -Force

if (-not $SkipInstaller) {
    $makensis = if (Test-Path ".tools\nsis\nsis-3.11\makensis.exe") {
        (Resolve-Path ".tools\nsis\nsis-3.11\makensis.exe").Path
    } else {
        "makensis"
    }
    & $makensis "installer\BlueHarbor.nsi"
}

Write-Host "Done. Output: dist\app and dist\BlueHarbor-Setup-1.0.7.exe"
