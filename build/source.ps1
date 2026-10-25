param([Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path ([IO.Path]::GetTempPath()) ('lichess-puzzles-source-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $manifest = [Collections.Generic.List[string]]::new()
    foreach ($name in 'lichess-puzzles', 'jupiter-ui') {
        $repo = if ($name -eq 'lichess-puzzles') { $root } else { Join-Path (Split-Path -Parent $root) $name }
        if (Test-Path -LiteralPath (Join-Path $repo '.git')) {
            $files = git -C $repo ls-files
            if ($LASTEXITCODE -ne 0) { throw "Cannot enumerate $name source." }
            $commit = git -C $repo rev-parse HEAD
        }
        else {
            # Release archives contain no .git directory. Repackage only files from their manifest.
            $originalManifest = Join-Path (Split-Path -Parent $root) 'SOURCE-MANIFEST.txt'
            if (-not (Test-Path -LiteralPath $originalManifest)) { throw "Cannot find Git metadata or SOURCE-MANIFEST.txt for $name." }
            $prefix = "$name/"
            $files = Get-Content -LiteralPath $originalManifest | Where-Object { $_ -match '^[0-9a-f]{64}  ' } |
                ForEach-Object { $_.Substring(66) } | Where-Object { $_.StartsWith($prefix) } |
                ForEach-Object { $_.Substring($prefix.Length) }
            $commit = 'release source snapshot'
        }
        $manifest.Add("$name base commit: $commit (SHA256 entries identify the packaged working files)")
        foreach ($file in $files) {
            $source = Join-Path $repo $file
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
            $destination = Join-Path $stage "$name/$file"
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination
            $manifest.Add("$((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant())  $name/$file")
        }
    }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $stage 'jupiter-ui/LICENSE')
    $uiLicense = Join-Path $stage 'jupiter-ui/LICENSE'
    if (-not ($manifest | Where-Object { $_.EndsWith('  jupiter-ui/LICENSE') })) {
        $manifest.Add("$((Get-FileHash -LiteralPath $uiLicense -Algorithm SHA256).Hash.ToLowerInvariant())  jupiter-ui/LICENSE")
    }
    [IO.File]::WriteAllLines((Join-Path $stage 'SOURCE-MANIFEST.txt'), $manifest)
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $OutputPath -Force
}
finally {
    $resolved = [IO.Path]::GetFullPath($stage)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolved)).StartsWith('lichess-puzzles-source-')) { throw 'Unexpected source staging path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
