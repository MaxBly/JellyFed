#!/usr/bin/env bash
# build-repo.sh — Compile JellyFed, empaquète le ZIP, met à jour manifest.json et déploie sur le VPS
# Usage : ./scripts/build-repo.sh [--deploy]
#   --deploy : rsync vers le VPS après la génération (nécessite SSH configuré)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/Jellyfin.Plugin.JellyFed"
REPO_DIR="$REPO_ROOT/repo"
VPS="vps-blynet"
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
BUILD_YAML="$REPO_ROOT/build.yaml"

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
import json
import re
import subprocess
from pathlib import Path
from urllib.parse import urlparse

manifest_path = Path("$MANIFEST")
repo_dir = manifest_path.parent
repo_root = Path("$REPO_ROOT")
build_yaml_path = Path("$BUILD_YAML")
version = "$VERSION"
checksum = "$CHECKSUM"
timestamp = "$TIMESTAMP"
target_abi = "$TARGET_ABI"
source_url = "https://jellyfed.bly-net.com/repo/$ZIP_NAME"


def load_build_yaml_changelog(path: Path) -> str:
    lines = path.read_text(encoding='utf-8').splitlines()
    capture = False
    out: list[str] = []
    for line in lines:
        if not capture:
            if re.match(r'^changelog:\s*(>|\|)?\s*$', line):
                capture = True
            continue
        if line.startswith(' ') or line.startswith('\t'):
            out.append(line.lstrip())
            continue
        break
    text = '\n'.join(out).strip()
    return text


def git_fallback_changelog(root: Path) -> str:
    try:
        proc = subprocess.run(
            ['git', 'log', '-1', '--pretty=%s%n%b', 'HEAD'],
            cwd=root,
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
        )
        text = proc.stdout.strip()
        if text:
            return text
    except (OSError, subprocess.CalledProcessError):
        pass
    return f'Build {version}.'


def normalize_release_changelog(raw: str, label: str) -> str:
    text = ' '.join(part.strip() for part in raw.splitlines() if part.strip()).strip()
    if not text:
        text = f'Build {version}.'
    text = re.sub(r'\s+', ' ', text)
    return f'[{label}] {text}'


changelog_text = load_build_yaml_changelog(build_yaml_path) or git_fallback_changelog(repo_root)
changelog_main = normalize_release_changelog(changelog_text, 'Main release')

with manifest_path.open() as f:
    manifest = json.load(f)

plugin = manifest[0]
plugin["timestamp"] = timestamp

new_version = {
    "version": version,
    "changelog": changelog_main,
    "targetAbi": target_abi,
    "sourceUrl": source_url,
    "checksum": checksum,
    "timestamp": timestamp
}

versions = plugin.get("versions", [])
replaced = False
for i, v in enumerate(versions):
    if v.get("version") == version:
        versions[i] = new_version
        replaced = True
        break

if not replaced:
    versions.insert(0, new_version)

for v in versions:
    ch = v.get('changelog')
    if isinstance(ch, str) and ch.strip() and not ch.startswith('['):
        v['changelog'] = normalize_release_changelog(ch, 'Main release')


def zip_exists(v: dict) -> bool:
    url = v.get("sourceUrl")
    if not isinstance(url, str) or not url:
        return True
    name = Path(urlparse(url).path).name
    if not name.endswith('.zip'):
        return True
    return (repo_dir / name).is_file()

before = len(versions)
versions = [v for v in versions if zip_exists(v)]
removed = before - len(versions)
plugin["versions"] = versions

with manifest_path.open("w") as f:
    json.dump(manifest, f, indent=2, ensure_ascii=False)
    f.write("\n")

status = 'remplacée' if replaced else 'ajoutée'
if removed:
    print(f"    Manifest mis à jour : version {version} ({status}), {removed} ancienne(s) entrée(s) supprimée(s)")
else:
    print(f"    Manifest mis à jour : version {version} ({status})")
PYEOF

# --- Nettoyer le dossier de build intermédiaire ---
rm -rf "$REPO_DIR/build"

echo ""
echo "==> Fichiers générés dans $REPO_DIR :"
ls -lh "$REPO_DIR/"

# --- Déploiement VPS ---
if [[ "$DEPLOY" == true ]]; then
  echo ""
  echo "==> Déploiement sur $VPS:$VPS_PATH ..."
  ssh "$VPS" "mkdir -p $VPS_PATH"
  scp -O "$REPO_DIR/manifest.json" "$VPS:$VPS_PATH/manifest.json"
  scp -O "$REPO_DIR/$ZIP_NAME" "$VPS:$VPS_PATH/$ZIP_NAME"
  echo "    Déploiement OK"
  echo ""
  echo "==> URL du repo Jellyfin :"
  echo "    https://jellyfed.bly-net.com/repo/manifest.json"
else
  echo ""
  echo "==> Pour déployer sur le VPS :"
  echo "    ./scripts/build-repo.sh --deploy"
fi
