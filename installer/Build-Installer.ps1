<#
.SYNOPSIS
  Publica CA-A-IA (win-x64, self-contained) y genera el instalador Inno Setup firmado por CA.

.DESCRIPTION
  1. Lee la versión de Directory.Build.props (<Version>).
  2. dotnet publish del proyecto WinUI en Release.
  3. Firma Authenticode del exe (opcional, si hay certificado CA).
  4. Compila installer\CA-A-IA.iss con ISCC.

  Firma CA: define las variables de entorno CA_PFX_PATH (certificado .pfx del
  editor "CA") y CA_PFX_PASSWORD. Sin ellas, el instalador se genera igual
  pero sin firma Authenticode (los metadatos Company/Product siguen siendo CA).

.EXAMPLE
  .\installer\Build-Installer.ps1
  $env:CA_PFX_PATH="C:\certs\ca.pfx"; $env:CA_PFX_PASSWORD="..."; .\installer\Build-Installer.ps1
#>
[CmdletBinding()]
param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [switch]$SkipPublish,
  [string]$IsccPath = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root

# 1. Versión única: la de Directory.Build.props (el nodo <Version> no vacío).
$props = [xml](Get-Content -LiteralPath (Join-Path $root "Directory.Build.props"))
$version = @($props.Project.PropertyGroup.Version | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
  Select-Object -First 1 | ForEach-Object { $_.Trim() })
if (-not $version) { $version = "0.1.0" }
$version = [string]$version
Write-Host "CA-A-IA versión $version" -ForegroundColor Cyan

# 2. Publicar (limpiando antes: restos con nombres viejos acabarían en el instalador).
# NOTA: se compila con Rebuild a propósito — el target _GenerateProjectPriFile
# de WinUI puede saltarse por un up-to-date erróneo y empaquetar sin
# CA-A-IA.Presentation.pri (la app luego muere en MainWindow al no hallar su XAML).
$publishDir = Join-Path $root "publish\$Runtime"
$csproj = "src\CA-A-IA.Presentation\CA-A-IA.Presentation.csproj"
if (-not $SkipPublish) {
  Write-Host "Compilando (Rebuild $Configuration)..." -ForegroundColor Cyan
  # (Sin -p:DebugSymbols=false: publish --no-build espera el pdb; el instalador
  # ya excluye *.pdb vía Excludes.)
  dotnet build $csproj -c $Configuration -r $Runtime -t:Rebuild -p:SelfContained=true
  if ($LASTEXITCODE -ne 0) { throw "dotnet build falló." }
  Write-Host "Publicando (self-contained $Runtime)..." -ForegroundColor Cyan
  if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -Recurse -Force -LiteralPath $publishDir
  }
  dotnet publish $csproj -c $Configuration -r $Runtime --self-contained true `
    --no-build -o $publishDir
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló." }
}

# Puerta de calidad: sin el PRI de la app, el instalador quedaría roto (XAML
# ilocalizable → la ventana no abre). Fallar aquí, nunca en casa del usuario.
$pri = Join-Path $publishDir "CA-A-IA.Presentation.pri"
if (-not (Test-Path -LiteralPath $pri)) {
  # publish --no-build a veces no arrastra el PRI aunque el build lo generó:
  # copiarlo explícitamente desde bin.
  $builtPri = Join-Path $root "src\CA-A-IA.Presentation\bin\$Configuration\net10.0-windows10.0.19041.0\$Runtime\CA-A-IA.Presentation.pri"
  if (Test-Path -LiteralPath $builtPri) {
    Write-Host "Copiando PRI generado en build a la publicación..." -ForegroundColor Yellow
    Copy-Item -LiteralPath $builtPri -Destination $pri
  }
}
if (-not (Test-Path -LiteralPath $pri)) {
  throw "Falta $pri en la publicación: WinUI no generó los recursos XAML. Abortando."
}

$exe = Join-Path $publishDir "CA-A-IA.Presentation.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "No se encontró $exe. ¿Falló la publicación?" }

# 3. Localizar ISCC.
if ([string]::IsNullOrWhiteSpace($IsccPath)) {
  $cmd = Get-Command iscc -ErrorAction SilentlyContinue
  if ($cmd) { $IsccPath = $cmd.Source }
  foreach ($candidata in @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    (Join-Path ([System.Environment]::GetFolderPath("LocalApplicationData")) "Programs\Inno Setup 6\ISCC.exe"))) {
    if (-not $IsccPath -and (Test-Path -LiteralPath $candidata)) { $IsccPath = $candidata }
  }
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath)) {
  throw "ISCC.exe no encontrado. Instala Inno Setup 6 (https://jrsoftware.org/isinfo.php) o pasa -IsccPath."
}

# 4. Firma Authenticode (opcional).
$signArgs = @()
$pfx = $env:CA_PFX_PATH
if (-not [string]::IsNullOrWhiteSpace($pfx) -and (Test-Path -LiteralPath $pfx)) {
  $signtool = $null
  foreach ($sdk in @("C:\Program Files (x86)\Windows Kits\10\bin", "C:\Program Files\Windows Kits\10\bin")) {
    if (Test-Path -LiteralPath $sdk) {
      $signtool = Get-ChildItem -LiteralPath $sdk -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
      if ($signtool) { break }
    }
  }
  if (-not $signtool) { throw "Hay certificado CA pero no se encontró signtool.exe (instala el Windows SDK)." }
  $pwd = $env:CA_PFX_PASSWORD
  if ([string]::IsNullOrEmpty($pwd)) { throw "Define CA_PFX_PASSWORD para el certificado CA." }

  Write-Host "Firmando exe con el certificado CA..." -ForegroundColor Cyan
  & $signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f $pfx /p $pwd $exe
  if ($LASTEXITCODE -ne 0) { throw "La firma del exe falló." }

  $signArgs += '/DUSE_CODESIGN'
  $signArgs += "/S`"casign=$signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f $pfx /p `$q$pwd`$q `$f`""
}
else {
  Write-Warning "Sin CA_PFX_PATH: el instalador se generará SIN firma Authenticode."
}

# 5. Compilar instalador.
Write-Host "Compilando instalador..." -ForegroundColor Cyan
$iss = Join-Path $root "installer\CA-A-IA.iss"
& $IsccPath $iss "/DPublishDir=$publishDir" "/DAppVersion=$version" @signArgs
if ($LASTEXITCODE -ne 0) { throw "ISCC falló." }

$setup = Join-Path $root "installer\Output\CA-A-IA-Setup-$version.exe"
Write-Host "Listo: $setup" -ForegroundColor Green
Write-Host "Instálalo con doble clic. Accesos: menú Inicio > CA-A-IA, escritorio y Win+R > CA-A-IA." -ForegroundColor Green
