<#
.SYNOPSIS
  Publica o Headroom (self-contained, single-file, win-x64) e, se o Inno Setup estiver instalado,
  gera o instalador.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\publish.ps1
  powershell -ExecutionPolicy Bypass -File build\publish.ps1 -SkipTests -SkipInstaller
#>
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root 'artifacts\publish'
$installerDir = Join-Path $root 'artifacts\installer'

Push-Location $root
try {
    if (-not $SkipTests) {
        Write-Host '>> Testes' -ForegroundColor Cyan
        dotnet test tests\Headroom.Tests\Headroom.Tests.csproj -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'Os testes falharam.' }
    }

    Write-Host '>> Publicando (self-contained, single-file, win-x64)' -ForegroundColor Cyan
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish src\Headroom.App\Headroom.App.csproj -c $Configuration -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou.' }

    $exe = Join-Path $publishDir 'Headroom.exe'
    Write-Host ("   {0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))

    if (-not $SkipInstaller) {
        $iscc = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1

        if ($iscc) {
            Write-Host '>> Gerando instalador (Inno Setup)' -ForegroundColor Cyan
            New-Item -ItemType Directory -Force $installerDir | Out-Null
            & $iscc "/DPublishDir=$publishDir" "/O$installerDir" (Join-Path $root 'installer\Headroom.iss')
            if ($LASTEXITCODE -ne 0) { throw 'O Inno Setup falhou.' }
        }
        else {
            Write-Warning 'Inno Setup 6 não encontrado (https://jrsoftware.org/isdl.php). O instalador não foi gerado.'
        }
    }
}
finally {
    Pop-Location
}
