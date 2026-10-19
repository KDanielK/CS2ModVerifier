<#
.SYNOPSIS
    Regenerates src/CS2ModVerifier.Core/known-libraries.json from official GitHub releases.

.DESCRIPTION
    For each stable Harmony release since 2.2.0, downloads the release zips from github.com/pardeike/Harmony
    with the GitHub CLI, hashes every runtime 0Harmony.dll inside them (in memory, nothing is unpacked or run),
    and writes the list the tool uses to recognise official builds. Anyone can run this to check the list.

    Requires: GitHub CLI (gh), logged in. Windows PowerShell 5.1 or PowerShell 7.
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\src\CS2ModVerifier.Core\known-libraries.json'),
    [version]$MinimumVersion = '2.2.0'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repo = 'pardeike/Harmony'
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('known-libraries-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$sha = [System.Security.Cryptography.SHA256]::Create()
$entries = New-Object System.Collections.Generic.List[object]

try {
    $releases = gh api "repos/$repo/releases?per_page=100" | ConvertFrom-Json
    foreach ($release in $releases) {
        if ($release.prerelease -or $release.draft) { continue }
        $versionText = ($release.tag_name -replace '^v', '') -replace '-.*$', ''
        $parsed = $null
        if (-not [version]::TryParse($versionText, [ref]$parsed) -or $parsed -lt $MinimumVersion) { continue }

        # Runtime builds only: "Ref" zips hold reference assemblies for compiling, not code that runs.
        foreach ($asset in $release.assets | Where-Object { $_.name -like '*.zip' -and $_.name -notlike 'Harmony-Ref*' }) {
            $zipPath = Join-Path $work $asset.name
            gh release download $release.tag_name --repo $repo --pattern $asset.name --dir $work --clobber | Out-Null
            $variant = if ($asset.name -like 'Harmony-Thin*') { 'thin' } else { 'fat' }

            $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
            try {
                foreach ($entry in $zip.Entries | Where-Object { $_.Name -eq '0Harmony.dll' }) {
                    $stream = $entry.Open()
                    try { $hash = ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) -join '' }
                    finally { $stream.Dispose() }

                    $build = Split-Path (Split-Path $entry.FullName -Parent) -Leaf
                    $entries.Add([ordered]@{
                        name     = 'Harmony'
                        fileName = '0Harmony.dll'
                        version  = $versionText
                        build    = "$build ($variant)"
                        sha256   = $hash
                        source   = $release.html_url
                    })
                }
            }
            finally { $zip.Dispose() }
            Remove-Item $zipPath
        }

        Write-Host "$($release.tag_name): done"
    }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

$sorted = @($entries | Sort-Object { [version]$_.version }, { $_.build } -Unique)
# "sources" tells the tool where to look up versions released after this list was made.
$sources = @([ordered]@{ name = 'Harmony'; fileName = '0Harmony.dll'; repository = $repo })
$json = ConvertTo-Json -InputObject ([ordered]@{ generatedBy = 'tools/Update-KnownLibraries.ps1'; sources = $sources; libraries = $sorted }) -Depth 4
[System.IO.File]::WriteAllText((Resolve-Path (Split-Path $Output -Parent)).Path + '\' + (Split-Path $Output -Leaf), $json, (New-Object System.Text.UTF8Encoding $false))
Write-Host "Wrote $($sorted.Count) entries to $Output"
