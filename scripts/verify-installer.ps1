param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$bytes = [IO.File]::ReadAllBytes($Path)
$trailer = $bytes.Length - 72
if ($trailer -lt 64 -or [Text.Encoding]::ASCII.GetString($bytes, $trailer, 5) -ne 'FORGE') { throw 'Missing Forge trailer.' }
$offset = [BitConverter]::ToInt64($bytes, $trailer + 16)
$length = [BitConverter]::ToInt64($bytes, $trailer + 24)
if ($offset -lt 64 -or $length -le 0 -or $offset + $length -ne $trailer) { throw 'Invalid Forge payload bounds.' }
$sha = [Security.Cryptography.SHA256]::Create()
try { $hash = $sha.ComputeHash($bytes, $offset, $length) } finally { $sha.Dispose() }
if ([BitConverter]::ToString($hash) -ne [BitConverter]::ToString($bytes, $trailer + 32, 32)) { throw 'Forge checksum mismatch.' }
$stream = [IO.MemoryStream]::new($bytes, [int]$offset, [int]$length, $false)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read)
try {
    function Read-Entry([string]$Name) {
        $entry = $zip.GetEntry($Name)
        if ($null -eq $entry) { throw "Installer is missing $Name" }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    $manifest = (Read-Entry 'manifest.json') | ConvertFrom-Json
    if ($manifest.app.id -ne 'com.fezcode.qemik' -or $manifest.app.version -ne $Version) { throw 'Incorrect installer identity/version.' }
    if ($manifest.ui.theme -ne 'huh') { throw 'Installer must use the huh theme.' }
    if (($manifest.steps.type -join ',') -ne 'welcome,license,folder,shortcuts,install,finish') { throw 'Installer must contain all six wizard steps in order.' }
    foreach ($step in $manifest.steps) { if ([string]::IsNullOrWhiteSpace($step.title) -or [string]::IsNullOrWhiteSpace($step.body)) { throw "Wizard step $($step.type) has no title or body." } }
    if ((Read-Entry $manifest.steps[1].file) -notmatch 'MIT License') { throw 'MIT license agreement is missing.' }
    if (@($manifest.shortcuts).Count -ne 2) { throw 'Expected Desktop and Start Menu choices.' }
    foreach ($shortcut in $manifest.shortcuts) {
        if (-not $shortcut.optional -or -not $shortcut.default -or $shortcut.target -ne '${INSTALLDIR}/Qemik.exe') { throw 'Incorrect shortcut configuration.' }
    }
    if (($manifest.shortcuts.location -join '|') -ne '${DESKTOP}/Qemik.lnk|${STARTMENU}/Qemik.lnk') { throw 'Unexpected shortcut destinations.' }
    foreach ($name in @('InstallDir', 'Version')) {
        $entry = @($manifest.registry | Where-Object { $_.value -eq $name -and $_.hive -eq 'HKCU' -and $_.key -eq 'Software\fezcode\Qemik' })
        $expected = if ($name -eq 'Version') { '${app.version}' } else { '${INSTALLDIR}' }
        if ($entry.Count -ne 1 -or $entry[0].data -ne $expected) { throw "Missing vendor registration: $name" }
    }
    # The Qemik library holds guest disks; the uninstaller must never offer to delete it.
    if (@($manifest.uninstall.settings_dirs | Where-Object { $_ }).Count -ne 0) { throw 'Uninstall must not offer to remove the Qemik library.' }
    $launch = $manifest.steps[5].launches
    if (@($launch).Count -ne 1 -or -not $launch[0].checked -or $launch[0].target -ne '${INSTALLDIR}/Qemik.exe') { throw 'Open Qemik must be selected on Finish.' }
    $destinations = @($manifest.files.dst)
    foreach ($file in @('Qemik.exe', 'Qemik.dll', 'Qemik.Core.dll', 'Qemik.deps.json', 'Qemik.runtimeconfig.json', 'coreclr.dll', 'e_sqlite3.dll', 'Assets/qemik.ico', 'LICENSE.txt')) {
        if ($destinations -notcontains ('${INSTALLDIR}/' + $file)) { throw "Payload is missing installed file $file" }
    }
    foreach ($forbidden in @('*.iso', '*.qcow2', '*.vhdx', '*.raw', '*.img', 'qemu-system-*')) {
        $hit = @($destinations | Where-Object { $_ -like "*/$forbidden" })
        if ($hit.Count -gt 0) { throw "Payload must not bundle QEMU or guest media: $($hit[0])" }
    }
    # Forge copies every .ico image verbatim into the Setup's RT_ICON resources, but only
    # warns when patching fails. Search the executable part (before the payload) so the
    # bundled copy of the icon cannot satisfy the check.
    if ($manifest.app.icon -ne 'native/Qemik.Desktop/Assets/qemik.ico') { throw 'Installer icon must be Qemik''s icon.' }
    $ico = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot '../native/Qemik.Desktop/Assets/qemik.ico'))
    $stub = [Text.Encoding]::GetEncoding(28591).GetString($bytes, 0, [int]$offset)
    for ($i = 0; $i -lt [BitConverter]::ToUInt16($ico, 4); $i++) {
        $size = [BitConverter]::ToInt32($ico, 6 + $i * 16 + 8); $start = [BitConverter]::ToInt32($ico, 6 + $i * 16 + 12)
        if ($stub.IndexOf([Text.Encoding]::GetEncoding(28591).GetString($ico, $start, $size), [StringComparison]::Ordinal) -lt 0) { throw "Setup executable is missing Qemik icon image $($i + 1); Forge did not patch the icon." }
    }
    if ($null -eq $zip.GetEntry('theme/fragments/shortcuts.html') -or (Read-Entry 'theme/theme.js') -notmatch 'SetShortcutEnabled') { throw 'Packaged theme cannot render shortcut choices.' }
    Write-Host "Verified Qemik ${Version}: bundle checksum, Setup icon, six steps, MIT license, shortcuts, registry, preserved library, folder payload and finish launch."
} finally { $zip.Dispose(); $stream.Dispose() }
