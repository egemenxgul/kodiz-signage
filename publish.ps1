<#
.SYNOPSIS
    Builds Kodiz Signage as a single, self-contained KodizSignage.exe (no .NET install needed).

.EXAMPLE
    .\publish.ps1                                   # tests + publish to .\publish
    .\publish.ps1 -SkipTests
    .\publish.ps1 -CertificatePath code.pfx -CertificatePassword ****   # + code signing
    .\publish.ps1 -CertificateThumbprint 0123ABCD...                     # cert from the Windows store
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = (Join-Path $PSScriptRoot "publish"),
    [switch]$SkipTests,
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    Write-Host "==> Running tests" -ForegroundColor Cyan
    dotnet test "tests/KodizSignage.Tests/KodizSignage.Tests.csproj" -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

Write-Host "==> Publishing $Runtime ($Configuration) to $Output" -ForegroundColor Cyan
if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

dotnet publish "src/KodizSignage/KodizSignage.csproj" `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $Output
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

$exe = Join-Path $Output "KodizSignage.exe"

# Optional code signing: removes the SmartScreen "unknown publisher" warning (needs a code-signing certificate).
if ($CertificatePath -or $CertificateThumbprint) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "x64" } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw "signtool.exe not found – install the Windows SDK" }

    $signArgs = @("sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256", "/d", "Kodiz Signage")
    if ($CertificatePath) {
        $signArgs += @("/f", $CertificatePath)
        if ($CertificatePassword) { $signArgs += @("/p", $CertificatePassword) }
    } else {
        $signArgs += @("/sha1", $CertificateThumbprint)
    }
    $signArgs += $exe

    Write-Host "==> Signing" -ForegroundColor Cyan
    & $signtool.FullName @signArgs
    if ($LASTEXITCODE -ne 0) { throw "Signing failed" }
}

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "==> Done: $exe ($sizeMb MB)" -ForegroundColor Green
