#!/usr/bin/env bash
# build-repo.sh — Compile JellyFed, empaquète le ZIP, met à jour manifest.json et déploie sur le VPS
# Usage : ./scripts/build-repo.sh [--deploy]
#   --deploy : rsync vers le VPS après la génération (nécessite SSH configuré)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/Jellyfin.Plugin.JellyFed"
REPO_DIR="$REPO_ROOT/repo"
VPS_USER="root"
VPS_HOST="194.99.23.234"
VPS_PATH="/srv/jellyfed-repo"
DEPLOY=false

# --- Options ---
for arg in "$@"; do
  case $arg in
    --deploy) DEPLOY=true ;;
    *) echo "Option inconnue : $arg" && exit 1 ;;
  esac
done

# --- Lire la version depuis build.yaml ---
VERSION=$(grep '^version:' "$REPO_ROOT/build.yaml" | awk '{print $2}' | tr -d '"')
TARGET_ABI=$(grep '^targetAbi:' "$REPO_ROOT/build.yaml" | awk '{print $2}' | tr -d '"')
PLUGIN_NAME=$(grep '^name:' "$REPO_ROOT/build.yaml" | awk '{print $2}' | tr -d '"')
PLUGIN_GUID=$(grep '^guid:' "$REPO_ROOT/build.yaml" | awk '{print $2}' | tr -d '"')
PLUGIN_OVERVIEW=$(grep '^overview:' "$REPO_ROOT/build.yaml" | sed 's/^overview: *//' | tr -d '"')

echo "==> Build JellyFed $VERSION (targetAbi $TARGET_ABI)"

# --- Compilation ---
dotnet build "$PROJECT" -c Release -o "$REPO_DIR/build" --nologo -v quiet
echo "    Compilation OK"

# --- Empaquetage ZIP ---
ZIP_NAME="jellyfed_${VERSION}.zip"
ZIP_PATH="$REPO_DIR/$ZIP_NAME"
rm -f "$ZIP_PATH"
cd "$REPO_DIR/build"
zip -q "$ZIP_PATH" "Jellyfin.Plugin.JellyFed.dll"
cd "$REPO_ROOT"
echo "    ZIP : $ZIP_NAME"

# --- Checksum MD5 ---
CHECKSUM=$(md5sum "$ZIP_PATH" | awk '{print $1}')
echo "    MD5 : $CHECKSUM"

# --- Timestamp ---
TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%S.0000000Z")

# --- Lire le manifest existant ou créer un squelette ---
MANIFEST="$REPO_DIR/manifest.json"

if [[ ! -f "$MANIFEST" ]]; then
  # Premier build — créer le manifest from scratch
  cat > "$MANIFEST" << MANIFEST_EOF
[
  {
    "category": "General",
    "guid": "$PLUGIN_GUID",
    "name": "$PLUGIN_NAME",
    "description": "JellyFed connects multiple Jellyfin servers together. Libraries from federated instances appear as native items — with artwork, metadata and direct streaming — in any Jellyfin client, without modification.",
    "overview": "$PLUGIN_OVERVIEW",
    "owner": "MaxBly",
    "targetAbi": "$TARGET_ABI",
    "timestamp": "$TIMESTAMP",
    "versions": []
  }
]
MANIFEST_EOF
  echo "    Manifest créé"
fi

# --- Mettre à jour / insérer la version dans manifest.json (via Python) ---
python3 - <<PYEOF
import json, sys

manifest_path = "$MANIFEST"
version = "$VERSION"
checksum = "$CHECKSUM"
timestamp = "$TIMESTAMP"
target_abi = "$TARGET_ABI"
source_url = "https://jellyfed.bly-net.com/repo/$ZIP_NAME"
changelog_default = "Release $VERSION."

with open(manifest_path) as f:
    manifest = json.load(f)

plugin = manifest[0]

# Mettre à jour le timestamp global
plugin["timestamp"] = timestamp

new_version = {
    "version": version,
    "changelog": changelog_default,
    "targetAbi": target_abi,
    "sourceUrl": source_url,
    "checksum": checksum,
    "timestamp": timestamp
}

# Remplacer si la version existe déjà, sinon insérer en tête
versions = plugin.get("versions", [])
replaced = False
for i, v in enumerate(versions):
    if v["version"] == version:
        versions[i] = new_version
        replaced = True
        break

if not replaced:
    versions.insert(0, new_version)

plugin["versions"] = versions

with open(manifest_path, "w") as f:
    json.dump(manifest, f, indent=2, ensure_ascii=False)
    f.write("\n")

print(f"    Manifest mis à jour : version {version} {'(remplacée)' if replaced else '(ajoutée)'}")
PYEOF

# --- Nettoyer le dossier de build intermédiaire ---
rm -rf "$REPO_DIR/build"

echo ""
echo "==> Fichiers générés dans $REPO_DIR :"
ls -lh "$REPO_DIR/"

# --- Déploiement VPS ---
if [[ "$DEPLOY" == true ]]; then
  echo ""
  echo "==> Déploiement sur $VPS_HOST:$VPS_PATH ..."
  ssh "$VPS_USER@$VPS_HOST" "mkdir -p $VPS_PATH"
  rsync -avz --progress "$REPO_DIR/" "$VPS_USER@$VPS_HOST:$VPS_PATH/"
  echo "    Déploiement OK"
  echo ""
  echo "==> URL du repo Jellyfin :"
  echo "    https://jellyfed.bly-net.com/repo/manifest.json"
else
  echo ""
  echo "==> Pour déployer sur le VPS :"
  echo "    ./scripts/build-repo.sh --deploy"
fi
