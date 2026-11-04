# Met à jour le serveur local : télécharge la dernière image, redémarre le conteneur et vérifie qu'il répond.
# Le serveur sauvegarde ses bases (dossier backups/ du volume) avant toute migration.
# Usage (PowerShell, dans le dossier du docker-compose.yml) :
#   .\update.ps1
$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

function Get-ServerVersion { docker inspect -f '{{ index .Config.Labels "org.opencontainers.image.version" }}' recherche-logement 2>$null }
function Get-ServerHealth { docker inspect -f '{{ if .State.Health }}{{ .State.Health.Status }}{{ else }}unknown{{ end }}' recherche-logement 2>$null }

$before = Get-ServerVersion
docker compose pull
if ($LASTEXITCODE -ne 0) { throw "docker compose pull a échoué" }
docker compose up -d
if ($LASTEXITCODE -ne 0) { throw "docker compose up a échoué" }

for ($i = 0; $i -lt 90 -and (Get-ServerHealth) -ne 'healthy'; $i++) { Start-Sleep -Seconds 2 }

if ((Get-ServerHealth) -ne 'healthy') {
    Write-Host "ÉCHEC : le serveur ne répond pas après la mise à jour. Dernières lignes du journal :"
    docker compose logs --tail 30 server
    Write-Host "Revenir à la version précédente : `$env:RL_TAG='$before'; docker compose up -d"
    Write-Host "Si la base a déjà été migrée, restaurer d'abord une sauvegarde (README, « Sauvegardes et restauration »)."
    exit 1
}

docker image prune -f --filter "label=org.opencontainers.image.title=recherche-logement-server" | Out-Null
Write-Host "Serveur à jour : $before -> $(Get-ServerVersion)"
$port = if ($env:RL_PORT) { $env:RL_PORT } else { '5080' }
Write-Host "http://localhost:$port"
