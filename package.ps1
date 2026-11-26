$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$payloadDir = Join-Path $dist "payload"
$payloadZip = Join-Path $root "src\ProjectCrystal.Installer\payload.zip"

Write-Host "=== 1. Running Automated Tests ==="
dotnet test (Join-Path $root "src\ProjectCrystal.Tests\ProjectCrystal.Tests.csproj") --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE" }

Write-Host "=== 2. Publishing ProjectCrystal.Shell (Release win-x64) ==="
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null

dotnet publish (Join-Path $root "src\ProjectCrystal.Shell\ProjectCrystal.Shell.csproj") `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $payloadDir
if ($LASTEXITCODE -ne 0) { throw "Shell publish failed with exit code $LASTEXITCODE" }

Write-Host "=== 3. Creating Embedded Installer Payload ==="
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
Compress-Archive -Path (Join-Path $payloadDir "*") -DestinationPath $payloadZip -CompressionLevel Optimal

Write-Host "=== 4. Building Standalone ProjectCrystalSetup.exe ==="
$installerOut = Join-Path $dist "installer_tmp"
dotnet publish (Join-Path $root "src\ProjectCrystal.Installer\ProjectCrystal.Installer.csproj") `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    --output $installerOut

Copy-Item (Join-Path $installerOut "ProjectCrystalSetup.exe") (Join-Path $dist "ProjectCrystalSetup.exe") -Force
Remove-Item $installerOut -Recurse -Force
Remove-Item $payloadZip -Force

Write-Host "=== 5. Package Ready ==="
Get-Item (Join-Path $dist "ProjectCrystalSetup.exe") | Select-Object Name, Length, FullName
