# Builds dist\AppLock-Setup-<version>.exe
# Needs the .NET 9 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

[xml]$proj = Get-Content AppLock.csproj
$version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

# Self-contained, so the setup works on PCs without .NET installed.
$build = Join-Path $PSScriptRoot 'installer\build'
if (Test-Path $build) { Remove-Item $build -Recurse -Force }
dotnet publish AppLock.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o $build
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

& $iscc "/DAppVersion=$version" installer\AppLock.iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
Get-Item "dist\AppLock-Setup-$version.exe"
