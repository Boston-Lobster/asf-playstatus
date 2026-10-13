param(
    [string]$AsfVersion = "6.3.8.4"
)

$ErrorActionPreference = "Stop"
if ($AsfVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "AsfVersion must be a four-part release version, for example 6.3.8.4"
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$libDir = Join-Path $root "lib"
$referenceDir = Join-Path $libDir "asf-$AsfVersion"
$downloadDir = Join-Path $libDir "asf-$AsfVersion-download"
$zip = Join-Path $libDir "ASF-generic-$AsfVersion.zip"
New-Item -ItemType Directory -Force -Path $libDir | Out-Null

$archiDll = Join-Path $referenceDir "ArchiSteamFarm.dll"
$steamKitDll = Join-Path $referenceDir "SteamKit2.dll"
$cachedVersion = $null
if ((Test-Path -LiteralPath $archiDll) -and (Test-Path -LiteralPath $steamKitDll)) {
    try {
        $cachedVersion = [System.Reflection.AssemblyName]::GetAssemblyName($archiDll).Version.ToString()
    } catch {
        $cachedVersion = $null
    }
}

if (($cachedVersion -ne $AsfVersion) -or -not (Test-Path -LiteralPath $steamKitDll)) {
    Write-Host "Downloading ASF $AsfVersion generic build to obtain reference assemblies..."
    Invoke-WebRequest -Uri "https://github.com/JustArchiNET/ArchiSteamFarm/releases/download/$AsfVersion/ASF-generic.zip" -OutFile $zip -UseBasicParsing

    Expand-Archive -Path $zip -DestinationPath $downloadDir -Force

    $downloadedArchiDll = Join-Path $downloadDir "ArchiSteamFarm.dll"
    $downloadedSteamKitDll = Join-Path $downloadDir "SteamKit2.dll"
    if (-not (Test-Path -LiteralPath $downloadedArchiDll) -or -not (Test-Path -LiteralPath $downloadedSteamKitDll)) {
        throw "ASF $AsfVersion package did not contain the expected reference assemblies"
    }

    $downloadedVersion = [System.Reflection.AssemblyName]::GetAssemblyName($downloadedArchiDll).Version.ToString()
    if ($downloadedVersion -ne $AsfVersion) {
        throw "Downloaded ASF assembly version $downloadedVersion does not match requested version $AsfVersion"
    }

    New-Item -ItemType Directory -Force -Path $referenceDir | Out-Null
    Copy-Item -LiteralPath $downloadedArchiDll -Destination $archiDll -Force
    Copy-Item -LiteralPath $downloadedSteamKitDll -Destination $steamKitDll -Force
}

$finalVersion = [System.Reflection.AssemblyName]::GetAssemblyName($archiDll).Version.ToString()
if (($finalVersion -ne $AsfVersion) -or -not (Test-Path -LiteralPath $steamKitDll)) {
    throw "Cached references do not match ASF $AsfVersion"
}

$csproj = Join-Path $root "AsfPlayStatus\AsfPlayStatus.csproj"
dotnet restore $csproj "-p:AsfReferenceVersion=$AsfVersion"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE"
}

dotnet build $csproj -c Release "-p:AsfReferenceVersion=$AsfVersion"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

Write-Host "Built: $(Join-Path $root 'AsfPlayStatus\bin\Release\net10.0\AsfPlayStatus.dll')"
