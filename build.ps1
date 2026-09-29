param([switch]$Test, [switch]$Publish, [switch]$Run, [string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'version.ps1')
if (-not (Show-Report $PSScriptRoot $null)) { throw 'Version mismatch. Run version.ps1 before building.' }
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
Push-Location $PSScriptRoot
try {
    dotnet restore native/Qemik.slnx --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    dotnet build native/Qemik.slnx --no-restore -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if ($Test) {
        $env:QEMIK_SCREENSHOTS = Join-Path $PSScriptRoot 'artifacts/screenshots'
        dotnet test native/Qemik.slnx --no-build --no-restore -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    if ($Publish) {
        $outputPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "dist/$Runtime"))
        if (-not $outputPath.StartsWith((Join-Path $PSScriptRoot 'dist') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish output must stay within dist.' }
        # Replace only this runtime's payload so stale files cannot reach the installer.
        # A running dist copy of Qemik locks the folder and stops the build here.
        if (Test-Path -LiteralPath $outputPath) {
            $items = @((Get-Item -LiteralPath $outputPath)) + @(Get-ChildItem -LiteralPath $outputPath -Recurse -Force)
            if ($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw "Refusing to clean linked publish output: $outputPath" }
            Remove-Item -LiteralPath $outputPath -Recurse -Force
        }
        dotnet publish native/Qemik.Desktop/Qemik.Desktop.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:DebugType=None -o "dist/$Runtime"
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Get-ChildItem -LiteralPath $outputPath -File -Filter '*.pdb' | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
    }
    if ($Run) {
        $executable = if ($Publish) { "dist/$Runtime/Qemik.exe" } else { 'native/Qemik.Desktop/bin/Release/net10.0/Qemik.exe' }
        Start-Process -FilePath (Join-Path $PSScriptRoot $executable)
    }
}
finally { Pop-Location }
