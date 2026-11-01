# Gera a versão para download: um Pomelo.exe único, sem precisar instalar o .NET.
# Uso: ./release.ps1            (versão do src/Pomelo/Pomelo.csproj)
# Saída: artifacts/Pomelo-<versão>-win-x64.zip
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$project = "src/Pomelo/Pomelo.csproj"
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "Defina <Version> em $project." }

$publish = "artifacts/publish"
Remove-Item -Recurse -Force artifacts -ErrorAction SilentlyContinue
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou." }

Copy-Item LICENSE, THIRD-PARTY-NOTICES.txt $publish
$zip = "artifacts/Pomelo-$version-win-x64.zip"
Compress-Archive -Path "$publish/*" -DestinationPath $zip
Get-Item $zip | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
