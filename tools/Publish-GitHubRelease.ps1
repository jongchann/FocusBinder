param(
    [string]$Version = '0.1.0-preview.2',
    [string]$AssetDirectory = "artifacts/releases/$Version",
    [string]$Repository = 'jongchann/FocusBinder',
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$' -or $Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid release parameters.' }
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
# Credential Manager owns authentication. Keep its response and token in memory; never log them.
$credentialLines = "protocol=https`nhost=github.com`nusername=jongchann`n`n" | & git credential fill
if ($LASTEXITCODE -ne 0) { throw 'GitHub authentication is unavailable. Refresh Git Credential Manager login.' }
$credentials = @{}
foreach ($line in $credentialLines) {
    $split = $line.IndexOf('=')
    if ($split -gt 0) { $credentials[$line.Substring(0, $split)] = $line.Substring($split + 1) }
}
if (!$credentials.password) { throw 'No GitHub credential available.' }
$headers = @{ Authorization = 'Bearer ' + $credentials.password; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'FocusBind-release' }
$api = "https://api.github.com/repos/$Repository"
$repo = Invoke-RestMethod -Uri $api -Headers $headers
$existing = @(Invoke-RestMethod -Uri "$api/releases?per_page=100" -Headers $headers) | Where-Object { $_.tag_name -eq "v$Version" } | Select-Object -First 1
if ($CheckOnly) { [ordered]@{ Repository = $repo.full_name; Private = $repo.private; ExistingRelease = $existing.html_url; CanPush = $repo.permissions.push } | ConvertTo-Json; exit 0 }
if (!$repo.permissions.push) { throw 'GitHub credential does not have write permission.' }
$gitPrefix = @('-c', "safe.directory=$($root.Replace('\','/'))")
$dirty = & git @gitPrefix status --porcelain
if ($dirty) { throw 'Commit all release source changes before publishing.' }
$commit = (& git @gitPrefix rev-parse HEAD).Trim()
$directory = [IO.Path]::GetFullPath((Join-Path $root $AssetDirectory))
$archiveName = "FocusBind-$Version-win-x64.zip"
$archive = Join-Path $directory $archiveName
$checksums = Join-Path $directory 'SHA256SUMS.txt'
if (!(Test-Path $archive) -or !(Test-Path $checksums)) { throw 'Build the release assets first.' }
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
if ((Get-Content -Raw $checksums).Trim() -ne "$hash  $archiveName") { throw 'Asset checksum does not match.' }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $entry = $zip.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq "FocusBind-$Version-win-x64/release.json" } | Select-Object -First 1
    if (!$entry) { throw 'Release manifest missing from ZIP.' }
    $reader = New-Object IO.StreamReader($entry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.SourceCommit -ne $commit -or $manifest.Version -ne $Version) { throw 'Build artifacts from the exact committed release source.' }
}
finally { $zip.Dispose() }
if ($existing -and !$existing.draft) { throw 'This release is already published; refusing to overwrite it.' }
& git @gitPrefix push origin main
if ($LASTEXITCODE -ne 0) { throw 'Source push failed.' }
$tag = "v$Version"
& git @gitPrefix show-ref --verify --quiet "refs/tags/$tag"
if ($LASTEXITCODE -eq 0) {
    $tagCommit = (& git @gitPrefix rev-list -n 1 $tag).Trim()
    if ($tagCommit -ne $commit) { throw 'Existing local tag points to another commit.' }
}
else {
    & git @gitPrefix tag -a $tag -m "FocusBind $Version"
    if ($LASTEXITCODE -ne 0) { throw 'Tag creation failed.' }
}
& git @gitPrefix push origin $tag
if ($LASTEXITCODE -ne 0) { throw 'Tag push failed.' }
function Send-Json([string]$Method, [string]$Uri, $Value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 8))
    Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers -ContentType 'application/json; charset=utf-8' -Body $bytes
}
$body = [IO.File]::ReadAllText((Join-Path $root "docs/releases/v$Version.md"))
$release = $existing
if (!$release) {
    $release = Send-Json 'Post' "$api/releases" @{ tag_name = $tag; target_commitish = $commit; name = "FocusBind $Version";
        body = $body; draft = $true; prerelease = $true; make_latest = 'false' }
}
elseif ($release.target_commitish -ne $commit) { throw 'Existing draft targets another commit.' }
# Publish only after all assets have uploaded and their server size/hash have been checked.
foreach ($assetPath in @($archive, $checksums)) {
    $file = Get-Item -LiteralPath $assetPath
    $localHash = 'sha256:' + (Get-FileHash -Algorithm SHA256 -LiteralPath $assetPath).Hash.ToLowerInvariant()
    $uploaded = @($release.assets) | Where-Object { $_.name -eq $file.Name } | Select-Object -First 1
    if (!$uploaded) {
        $upload = $release.upload_url.Split('{')[0] + '?name=' + [Uri]::EscapeDataString($file.Name)
        $contentType = if ($file.Extension -eq '.zip') { 'application/zip' } else { 'text/plain' }
        $uploaded = Invoke-RestMethod -Method Post -Uri $upload -Headers $headers -ContentType $contentType -InFile $assetPath -TimeoutSec 600
    }
    if ($uploaded.size -ne $file.Length -or $uploaded.state -ne 'uploaded') { throw "Upload verification failed: $($file.Name)" }
    if ($uploaded.digest -and $uploaded.digest -ne $localHash) { throw "Server checksum mismatch: $($file.Name)" }
}
$published = Send-Json 'Patch' "$api/releases/$($release.id)" @{ draft = $false; prerelease = $true; body = $body; make_latest = 'false' }
$verified = Invoke-RestMethod -Uri "$api/releases/tags/$tag" -Headers $headers
if ($verified.draft -or !$verified.prerelease -or @($verified.assets).Count -ne 2) { throw 'Published release state is unexpected.' }
[ordered]@{ Url = $published.html_url; Tag = $tag; Commit = $commit; Prerelease = $verified.prerelease; Assets = @($verified.assets | Select-Object name,size,browser_download_url,digest) } | ConvertTo-Json -Depth 5
