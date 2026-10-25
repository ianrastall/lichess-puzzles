param([Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$text = [Text.StringBuilder]::new()
[void]$text.AppendLine('Lichess Puzzles - third-party notices')
[void]$text.AppendLine('Application code: MIT (see LICENSE). Merida artwork: GPL-2.0-or-later (see Assets/pieces).')
[void]$text.AppendLine('The entries below include build and test dependencies as well as shipped dependencies.')

function Add-Licences([string]$Directory) {
    Get-ChildItem -LiteralPath $Directory -File -Recurse -Depth 2 |
        Where-Object { $_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE|ThirdPartyNotices)([.\-_]|$)' -and $_.Length -lt 2MB } |
        Sort-Object FullName | ForEach-Object {
            [void]$text.AppendLine("--- $([IO.Path]::GetRelativePath($Directory, $_.FullName)) ---")
            [void]$text.AppendLine([IO.File]::ReadAllText($_.FullName))
        }
}

Push-Location $root
try {
    $metadata = cargo metadata --locked --format-version 1 | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Cargo metadata failed.' }
    foreach ($package in ($metadata.packages | Where-Object source | Sort-Object name, version)) {
        [void]$text.AppendLine("`n=== Rust: $($package.name) $($package.version) ===")
        [void]$text.AppendLine("Licence: $($package.license); upstream: $($package.repository)")
        Add-Licences (Split-Path -Parent $package.manifest_path)
    }
    [void]$text.AppendLine("`n=== Additional upstream licence texts ===")
    Add-Licences (Join-Path $root 'licenses')
    $cacheLine = dotnet nuget locals global-packages --list
    if ($LASTEXITCODE -ne 0) { throw 'NuGet cache lookup failed.' }
    $cache = ($cacheLine -replace '^global-packages:\s*', '').Trim()
    $packages = @{}
    foreach ($lock in (Get-ChildItem -Path (Join-Path $root 'src/*/packages.lock.json'))) {
        $data = Get-Content -Raw -LiteralPath $lock | ConvertFrom-Json -AsHashtable
        foreach ($framework in $data.dependencies.Values) {
            foreach ($entry in $framework.GetEnumerator()) {
                if ($entry.Value.type -eq 'Project') { continue }
                $packages["$($entry.Key.ToLowerInvariant())/$($entry.Value.resolved)"] = $true
            }
        }
    }
    foreach ($id in ($packages.Keys | Sort-Object)) {
        $dir = Join-Path $cache $id
        if (-not (Test-Path -LiteralPath $dir)) { throw "Missing restored package: $id" }
        $spec = Get-ChildItem -LiteralPath $dir -Filter '*.nuspec' | Select-Object -First 1
        $info = ([xml](Get-Content -Raw -LiteralPath $spec.FullName)).package.metadata
        [void]$text.AppendLine("`n=== NuGet: $id ===")
        [void]$text.AppendLine("Authors: $($info.authors); licence: $($info.license.InnerText); licence URL: $($info.licenseUrl); project: $($info.projectUrl)")
        Add-Licences $dir
    }
    # Self-contained .NET binaries also carry the runtime's notices.
    $runtime = Get-ChildItem -LiteralPath $cache -Directory -Filter 'microsoft.netcore.app.runtime.win-x64' |
        Get-ChildItem -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($runtime) { Add-Licences $runtime.FullName }
    [IO.File]::WriteAllText($OutputPath, $text.ToString())
}
finally { Pop-Location }
