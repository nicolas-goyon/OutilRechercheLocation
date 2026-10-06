#!/usr/bin/env sh
# Met à jour le serveur local : télécharge la dernière image et redémarre le conteneur.
# Les données (volume Docker) sont conservées. Usage : ./update.sh   (dans le dossier du docker-compose.yml)
set -eu
cd "$(dirname "$0")"
docker compose pull
docker compose up -d
docker image prune -f --filter "label=org.opencontainers.image.title=recherche-logement-server" >/dev/null
echo "Serveur à jour : $(docker inspect -f '{{ index .Config.Labels "org.opencontainers.image.version" }}' recherche-logement 2>/dev/null || echo '?')"
echo "http://localhost:${RL_PORT:-5080}"
