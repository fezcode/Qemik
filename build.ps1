param([switch]$Test, [switch]$Publish, [switch]$Run, [string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
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
        dotnet publish native/Qemik.Desktop/Qemik.Desktop.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -o "dist/$Runtime"
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    }
    if ($Run) {
        $executable = if ($Publish) { "dist/$Runtime/Qemik.exe" } else { 'native/Qemik.Desktop/bin/Release/net10.0/Qemik.exe' }
        Start-Process -FilePath (Join-Path $PSScriptRoot $executable)
    }
}
finally { Pop-Location }
