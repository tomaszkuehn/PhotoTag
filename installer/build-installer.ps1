# Buduje instalator PhotoTag: publikuje program (Release, win-x64, jeden plik z wbudowanym .NET)
# i kompiluje installer\PhotoTag.iss. Wynik: installer\output\PhotoTag-<wersja>-setup.exe
# Wymaga Inno Setup 6 (winget install JRSoftware.InnoSetup).

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'publish'

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Nie znaleziono Inno Setup 6 (ISCC.exe). Zainstaluj: winget install JRSoftware.InnoSetup' }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'PhotoTag.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -p:GenerateDocumentationFile=false -o $publish -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish nie powiódł się' }
Get-ChildItem $publish -Filter *.xml | Remove-Item

# wersja z <Version> w PhotoTag.csproj (bez czwartego członu)
$version = ([xml](Get-Content (Join-Path $root 'PhotoTag.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

# Podpis cyfrowy (opcjonalny): ustaw odcisk certyfikatu do podpisywania kodu z magazynu Windows
# (także z tokenu/karty, np. Certum), np.:  $env:PHOTOTAG_SIGN_THUMBPRINT = 'AB12…'
$isccArgs = @("/DAppVersion=$version")
$thumb = $env:PHOTOTAG_SIGN_THUMBPRINT
if ($thumb) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { throw 'Nie znaleziono signtool.exe (Windows SDK).' }
    $timestamp = if ($env:PHOTOTAG_TIMESTAMP_URL) { $env:PHOTOTAG_TIMESTAMP_URL } else { 'http://time.certum.pl' }
    $signCmd = "`"$signtool`" sign /sha1 $thumb /fd sha256 /tr $timestamp /td sha256"

    & $signtool sign /sha1 $thumb /fd sha256 /tr $timestamp /td sha256 (Join-Path $publish 'PhotoTag.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Podpisanie PhotoTag.exe nie powiodło się' }
    $isccArgs += '/DSign', "/Ssigntool=$signCmd `$f"
    Write-Host 'Podpisywanie: włączone'
} else {
    Write-Host 'Podpisywanie: pominięte (brak PHOTOTAG_SIGN_THUMBPRINT)'
}

& $iscc @isccArgs (Join-Path $PSScriptRoot 'PhotoTag.iss')
if ($LASTEXITCODE -ne 0) { throw 'Kompilacja instalatora nie powiodła się' }
