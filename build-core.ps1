# Builds the native core (core/ -> lpd_core.dll) with MSVC, CMake and Ninja.
# Called automatically by the app project; run it directly with -Test to also run the self-checks.
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [switch] $Test
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$buildDir = Join-Path $root "core\build\$Configuration"

# Import the MSVC x64 environment unless we are already inside a developer prompt.
if (-not $env:VSCMD_VER -or $env:VSCMD_ARG_TGT_ARCH -ne 'x64') {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw 'vswhere.exe not found - install Visual Studio with the C++ workload.' }
    $vs = & $vswhere -latest -prerelease -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'No Visual Studio installation with the C++ x64 tools was found.' }
    $devCmd = Join-Path $vs 'Common7\Tools\VsDevCmd.bat'
    cmd /c "`"$devCmd`" -arch=x64 -host_arch=x64 -no_logo && set" | ForEach-Object {
        if ($_ -match '^([^=]+)=(.*)$') { Set-Item -Path "env:$($Matches[1])" -Value $Matches[2] }
    }
}

foreach ($tool in 'cmake', 'ninja', 'cl') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool was not found. Install the 'C++ CMake tools for Windows' Visual Studio component."
    }
}

$env:CC = 'cl'
$env:CXX = 'cl'
cmake -S (Join-Path $root 'core') -B $buildDir -G Ninja "-DCMAKE_BUILD_TYPE=$Configuration" | Out-Host
if ($LASTEXITCODE) { throw 'CMake configure failed.' }
cmake --build $buildDir | Out-Host
if ($LASTEXITCODE) { throw 'Native core build failed.' }

if ($Test) {
    & (Join-Path $buildDir 'lpd_core_tests.exe') (Join-Path $root 'samples\sample_puzzles.csv') | Out-Host
    if ($LASTEXITCODE) { throw 'Native core self-checks failed.' }
}
