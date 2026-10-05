# Publishes the app (self-contained, win-x64) and compiles the Inno Setup installer.
# Output: artifacts\installer\LichessPuzzleDesktop-<version>-Setup.exe
param(
    # Defaults to <Version> in the app project.
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'app\LichessPuzzleDesktop\LichessPuzzleDesktop.csproj'
$publishDir = Join-Path $root 'artifacts\publish'
$installerDir = Join-Path $root 'artifacts\installer'

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "'$Version' is not a version like 2.0.0." }

$iscc = @(
    (Get-Command iscc -ErrorAction SilentlyContinue).Source
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php or with: winget install JRSoftware.InnoSetup' }

# Empty the folder rather than deleting it, so a shell sitting in it doesn't block the build.
if (Test-Path $publishDir) { Get-ChildItem $publishDir -Force | Remove-Item -Recurse -Force }
# Self-contained: the installed app needs neither the .NET runtime nor the Windows App SDK runtime.
dotnet publish $project -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $publishDir | Out-Host
if ($LASTEXITCODE) { throw 'dotnet publish failed.' }

foreach ($required in 'LichessPuzzleDesktop.exe', 'LichessPuzzleDesktop.pri', 'MainWindow.xbf', 'lpd_core.dll', 'Assets\pieces\wK.svg', 'samples\sample_puzzles.csv') {
    if (-not (Test-Path (Join-Path $publishDir $required))) { throw "Publish output is missing $required." }
}

& $iscc /Qp "/DAppVersion=$Version" "/DSourceDir=$publishDir" "/DOutputDir=$installerDir" (Join-Path $root 'installer\LichessPuzzleDesktop.iss') | Out-Host
if ($LASTEXITCODE) { throw 'Inno Setup compilation failed.' }

Get-Item (Join-Path $installerDir "LichessPuzzleDesktop-$Version-Setup.exe")
