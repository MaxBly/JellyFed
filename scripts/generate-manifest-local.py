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
from urllib.error import URLError
from urllib.parse import urlparse
from urllib.request import Request, urlopen

_DEV_OVERVIEW_PREFIX = "[Dépôt LAN · dev] "
_DEV_CHANGELOG_PREFIX = "[Dev local] "
_MAIN_CHANGELOG_PREFIX = "[Main release] "
_DEFAULT_MAIN_MANIFEST_URL = "https://jellyfed.bly-net.com/repo/manifest.json"
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


def _fetch_latest_main_version(manifest_url: str, plugin_guid: str) -> dict | None:
    try:
        req = Request(manifest_url, headers={"User-Agent": "JellyFed dev-manifest generator"})
        with urlopen(req, timeout=10) as resp:
            payload = json.load(resp)
    except (OSError, URLError, TimeoutError, json.JSONDecodeError):
        return None

    if not isinstance(payload, list):
        return None

    for plugin in payload:
        if not isinstance(plugin, dict):
            continue
        if plugin.get("guid") != plugin_guid:
            continue
        versions = plugin.get("versions")
        if isinstance(versions, list):
            for version in versions:
                if isinstance(version, dict) and version.get("version"):
                    return dict(version)
    return None


def _load_latest_main_version_from_git(root: Path, plugin_guid: str, ref: str) -> dict | None:
    try:
        proc = subprocess.run(
            ['git', 'show', f'{ref}:repo/manifest.json'],
            cwd=root,
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
        )
        payload = json.loads(proc.stdout)
    except (OSError, subprocess.CalledProcessError, json.JSONDecodeError):
        return None

    if not isinstance(payload, list):
        return None

    for plugin in payload:
        if not isinstance(plugin, dict):
            continue
        if plugin.get('guid') != plugin_guid:
            continue
        versions = plugin.get('versions')
        if isinstance(versions, list):
            for version in versions:
                if isinstance(version, dict) and version.get('version'):
                    return dict(version)
    return None


def _resolve_latest_main_version(root: Path, plugin_guid: str, manifest_url: str) -> dict | None:
    for ref in ('upstream/main', 'origin/main', 'main'):
        version = _load_latest_main_version_from_git(root, plugin_guid, ref)
        if version is not None:
            return version
    return _fetch_latest_main_version(manifest_url, plugin_guid)


def _prefer_local_source_url(version_entry: dict, base_url: str, repo_dir: Path) -> dict:
    out = dict(version_entry)
    url = out.get("sourceUrl")
    if not isinstance(url, str) or not url:
        return out
    parsed = urlparse(url)
    name = parsed.path.rsplit("/", 1)[-1] if parsed.path else ""
    if name.endswith(".zip") and (repo_dir / name).is_file():
        out["sourceUrl"] = f"{base_url}/{name}"
    return out


def _upsert_after_latest_dev(versions: list, version_entry: dict) -> list:
    version = version_entry.get("version")
    if not version:
        return versions
    head = versions[:1]
    tail = [v for v in versions[1:] if not (isinstance(v, dict) and v.get("version") == version)]
    filtered = head + tail
    insert_at = 1 if head else 0
    filtered.insert(insert_at, version_entry)
    return filtered


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
    parser.add_argument(
        "--main-manifest-url",
        default=_DEFAULT_MAIN_MANIFEST_URL,
        help=(
            "Manifest public servant de source de vérité pour la dernière version main "
            f"(défaut : {_DEFAULT_MAIN_MANIFEST_URL})."
        ),
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

    latest_main = _resolve_latest_main_version(root, str(plugin.get("guid", "")), args.main_manifest_url)
    if latest_main is not None:
        latest_main = _prefer_local_source_url(latest_main, base, repo_dir)
        main_changelog = latest_main.get("changelog")
        if isinstance(main_changelog, str):
            latest_main["changelog"] = f"{_MAIN_CHANGELOG_PREFIX}{_strip_known_prefixes(main_changelog)}"

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
            continue
        v["sourceUrl"] = f"{base}/{name}"

    if missing:
        print(
            "Avertissement : ZIP absents du dossier repo/ ; les entrées concernées gardent leur sourceUrl d'origine :",
            file=sys.stderr,
        )
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

    if latest_main is not None:
        versions[:] = _upsert_after_latest_dev(versions, latest_main)

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
