#!/usr/bin/env sh
# Met à jour le serveur local : télécharge la dernière image, redémarre le conteneur et vérifie qu'il répond.
# Le serveur sauvegarde ses bases (dossier backups/ du volume) avant toute migration.
# Usage : ./update.sh   (dans le dossier du docker-compose.yml)
set -eu
cd "$(dirname "$0")"
version() { docker inspect -f '{{ index .Config.Labels "org.opencontainers.image.version" }}' recherche-logement 2>/dev/null || echo '?'; }
health() { docker inspect -f '{{ if .State.Health }}{{ .State.Health.Status }}{{ else }}{{ .State.Status }}{{ end }}' recherche-logement 2>/dev/null || echo missing; }

before=$(version)
docker compose pull
docker compose up -d

# docker-compose.yml plus ancien, sans healthcheck : on accepte un conteneur encore en marche après 15 s.
ok=
i=0
while [ "$i" -lt 90 ] && [ -z "$ok" ]; do
  sleep 2
  state=$(health)
  if [ "$state" = healthy ] || { [ "$state" = running ] && [ "$i" -ge 7 ]; }; then ok=1; fi
  i=$((i + 1))
done

if [ -z "$ok" ]; then
  echo "ÉCHEC : le serveur ne répond pas après la mise à jour. Dernières lignes du journal :" >&2
  docker compose logs --tail 30 server >&2 || true
  echo "Revenir à la version précédente : RL_TAG=$before docker compose up -d" >&2
  echo "Si la base a déjà été migrée, restaurer d'abord une sauvegarde (README, « Sauvegardes et restauration »)." >&2
  exit 1
fi

docker image prune -f --filter "label=org.opencontainers.image.title=recherche-logement-server" >/dev/null
echo "Serveur à jour : $before -> $(version)"
echo "http://localhost:${RL_PORT:-5080}"
