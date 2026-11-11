param(
    [string]$Version = "10.0.22621.57"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root ".tools\winrt"
$dll = Join-Path $target "lib\net8.0\Microsoft.Windows.SDK.NET.dll"

if (Test-Path $dll) {
    Write-Host "WinRT projection already present: $dll"
    return
}

$packages = Join-Path $root ".tools\packages"
New-Item -ItemType Directory -Force -Path $packages | Out-Null
$nupkg = Join-Path $packages "microsoft.windows.sdk.net.ref.$Version.nupkg"

if (-not (Test-Path $nupkg)) {
    $url = "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.net.ref/$Version/microsoft.windows.sdk.net.ref.$Version.nupkg"
    Write-Host "Downloading $url"
    if (Get-Command node -ErrorAction SilentlyContinue) {
        node -e "const fs=require('fs');fetch(process.argv[1]).then(async r=>{if(!r.ok)throw Error(r.status);fs.writeFileSync(process.argv[2],Buffer.from(await r.arrayBuffer()))}).catch(e=>{console.error(e);process.exit(1)})" $url $nupkg
    }
    else {
        Invoke-WebRequest -Uri $url -OutFile $nupkg
    }
}

New-Item -ItemType Directory -Force -Path $target | Out-Null
Expand-Archive -Path $nupkg -DestinationPath $target -Force
Write-Host "WinRT projection ready: $target"
