param([switch]$SkipBuild, [string]$Forge = (Join-Path $PSScriptRoot '../Forge/build/forge.exe'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'version.ps1')
if (-not (Show-Report $PSScriptRoot $null)) { throw 'Version mismatch. Run version.ps1 before packaging.' }
$version = (Test-Consistent $PSScriptRoot $null).Target
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Test -Publish -Runtime win-x64 }
$forgeExecutable = [IO.Path]::GetFullPath($Forge)
if (-not (Test-Path -LiteralPath $forgeExecutable)) { throw "Forge not found: $forgeExecutable (run gobake build in ../Forge)" }
# Forge silently skips a missing [app] icon, which would ship Setup with Forge's own icon.
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'native/Qemik.Desktop/Assets/qemik.ico') -PathType Leaf)) { throw 'Installer icon is missing: native/Qemik.Desktop/Assets/qemik.ico' }
$payload = Join-Path $PSScriptRoot 'dist/win-x64/Qemik.exe'
if (-not (Test-Path -LiteralPath $payload -PathType Leaf)) { throw 'Publish output is missing: dist/win-x64/Qemik.exe' }
$payloadVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($payload).ProductVersion -replace '\+.*$', ''
if ($payloadVersion -ne $version) { throw "Payload version mismatch: Qemik.exe is $payloadVersion, expected $version. Rebuild before packaging." }
foreach ($required in @('Qemik.dll', 'Qemik.deps.json', 'Qemik.runtimeconfig.json', 'Qemik.Core.dll', 'coreclr.dll', 'hostfxr.dll', 'e_sqlite3.dll', 'Assets/qemik.ico', 'LICENSE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot "dist/win-x64/$required") -PathType Leaf)) { throw "Folder publish is incomplete: $required is missing. Rebuild before packaging." }
}
# A fresh staging directory prevents an old installer from being reported after a failed build.
$installerRoot = Join-Path $PSScriptRoot 'dist/installer'
$stage = Join-Path $installerRoot ('.installer-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$process = Start-Process -FilePath $forgeExecutable -ArgumentList @('build', '--out', ('"{0}"' -f $stage)) -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "Forge build failed with exit code $($process.ExitCode)" }
$filename = "Qemik-Setup-$version.exe"
$setup = Join-Path $stage $filename
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw "Forge did not produce $setup" }
$stream = [IO.File]::OpenRead($setup)
$reader = [IO.BinaryReader]::new($stream)
try {
    if ($stream.Length -lt 128 -or $reader.ReadUInt16() -ne 0x5a4d) { throw 'Setup is not a Windows executable.' }
    $stream.Position = 60; $pe = $reader.ReadInt32()
    if ($pe -lt 64 -or $pe -gt $stream.Length - 94) { throw 'Setup has an invalid PE header.' }
    $stream.Position = $pe
    if ($reader.ReadUInt32() -ne 0x4550) { throw 'Setup has an invalid PE signature.' }
    $stream.Position = $pe + 24 + 68
    if ($reader.ReadUInt16() -ne 2) { throw 'Setup must be a GUI executable, not a console stub.' }
} finally { $reader.Dispose(); $stream.Dispose() }
& (Join-Path $PSScriptRoot 'scripts/verify-installer.ps1') -Path $setup -Version $version
$destination = [IO.Path]::GetFullPath((Join-Path $installerRoot $filename))
$boundary = [IO.Path]::GetFullPath($installerRoot) + [IO.Path]::DirectorySeparatorChar
if (-not $destination.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFullPath($setup)).StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer paths must stay within dist/installer.' }
Move-Item -LiteralPath $setup -Destination $destination -Force
# Remove only this empty staging directory, preserving every other build and installer.
if (@(Get-ChildItem -LiteralPath $stage -Force).Count -eq 0) { Remove-Item -LiteralPath $stage }
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
Write-Host "SHA-256 $hash"
Get-Item -LiteralPath $destination
