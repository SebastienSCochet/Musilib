<#
.SYNOPSIS
    Génère un exécutable Windows autonome (self-contained, fichier unique) de Musilib Manager.

.DESCRIPTION
    1. Restaure et exécute les tests (sauf -SkipTests).
    2. Publie Musilib.Desktop en self-contained single-file pour le runtime demandé.
    3. Produit une archive ZIP prête à distribuer.

.EXAMPLE
    ./scripts/publish.ps1
    ./scripts/publish.ps1 -Runtime win-arm64 -SkipTests
#>
[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64", "win-x86")]
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src/Musilib.Desktop/Musilib.Desktop.csproj"
$output = Join-Path $root "artifacts/publish/$Runtime"

Write-Host "== Musilib Manager : publication $Runtime ($Configuration) ==" -ForegroundColor Green

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "Le SDK .NET 10 est requis (https://dotnet.microsoft.com/download)."
}

if (-not $SkipTests) {
    Write-Host "-> Tests automatisés..." -ForegroundColor Cyan
    dotnet test (Join-Path $root "Musilib.sln") -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Les tests ont échoué : publication annulée." }
}

if (Test-Path $output) { Remove-Item $output -Recurse -Force }

Write-Host "-> Publication self-contained..." -ForegroundColor Cyan
dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $output
if ($LASTEXITCODE -ne 0) { throw "La publication a échoué." }

$exe = Join-Path $output "MusilibManager.exe"
if (-not (Test-Path $exe)) { throw "Exécutable introuvable : $exe" }
$sizeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "-> Exécutable : $exe ($sizeMb Mo)" -ForegroundColor Green

if (-not $NoZip) {
    $version = (Select-Xml -Path (Join-Path $root "Directory.Build.props") -XPath "//Version").Node.InnerText
    $zip = Join-Path $root "artifacts/MusilibManager-$version-$Runtime.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $output "*") -DestinationPath $zip
    Write-Host "-> Archive : $zip" -ForegroundColor Green
}

Write-Host "Terminé. Lancez MusilibManager.exe (ou MusilibManager.exe --demo pour le mode démonstration)." -ForegroundColor Green
