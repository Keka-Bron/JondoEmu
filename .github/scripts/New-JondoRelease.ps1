<#
.SYNOPSIS
  Packages a release of the emulator for one platform: the setup players download, the full zip,
  the server and Studio as assets of their own, and the manifest the launcher updates from.

.DESCRIPTION
  The folder a player installs is what git versions of datos/, content/, launcher_assets/, the
  mods and the root marker, plus the executables built for the platform. Every file of it goes
  in the manifest with its size and SHA-256:

    - the versioned ones as "raw": the launcher fetches them one by one from the repository at
      the release's tag. They are taken with LF line endings, which is how the repository stores
      them, so their hashes are the raw files' hashes;
    - the executables as "asset": uploaded beside the zip under names without spaces. The
      launcher's asset is the setup itself (the same file), so it is uploaded once.

  An update fetches only the files whose hash changed, or the full zip when most did. Runs on
  Windows PowerShell 5.1 and on PowerShell 7, on Windows and on Linux.

.EXAMPLE
  ./New-JondoRelease.ps1 -Tag v0.3.0 -Platform win-x64 -OutDir release `
      -Executables @{ 'Jondo Emulator Launcher.exe' = 'Jondo Emulator Launcher.exe'; ... }
#>
param(
    [Parameter(Mandatory)] [string] $Tag,
    [Parameter(Mandatory)] [ValidateSet('win-x64', 'linux-x64')] [string] $Platform,
    [Parameter(Mandatory)] [string] $OutDir,
    # Name in the folder -> the built file to put there.
    [Parameter(Mandatory)] [hashtable] $Executables,
    [string] $Ref = 'HEAD',
    [string[]] $Data = @('.jondo-root', 'datos', 'content', 'launcher_assets', 'mods',
                         'JondoModLoader/JondoModLoader.dll')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not [IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path (Get-Location).ProviderPath $OutDir }
$OutDir = [IO.Path]::GetFullPath($OutDir)
$work = Join-Path $OutDir ".stage-$Platform"
$stage = Join-Path $work 'JondoEmu'
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $stage | Out-Null

# The versioned files, as the repository stores them (LF), whatever this checkout's settings.
$tar = Join-Path $work 'data.tar'
git -c core.autocrlf=false -c core.eol=lf archive --format=tar --output="$tar" $Ref -- @Data
if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }
tar -xf "$tar" -C "$stage"
if ($LASTEXITCODE -ne 0) { throw 'tar failed' }
Remove-Item $tar

# The executables, and their asset names: no spaces, the platform in them. The launcher's is the
# setup's: the launcher is the setup, and the manifest sends an update to whichever name it holds.
$setupName = if ($Platform -eq 'win-x64') { "JondoEmu-Setup-$Platform.exe" } else { "JondoEmu-Setup-$Platform" }
$assets = @{}
foreach ($name in $Executables.Keys) {
    Copy-Item -LiteralPath $Executables[$name] -Destination (Join-Path $stage $name)
    $base = [IO.Path]::GetFileNameWithoutExtension($name).ToLowerInvariant() -replace '^jondo\s+', '' -replace '\s+', '-'
    $ext = [IO.Path]::GetExtension($name)
    $assets[$name] = if ($name -like 'Jondo Emulator Launcher*') { $setupName } else { "$base-$Platform$ext" }
}

# The manifest.
$prefix = $stage.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$files = foreach ($file in (Get-ChildItem -LiteralPath $stage -Recurse -File -Force | Sort-Object FullName)) {
    $relative = $file.FullName.Substring($prefix.Length).Replace('\', '/')
    $sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    $entry = [ordered]@{ path = $relative; size = $file.Length; sha256 = $sha }
    if ($assets.ContainsKey($relative)) { $entry.asset = $assets[$relative] } else { $entry.raw = $true }
    [pscustomobject]$entry
}

# «JondoEmu-v1.0.12-win-x64.zip»: after the setup in the release's list, which sorts by name, so
# the file to download comes first. The launcher reads the name from the manifest.
$full = "JondoEmu-$Tag-$Platform.zip"
$manifest = [ordered]@{
    version  = $Tag
    platform = $Platform
    full     = $full
    setup    = $setupName
    files    = @($files)
}
$json = $manifest | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $OutDir "manifest-$Platform.json"), $json, (New-Object Text.UTF8Encoding $false))

# The full zip, with JondoEmu/ at its root. zip keeps the Unix permissions where there is one.
$zip = Join-Path $OutDir $full
if (Test-Path $zip) { Remove-Item $zip }
if ($Platform -eq 'linux-x64' -and (Get-Command zip -ErrorAction SilentlyContinue)) {
    Push-Location $work
    try { zip -qr "$zip" JondoEmu; if ($LASTEXITCODE -ne 0) { throw 'zip failed' } } finally { Pop-Location }
} else {
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
}

# The executables as assets: the launcher under the setup's name, the one players download.
foreach ($name in $Executables.Keys) {
    Copy-Item -LiteralPath (Join-Path $stage $name) -Destination (Join-Path $OutDir $assets[$name]) -Force
}

Remove-Item -Recurse -Force $work
Write-Host "[Release] $Platform $Tag : $(@($files).Count) files, $full"
Get-ChildItem $OutDir | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Host
