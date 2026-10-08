# Builds the zip for distribution: artifacts\OrderMail-win-x64.zip
# and refreshes OrderMail-latest\ at the repository root for manual checking, with the test report.
# The exe is self-contained, so the target PC needs no .NET installation.
param(
    [string]$Runtime = "win-x64",
    # Only build the zip (used by CI): skip OrderMail-latest and the test report.
    [switch]$ZipOnly
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts\OrderMail"
$zip = Join-Path $root "artifacts\OrderMail-$Runtime.zip"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

dotnet publish (Join-Path $root "src\OrderMail.Web") -c Release -r $Runtime -o $out --nologo -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item (Join-Path $root "docs\README-distribution.txt") (Join-Path $out "README.txt")
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Created $zip ($size MB)"
if ($ZipOnly) { return }

# Copy for manual checking: OrderMail-latest\OrderMail.exe. The data folder (DB and settings) is kept across rebuilds.
$latest = Join-Path $root "OrderMail-latest"
if (Get-Process -Name OrderMail -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($latest, [StringComparison]::OrdinalIgnoreCase) }) {
    throw "OrderMail-latest\OrderMail.exe is running. Close it and run this script again."
}
New-Item -ItemType Directory -Force $latest | Out-Null
Get-ChildItem $latest | Where-Object { $_.Name -ne "data" } | Remove-Item -Recurse -Force
Copy-Item (Join-Path $out "*") $latest -Recurse
Write-Host "Updated $latest\OrderMail.exe"

# Test specification (docs\test-spec.md) and results (OrderMail-latest\test-report.md).
dotnet run (Join-Path $root "scripts\TestReport.cs") -- $root $latest
switch ($LASTEXITCODE) {
    0 { }
    1 { Write-Warning "Some tests failed. See $latest\test-report.md" }
    default { Write-Warning "The test report could not be created." }
}
