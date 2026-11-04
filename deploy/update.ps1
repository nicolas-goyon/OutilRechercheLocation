# Met à jour le serveur local : télécharge la dernière image, redémarre le conteneur et vérifie qu'il répond.
# Le serveur sauvegarde ses bases (dossier backups/ du volume) avant toute migration.
# Usage (PowerShell, dans le dossier du docker-compose.yml) :
#   .\update.ps1
$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

# Modèles sans guillemets doubles : Windows PowerShell 5.1 les retire des arguments passés à docker.
# ErrorActionPreference local : avec 'Stop', le moindre message d'erreur de docker arrêterait le script (conteneur absent).
function Get-ServerVersion {
    $ErrorActionPreference = 'Continue'
    docker inspect -f '{{ index .Config.Labels `org.opencontainers.image.version` }}' recherche-logement 2>$null
}
function Get-ServerHealth {
    $ErrorActionPreference = 'Continue'
    docker inspect -f '{{ if .State.Health }}{{ .State.Health.Status }}{{ else }}{{ .State.Status }}{{ end }}' recherche-logement 2>$null
}

$before = Get-ServerVersion
docker compose pull
if ($LASTEXITCODE -ne 0) { throw "docker compose pull a échoué" }
docker compose up -d
if ($LASTEXITCODE -ne 0) { throw "docker compose up a échoué" }

# docker-compose.yml plus ancien, sans healthcheck : on accepte un conteneur encore en marche après 15 s.
$healthy = $false
for ($i = 0; $i -lt 90 -and -not $healthy; $i++) {
    Start-Sleep -Seconds 2
    $state = Get-ServerHealth
    $healthy = $state -eq 'healthy' -or ($state -eq 'running' -and $i -ge 7)
}

if (-not $healthy) {
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
