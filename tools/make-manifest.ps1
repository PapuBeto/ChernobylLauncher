# genera un manifest a partir de una carpeta de mods (sha256, tamano y link de descarga)
# uso: .\tools\make-manifest.ps1 -ModsFolder modpacks\completa -Output manifest\completa.json -Version 1.0.0
param(
    [Parameter(Mandatory = $true)][string]$ModsFolder,
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$Version = "1.0.0",
    [string]$BaseUrl = "https://github.com/PapuBeto/ChernobylLauncher/releases/download/mods"
)

Add-Type -AssemblyName System.IO.Compression.FileSystem

$jars = Get-ChildItem -Path $ModsFolder -Filter *.jar -File | Sort-Object Name
if (-not $jars) {
    Write-Host "no hay .jar en $ModsFolder, wey" -ForegroundColor Red
    exit 1
}

$mods = @()
$problemas = 0

foreach ($jar in $jars) {
    # github cambia espacios y simbolos al subir, asi que solo dejamos nombres seguros
    if ($jar.Name -notmatch '^[A-Za-z0-9._\-]+$') {
        Write-Host "NOMBRE RARO (renombralo sin espacios ni simbolos): $($jar.Name)" -ForegroundColor Red
        $problemas++
        continue
    }

    # un jar es un zip, si no abre como zip forge se muere al arrancar
    try {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($jar.FullName)
        $zip.Dispose()
    }
    catch {
        Write-Host "NO ES UN JAR VALIDO: $($jar.Name)" -ForegroundColor Red
        $problemas++
        continue
    }

    $hash = (Get-FileHash -Path $jar.FullName -Algorithm SHA256).Hash.ToLower()

    $mods += [ordered]@{
        fileName    = $jar.Name
        downloadUrl = "$BaseUrl/$($jar.Name)"
        sha256      = $hash
        sizeBytes   = $jar.Length
    }
}

if ($problemas -gt 0) {
    Write-Host "arregla los $problemas problema(s) de arriba y vuelve a correrlo, no genere nada" -ForegroundColor Yellow
    exit 1
}

$manifest = [ordered]@{
    modpackVersion = $Version
    mods           = @($mods)
}

# sin BOM, para que el launcher lo lea sin problemas
$json = $manifest | ConvertTo-Json -Depth 5
$ruta = Join-Path (Get-Location) $Output
New-Item -ItemType Directory -Force -Path (Split-Path $ruta) | Out-Null
[System.IO.File]::WriteAllText($ruta, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "listo: $($mods.Count) mods en $Output" -ForegroundColor Green