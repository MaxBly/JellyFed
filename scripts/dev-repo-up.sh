#!/usr/bin/env bash
# Démarre le dépôt plugin JellyFed sur le LAN : détecte l'IP locale, régénère
# manifest.local.json, lance Nginx (Docker) en avant-plan avec les logs dans la console.
#
# Usage :
#   ./scripts/dev-repo-up.sh                    # IP LAN détectée automatiquement
#   ./scripts/dev-repo-up.sh --restart          # idem, arrête d'abord le stack Docker s'il existe
#   ./scripts/dev-repo-up.sh http://IP:8765     # forcer l'URL de base
#   ./scripts/dev-repo-up.sh --restart http://IP:8765
#
# Variables :
#   JELLYFED_DEV_REPO_PORT  Port publié sur l'hôte (défaut 8765)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PORT="${JELLYFED_DEV_REPO_PORT:-8765}"

RESTART=false
if [[ "${1:-}" == "--restart" ]]; then
  RESTART=true
  shift
fi

MANUAL_URL="${1:-}"

if [[ -n "$MANUAL_URL" ]]; then
  BASE_URL="${MANUAL_URL%/}"
else
  LAN_IP="$(python3 "$REPO_ROOT/scripts/detect_lan_ip.py")" || exit 1
  BASE_URL="http://${LAN_IP}:${PORT}"
fi

if [[ ! "$BASE_URL" =~ ^https?:// ]]; then
  echo "Erreur : l'URL doit commencer par http:// ou https://" >&2
  exit 1
fi

REPO_URL="${BASE_URL}/manifest.local.json"

if [[ "$RESTART" == true ]]; then
  echo "==> Arrêt du dépôt Docker s'il est déjà actif (mise à jour)..."
  (cd "$REPO_ROOT/docker/dev-repo" && docker compose down --remove-orphans) || true
  echo ""
fi

python3 "$REPO_ROOT/scripts/generate-manifest-local.py" --base-url "$BASE_URL"

REPO_DIR="${REPO_ROOT}/repo"
export JELLYFED_REPO_DIR="$REPO_DIR"

if [[ ! -f "$REPO_DIR/manifest.local.json" ]]; then
  echo "Erreur : $REPO_DIR/manifest.local.json est introuvable après génération." >&2
  exit 1
fi

echo "==> Fichiers servis par Nginx (extrait) :"
ls -la "$REPO_DIR" | head -20 || true
echo ""

echo "═══════════════════════════════════════════════════════════════════"
echo "  Dépôt Jellyfin (Plugins → Repositories) — copier cette URL :"
echo ""
echo "    ${REPO_URL}"
echo ""
echo "  Base HTTP : ${BASE_URL}"
echo "  Port hôte : ${PORT} (écoute sur 0.0.0.0 — accessible sur le LAN)"
echo "═══════════════════════════════════════════════════════════════════"
echo ""
echo "Logs du conteneur (Ctrl+C pour arrêter le serveur) :"
echo "-------------------------------------------------------------------"

export JELLYFED_DEV_REPO_PORT="$PORT"
cd "$REPO_ROOT/docker/dev-repo"
exec docker compose up --remove-orphans
