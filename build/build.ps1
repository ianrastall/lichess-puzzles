<#
.SYNOPSIS
    Checks, tests and publishes Lichess Puzzles as a portable win-x64 folder and zip.

.EXAMPLE
    pwsh build/build.ps1                 # full check, tests and Release publish
    pwsh build/build.ps1 -SkipChecks     # publish only
#>
param(
    [switch]$SkipChecks,
    [string]$VcRuntimeDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'target'
$dist = Join-Path $root 'dist'
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Select-Object -First 1
$appDir = Join-Path $dist 'LichessPuzzles'

# The Rust DLL links the VC runtime dynamically. A self-contained .NET publish does not copy it.
if (-not $VcRuntimeDirectory) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $vsPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($vsPath) {
            $VcRuntimeDirectory = Get-ChildItem -Path (Join-Path $vsPath 'VC/Redist/MSVC/*/x64/Microsoft.VC*.CRT') -Directory |
                Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
        }
    }
}
if (-not $VcRuntimeDirectory -or -not (Test-Path -LiteralPath (Join-Path $VcRuntimeDirectory 'vcruntime140.dll'))) {
    throw 'Visual C++ x64 redistributable files were not found. Install the MSVC build tools or pass -VcRuntimeDirectory.'
}

function Invoke-Step([string]$Name, [scriptblock]$Command) {
    Write-Host "== $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit $LASTEXITCODE)." }
}

Push-Location $root
try {
    if (-not $SkipChecks) {
        Invoke-Step 'cargo fmt' { cargo fmt --all --check }
        Invoke-Step 'cargo clippy' { cargo clippy --workspace --all-targets --locked --target-dir $target -- -D warnings }
        Invoke-Step 'cargo test' { cargo test --workspace --locked --target-dir $target }
        Invoke-Step 'interop tests' { dotnet run --project tests/LichessPuzzles.InteropTests -c Release }
    }

    $expectedAppDir = [IO.Path]::GetFullPath((Join-Path $root 'dist/LichessPuzzles'))
    if ([IO.Path]::GetFullPath($appDir) -ne $expectedAppDir) { throw 'Unexpected publish path.' }
    if (Test-Path $appDir) { Remove-Item -LiteralPath $appDir -Recurse -Force }
    Invoke-Step 'publish app' { dotnet publish src/LichessPuzzles.App -c Release -o $appDir }
    Invoke-Step 'build CLI' { cargo build -p lichess-puzzles --release --locked --target-dir $target }
    Copy-Item (Join-Path $target 'release/lichess-puzzles.exe') $appDir
    Copy-Item (Join-Path $VcRuntimeDirectory '*.dll') $appDir
    Copy-Item (Join-Path $root 'LICENSE') $appDir

    foreach ($file in 'LichessPuzzles.exe', 'LichessPuzzles.pri', 'lichess_puzzles_ffi.dll', 'lichess-puzzles.exe', 'vcruntime140.dll', 'Assets/app.ico', 'Assets/pieces/wK.svg') {
        if (-not (Test-Path (Join-Path $appDir $file))) { throw "Published folder is missing $file." }
    }

    $zip = Join-Path $dist "LichessPuzzles-$version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $appDir '*') -DestinationPath $zip
    Write-Host "Published $appDir and $zip" -ForegroundColor Green
}
finally {
    Pop-Location
}
