#!/usr/bin/env python3
"""Génère repo/manifest.local.json à partir de repo/manifest.json.

Remplace les sourceUrl des versions par {base_url}/{nom-du-zip}, pour que
Jellyfin télécharge le ZIP depuis votre machine (ex. Docker nginx sur le LAN)
sans modifier le manifest versionné ni build-repo.sh.

Ajoute des libellés « dev » (overview / changelog) pour distinguer le dépôt LAN
dans l’interface Jellyfin — voir docs/dev-local-repo.md.

Force la version catalogue de l’entrée la plus récente à
`<major>.<minor>.<patch>.<stamp>` à partir de `build.yaml`.
Cela permet à Jellyfin de proposer l’upgrade à chaque `make dev`, tout en
conservant une version dev locale lisible et alignée sur la vraie base du code.
Un lien symbolique `repo/jellyfed_<version>.zip` pointe sur le ZIP réel pour
aligner nom de fichier et numéro.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
from pathlib import Path
from urllib.parse import urlparse

_DEV_OVERVIEW_PREFIX = "[Dépôt LAN · dev] "
_DEV_CHANGELOG_PREFIX = "[Dev local] "
_MAIN_CHANGELOG_PREFIX = "[Main release] "
# Base du schéma dev local, dérivée de build.yaml.
# Le dernier composant encode l'état courant du code source.
# - repo clean: timestamp Unix du commit HEAD
# - repo dirty: timestamp courant (strictement > au commit, donc plus récent)
# Ainsi la version exposée par le manifest local suit réellement l'état du dossier JellyFed.


def _git_output(root: Path, *args: str) -> str | None:
    try:
        proc = subprocess.run(
            ['git', *args],
            cwd=root,
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
        )
        return proc.stdout.strip()
    except (OSError, subprocess.CalledProcessError):
        return None


def _build_version_prefix(root: Path) -> str:
    build_yaml = root / 'build.yaml'
    for line in build_yaml.read_text(encoding='utf-8').splitlines():
        if line.startswith('version:'):
            raw = line.split(':', 1)[1].strip().strip('"')
            parts = [p for p in raw.split('.') if p]
            if len(parts) >= 3 and all(p.isdigit() for p in parts[:3]):
                return '.'.join(parts[:3])
            break
    return '0.0.0'


def _compute_local_catalog_version(root: Path) -> tuple[str, str]:
    """Version dev liée au code source courant.

    Retourne `(version, source_note)` où `version` est `<base_build_version>.<stamp>`.
    - Si le repo git est clean, `<stamp>` = timestamp Unix du commit HEAD.
    - Si le repo a des modifs non commit, `<stamp>` = max(now, head_ts + 1).
    - Si git n'est pas dispo, fallback sur `time.time()`.
    """
    head_ts_raw = _git_output(root, 'log', '-1', '--format=%ct', 'HEAD')
    head_sha = _git_output(root, 'rev-parse', '--short', 'HEAD')
    dirty = bool(_git_output(root, 'status', '--porcelain'))

    try:
        head_ts = int(head_ts_raw) if head_ts_raw else 0
    except ValueError:
        head_ts = 0

    now_ts = int(time.time())
    if head_ts > 0:
        stamp = max(now_ts, head_ts + 1) if dirty else head_ts
        note = f"git {head_sha or 'HEAD'} ({'dirty' if dirty else 'clean'})"
    else:
        stamp = now_ts
        note = 'no-git-fallback'

    return f"{_build_version_prefix(root)}.{stamp}", note


def _strip_known_prefixes(text: str) -> str:
    prefixes = (_DEV_CHANGELOG_PREFIX, _MAIN_CHANGELOG_PREFIX, '[Build locale] ')
    out = text
    changed = True
    while changed:
        changed = False
        for prefix in prefixes:
            if out.startswith(prefix):
                out = out[len(prefix):].lstrip()
                changed = True
    return out


def _apply_dev_labels(plugin: dict, versions: list) -> None:
    ov = plugin.get("overview")
    if isinstance(ov, str) and ov and not ov.startswith(_DEV_OVERVIEW_PREFIX):
        plugin["overview"] = _DEV_OVERVIEW_PREFIX + ov.removeprefix(_DEV_OVERVIEW_PREFIX)
    elif not isinstance(ov, str) or not ov:
        plugin["overview"] = _DEV_OVERVIEW_PREFIX.strip()

    for i, v in enumerate(versions):
        if not isinstance(v, dict):
            continue
        ch = v.get("changelog")
        base = _strip_known_prefixes(ch) if isinstance(ch, str) and ch else ''
        if i == 0:
            # une seule version dev locale : la plus récente
            v["changelog"] = base or 'Build locale.'
        else:
            v["changelog"] = f"{_MAIN_CHANGELOG_PREFIX}{base}" if base else _MAIN_CHANGELOG_PREFIX.strip()


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Écrit repo/manifest.local.json avec des sourceUrl pointant vers une base HTTP locale."
    )
    parser.add_argument(
        "--base-url",
        required=True,
        help="URL de base accessible depuis le serveur Jellyfin, sans slash final "
        "(ex. http://192.168.1.42:8765). Doit correspondre au port exposé par Docker.",
    )
    parser.add_argument(
        "--repo-dir",
        default=None,
        help="Chemin du dossier repo/ (défaut : <racine du dépôt>/repo).",
    )
    args = parser.parse_args()

    base = args.base_url.rstrip("/")
    if not base.lower().startswith(("http://", "https://")):
        print("Erreur : --base-url doit commencer par http:// ou https://", file=sys.stderr)
        return 1

    script_dir = Path(__file__).resolve().parent
    root = script_dir.parent
    repo_dir = Path(args.repo_dir).resolve() if args.repo_dir else root / "repo"
    src = repo_dir / "manifest.json"
    dst = repo_dir / "manifest.local.json"

    if not src.is_file():
        print(f"Erreur : fichier introuvable : {src}", file=sys.stderr)
        return 1

    with src.open(encoding="utf-8") as f:
        manifest = json.load(f)

    if not isinstance(manifest, list) or not manifest:
        print("Erreur : manifest.json doit être un tableau JSON non vide.", file=sys.stderr)
        return 1

    plugin = manifest[0]
    versions = plugin.get("versions")
    if not isinstance(versions, list):
        print("Erreur : clé 'versions' absente ou invalide.", file=sys.stderr)
        return 1

    # manifest.local.json sert uniquement au dépôt LAN : libellés explicites dans l’UI Jellyfin.
    _apply_dev_labels(plugin, versions)

    # Version catalogue LAN liée à l'état courant du code source — réutilisée à la fois
    # pour le champ `version`, le symlink et le changelog.
    local_catalog_version, source_note = _compute_local_catalog_version(root)

    missing: list[str] = []
    for v in versions:
        if not isinstance(v, dict):
            continue
        url = v.get("sourceUrl")
        if not isinstance(url, str) or not url:
            continue
        path = urlparse(url).path
        name = path.rsplit("/", 1)[-1] if path else ""
        if not name.endswith(".zip"):
            print(f"Avertissement : sourceUrl inattendu, ignoré : {url}", file=sys.stderr)
            continue
        zip_path = repo_dir / name
        if not zip_path.is_file():
            missing.append(str(zip_path))
        v["sourceUrl"] = f"{base}/{name}"

    if missing:
        print("Avertissement : ZIP absents du dossier repo/ (lancez ./scripts/build-repo.sh d'abord) :", file=sys.stderr)
        for m in missing:
            print(f"  - {m}", file=sys.stderr)

    if versions and isinstance(versions[0], dict):
        versions[0]["version"] = local_catalog_version
        v0 = versions[0]
        su0 = v0.get("sourceUrl")
        if isinstance(su0, str):
            path0 = urlparse(su0).path
            real_name = path0.rsplit("/", 1)[-1] if path0 else ""
            alias_name = f"jellyfed_{local_catalog_version}.zip"
            if real_name.endswith(".zip") and real_name != alias_name:
                real_path = repo_dir / real_name
                alias_path = repo_dir / alias_name
                if real_path.is_file():
                    # Purge des alias dev précédents (`jellyfed_1.3.3.<epoch>.zip` symlinks) pour
                    # éviter qu'ils s'accumulent à chaque `make dev`. On ne touche qu'aux symlinks,
                    # jamais aux ZIP réels produits par build-repo.sh.
                    local_prefix = f"jellyfed_{_build_version_prefix(root)}."
                    for old in repo_dir.glob("jellyfed_*.zip"):
                        if old.name == alias_name or old.name == real_name:
                            continue
                        if old.is_symlink() and old.name.startswith(local_prefix):
                            try:
                                old.unlink()
                            except OSError:
                                pass
                    try:
                        if alias_path.exists() or alias_path.is_symlink():
                            alias_path.unlink()
                        alias_path.symlink_to(real_name)
                        v0["sourceUrl"] = f"{base}/{alias_name}"
                        print(f"Lien symbolique : {alias_name} → {real_name}", file=sys.stderr)
                    except OSError as exc:
                        print(
                            f"Avertissement : impossible de créer {alias_name} → {real_name} : {exc}",
                            file=sys.stderr,
                        )

        # Changelog catalogue : plus de « Release 0.1.0.0 » figé — lien explicite version LAN + ZIP de build.
        artifact_stem = ""
        su_final = v0.get("sourceUrl", "")
        if isinstance(su_final, str):
            bn_final = urlparse(su_final).path.rsplit("/", 1)[-1] if urlparse(su_final).path else ""
            if bn_final:
                pzip = repo_dir / bn_final
                try:
                    if pzip.is_symlink():
                        tgt = pzip.readlink()
                        if not tgt.is_absolute():
                            tgt = pzip.parent / tgt
                        tn = tgt.name
                    else:
                        tn = bn_final
                    if tn.startswith("jellyfed_") and tn.endswith(".zip"):
                        artifact_stem = tn[len("jellyfed_") : -len(".zip")]
                except OSError:
                    pass
        base_changelog = _strip_known_prefixes(v0.get("changelog", ""))
        details = (
            f"JellyFed {local_catalog_version} — binaire issu du ZIP jellyfed_{artifact_stem or '?'}.zip "
            f"(source: {source_note}; build-repo.sh / build.yaml)."
        )
        if base_changelog:
            v0["changelog"] = f"{_DEV_CHANGELOG_PREFIX}{base_changelog} {details}"
        else:
            v0["changelog"] = f"{_DEV_CHANGELOG_PREFIX}{details}"

    with dst.open("w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")

    listed = [str(v["version"]) for v in versions if isinstance(v, dict) and v.get("version")]
    vers_line = ", ".join(listed) if listed else "(aucune entrée version)"

    print(f"Écrit : {dst}")
    print(
        f"Version(s) dans le manifest local : {vers_line}  "
        f"(entrée la plus récente = {local_catalog_version} ; sourceUrl du ZIP → voir manifest)"
    )
    print(
        "Overview [Dépôt LAN · dev] ; changelog entrée principale = [Dev local] + version catalogue "
        f"{local_catalog_version} + référence au ZIP de build."
    )
    print(
        "Version dev alignée sur l'état courant du dossier source "
        f"({source_note}) ; si le repo est dirty, un stamp plus récent est exposé."
    )
    print(f"URL dépôt Jellyfin (à coller dans Dashboard → Plugins → Repositories) :")
    print(f"  {base}/manifest.local.json")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
