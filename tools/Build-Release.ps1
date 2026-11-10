param(
    [string]$Version = '0.1.0-preview.1',
    [string]$OutputDirectory = 'artifacts/releases',
    [string]$Dotnet = '.dotnet/dotnet.exe'
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$') { throw 'Invalid release version.' }
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$dotnetPath = (Resolve-Path $Dotnet).Path
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$work = Join-Path $output ('work-' + [Guid]::NewGuid().ToString('N'))
$packageParent = Join-Path $work 'package'
$name = "FocusBind-$Version-win-x64"
$package = Join-Path $packageParent $name
$archive = Join-Path $output ($name + '.zip')
if (Test-Path -LiteralPath $archive) { throw "Asset already exists: $archive" }
New-Item -ItemType Directory -Path $package -Force | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.packages'
& $dotnetPath publish src/FocusBind.App/FocusBind.App.csproj -c Release -r win-x64 --self-contained true --source https://api.nuget.org/v3/index.json --output $package "-p:Version=$Version" -p:IncludeSourceRevisionInInformationalVersion=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }
foreach ($required in @('FocusBind.App.exe','FocusBind.App.dll','FocusBind.Core.dll','FocusBind.Win32.dll','coreclr.dll','hostfxr.dll','hostpolicy.dll','PresentationFramework.dll','WindowsBase.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $package $required))) { throw "Missing package file: $required" }
}
$runtime = Get-Content -Raw (Join-Path $package 'FocusBind.App.runtimeconfig.json') | ConvertFrom-Json
if ($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks -or !$runtime.runtimeOptions.includedFrameworks) { throw 'Package is not self-contained.' }
$exe = Join-Path $package 'FocusBind.App.exe'
$bytes = [IO.File]::ReadAllBytes($exe)
$pe = [BitConverter]::ToInt32($bytes, 0x3c)
if ([BitConverter]::ToUInt16($bytes, $pe + 4) -ne 0x8664) { throw 'Executable is not x64.' }
if (!([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion.StartsWith($Version))) { throw 'Executable version mismatch.' }
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $package
Copy-Item -LiteralPath (Join-Path $root "docs/releases/v$Version.md") -Destination (Join-Path $package 'RELEASE-NOTES.md')
$licenses = Join-Path $package 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
    $packName = $framework.name.ToLowerInvariant() + '.runtime.win-x64'
    $pack = Join-Path $env:NUGET_PACKAGES ($packName + '/' + $framework.version)
    $notices = @(Get-ChildItem -LiteralPath $pack -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' })
    if ($notices.Count -eq 0) { throw "Bundled runtime license missing: $packName" }
    foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenses ($framework.name + '-' + $notice.Name)) }
}
@'
FocusBind - Windows x64 portable preview

1. Extract the entire ZIP. Keep all files together.
2. Run FocusBind.App.exe and accept the administrator (UAC) prompt.
3. Select a target window, edit/save mappings, close the editor, then start mapping.
4. Overlay: open overlay settings, select the target process, and enable it.
   Ctrl+Alt+F10 toggles the overlay; hotkeys can be changed in the main window.

The .NET runtime is included. Settings stay in %LOCALAPPDATA%\FocusBind.
Back up that folder before upgrading. Close the app before replacing files.
Uninstall by deleting the extracted program folder; settings are retained.

This is an unsigned preview. Actual game compatibility is not fully verified.
See RELEASE-NOTES.md and README.md for details and known limitations.
'@ | Set-Content -Encoding UTF8 (Join-Path $package 'QUICKSTART.txt')
$sourceCommit = (& git -c "safe.directory=$($root.Replace('\','/'))" rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine source commit.' }
$manifest = [ordered]@{ Version = $Version; SourceCommit = $sourceCommit; Runtime = 'win-x64'; SelfContained = $true; BuiltUtc = [DateTime]::UtcNow.ToString('o') }
$manifest | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $package 'release.json')
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($packageParent, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
$extracted = Join-Path $work 'extracted'
[IO.Compression.ZipFile]::ExtractToDirectory($archive, $extracted)
$extractedPackage = Join-Path $extracted $name
foreach ($file in Get-ChildItem -LiteralPath $package -Recurse -File) {
    $relative = $file.FullName.Substring($package.Length + 1)
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash -ne (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $extractedPackage $relative)).Hash) { throw "ZIP verification failed: $relative" }
}
$report = Join-Path $work 'package-verification.json'
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = Join-Path $extractedPackage 'FocusBind.App.exe'
$start.Arguments = '--verify-package "' + $report + '"'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardError = $true
$start.RedirectStandardOutput = $true
$hostLaunch = 'self-contained-exe'
try { $process = [Diagnostics.Process]::Start($start) }
catch {
    $reason = $_.Exception
    while ($reason.InnerException) { $reason = $reason.InnerException }
    if ($reason -isnot [ComponentModel.Win32Exception] -or $reason.NativeErrorCode -ne 740) { throw }
    # No UAC interaction in unattended verification. The DLL entry point must still load the extracted app-local runtime.
    $hostLaunch = 'dotnet-entrypoint-with-app-local-runtime'
    $start.FileName = $dotnetPath
    $start.Arguments = '"' + (Join-Path $extractedPackage 'FocusBind.App.dll') + '" --verify-package "' + $report + '"'
    $process = [Diagnostics.Process]::Start($start)
}
$standardError = $process.StandardError.ReadToEndAsync()
$standardOutput = $process.StandardOutput.ReadToEndAsync()
if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Package verification timed out.' }
if ($process.ExitCode -ne 0) {
    Write-Output $standardError.Result
    if (Test-Path $report) { Get-Content -Raw $report | Write-Output }
    throw "Package verification failed: exit $($process.ExitCode)"
}
$verification = Get-Content -Raw $report | ConvertFrom-Json
if (!$verification.Success -or $verification.Architecture -ne 'X64') { throw 'Package smoke test failed.' }
foreach ($library in @($verification.CoreLibraryPath, $verification.DesktopLibraryPath)) {
    if (![string]::Equals([IO.Path]::GetDirectoryName($library), $extractedPackage, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Verification used a library outside the extracted package: $library"
    }
}
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
$hashPath = Join-Path $output 'SHA256SUMS.txt'
"$hash  $name.zip" | Set-Content -Encoding ASCII $hashPath
[ordered]@{ Version = $Version; SourceCommit = $sourceCommit; Archive = $archive; Sha256 = $hash; Size = (Get-Item $archive).Length; Runtime = $verification.Runtime; HostLaunch = $hostLaunch; Verification = $report; Checksums = $hashPath } | ConvertTo-Json
