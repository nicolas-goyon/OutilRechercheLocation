# Met à jour le serveur local : télécharge la dernière image et redémarre le conteneur.
# Les données (volume Docker) sont conservées. Usage (PowerShell, dans le dossier du docker-compose.yml) :
#   .\update.ps1
$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot
docker compose pull
if ($LASTEXITCODE -ne 0) { throw "docker compose pull a échoué" }
docker compose up -d
if ($LASTEXITCODE -ne 0) { throw "docker compose up a échoué" }
docker image prune -f --filter "label=org.opencontainers.image.title=recherche-logement-server" | Out-Null
$version = docker inspect -f '{{ index .Config.Labels "org.opencontainers.image.version" }}' recherche-logement 2>$null
Write-Host "Serveur à jour : $version"
$port = if ($env:RL_PORT) { $env:RL_PORT } else { '5080' }
Write-Host "http://localhost:$port"
