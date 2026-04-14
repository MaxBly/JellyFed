#!/usr/bin/env bash
# setup-vps.sh — Configuration initiale du repo JellyFed sur le VPS
# À exécuter UNE SEULE FOIS depuis blynk :
#   bash vps/setup-vps.sh
#
# Prérequis :
#   - DNS jellyfed.bly-net.com → 194.99.23.234 propagé
#   - SSH root@194.99.23.234 configuré

set -euo pipefail

VPS="root@194.99.23.234"
NGINX_CONF="$(dirname "${BASH_SOURCE[0]}")/jellyfed.bly-net.com.nginx"

echo "==> Création du dossier repo sur le VPS..."
ssh "$VPS" "mkdir -p /srv/jellyfed-repo"

echo "==> Déploiement de la config nginx..."
scp "$NGINX_CONF" "$VPS:/etc/nginx/sites-available/jellyfed.bly-net.com"
ssh "$VPS" "ln -sf /etc/nginx/sites-available/jellyfed.bly-net.com /etc/nginx/sites-enabled/jellyfed.bly-net.com"
ssh "$VPS" "nginx -t && systemctl reload nginx"
echo "    nginx rechargé (HTTP seulement pour l'instant)"

echo ""
echo "==> Obtention du certificat SSL Let's Encrypt..."
ssh "$VPS" "certbot --nginx -d jellyfed.bly-net.com --non-interactive --agree-tos -m admin@bly-net.com"
echo "    Certificat obtenu"

echo ""
echo "==> Setup terminé !"
echo "    URL repo : https://jellyfed.bly-net.com/repo/manifest.json"
echo ""
echo "==> Prochaine étape : déployer les fichiers du repo"
echo "    ./scripts/build-repo.sh --deploy"
