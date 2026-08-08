param(
    [string]$AsfVersion = "6.3.8.4"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$libDir = Join-Path $root "lib"
New-Item -ItemType Directory -Force -Path $libDir | Out-Null

$archiDll = Join-Path $libDir "ArchiSteamFarm.dll"
if (-not (Test-Path $archiDll)) {
    Write-Host "Downloading ASF $AsfVersion generic build to obtain reference assemblies..."
    $zip = Join-Path $libDir "asf-generic.zip"
    Invoke-WebRequest -Uri "https://github.com/JustArchiNET/ArchiSteamFarm/releases/download/$AsfVersion/ASF-generic.zip" -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath (Join-Path $libDir "asf-generic") -Force
    Copy-Item (Join-Path $libDir "asf-generic\ArchiSteamFarm.dll") $archiDll
    Copy-Item (Join-Path $libDir "asf-generic\SteamKit2.dll") (Join-Path $libDir "SteamKit2.dll")
}

dotnet restore (Join-Path $root "AsfPlayStatus\AsfPlayStatus.csproj")
dotnet build (Join-Path $root "AsfPlayStatus\AsfPlayStatus.csproj") -c Release
Write-Host "Built: $(Join-Path $root 'AsfPlayStatus\bin\Release\net10.0\AsfPlayStatus.dll')"
