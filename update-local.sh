#!/usr/bin/env sh
# Met à jour une installation construite depuis les sources (docker-compose.yml de la racine).
set -eu
cd "$(dirname "$0")"
git pull --ff-only
docker compose up -d --build --wait --wait-timeout 180 || {
  echo "ÉCHEC : le serveur ne répond pas. Dernières lignes du journal :" >&2
  docker compose logs --tail 30 server >&2
  exit 1
}
echo "Serveur à jour : http://localhost:5080"
