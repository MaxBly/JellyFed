#!/usr/bin/env python3
"""Génère repo/manifest.local.json à partir de repo/manifest.json.

Remplace les sourceUrl des versions par {base_url}/{nom-du-zip}, pour que
Jellyfin télécharge le ZIP depuis votre machine (ex. Docker nginx sur le LAN)
sans modifier le manifest versionné ni build-repo.sh.

Ajoute des libellés « dev » (overview / changelog) pour distinguer le dépôt LAN
dans l’interface Jellyfin — voir docs/dev-local-repo.md.

Force la version catalogue de l’entrée la plus récente à `1.3.3.<epoch_seconds>`
(strictement monotone à chaque build). Cela permet à Jellyfin de proposer
l’upgrade à chaque `make dev`, même si le binaire du ZIP sous-jacent ne bump
pas `build.yaml`. Un lien symbolique `repo/jellyfed_<version>.zip` pointe sur
le ZIP réel pour aligner nom de fichier et numéro.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path
from urllib.parse import urlparse

_DEV_OVERVIEW_PREFIX = "[Dépôt LAN · dev] "
_DEV_CHANGELOG_PREFIX = "[Build locale] "
# Prefix fixe du schéma dev. Le dernier composant est l'epoch Unix (strictement croissant)
# → chaque `make dev` produit une version plus grande que la précédente et Jellyfin propose
# systématiquement l'upgrade, même sur une instance qui avait déjà installé une version dev.
_LOCAL_VERSION_PREFIX = "1.3.3"


def _compute_local_catalog_version() -> str:
    """Version dev strictement monotone : `1.3.3.<epoch_seconds>`.

    L'epoch Unix tient dans Int32 (max 2 147 483 647 ≈ année 2038), donc
    System.Version (utilisé par Jellyfin) accepte sans débordement.
    """
    return f"{_LOCAL_VERSION_PREFIX}.{int(time.time())}"


def _apply_dev_labels(plugin: dict, versions: list) -> None:
    ov = plugin.get("overview")
    if isinstance(ov, str) and ov and not ov.startswith(_DEV_OVERVIEW_PREFIX):
        plugin["overview"] = _DEV_OVERVIEW_PREFIX + ov
    elif not isinstance(ov, str) or not ov:
        plugin["overview"] = _DEV_OVERVIEW_PREFIX.strip()

    for i, v in enumerate(versions):
        if not isinstance(v, dict):
            continue
        # L’entrée la plus récente : changelog réécrit plus bas (aligné sur la version catalogue LAN).
        if i == 0:
            continue
        ch = v.get("changelog")
        if isinstance(ch, str) and ch and not ch.startswith(_DEV_CHANGELOG_PREFIX):
            v["changelog"] = _DEV_CHANGELOG_PREFIX + ch
        elif not isinstance(ch, str) or not ch:
            v["changelog"] = _DEV_CHANGELOG_PREFIX.strip()


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

    # Version catalogue LAN (strictement monotone grâce à l'epoch) — calculée ici et réutilisée
    # à la fois pour le champ `version`, le symlink et le changelog.
    local_catalog_version = _compute_local_catalog_version()

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
                    prefix = f"jellyfed_{_LOCAL_VERSION_PREFIX}."
                    for old in repo_dir.glob("jellyfed_*.zip"):
                        if old.name == alias_name or old.name == real_name:
                            continue
                        if old.is_symlink() and old.name.startswith(prefix):
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
        v0["changelog"] = (
            f"[Build locale] JellyFed {local_catalog_version} — "
            f"binaire issu du ZIP jellyfed_{artifact_stem or '?'}.zip (build-repo.sh / build.yaml)."
        )

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
        "Overview [Dépôt LAN · dev] ; changelog entrée principale = version catalogue "
        f"{local_catalog_version} + référence au ZIP de build."
    )
    print(
        "Version dev strictement monotone (epoch Unix en dernier composant) → chaque "
        "`make dev` déclenche un upgrade côté Jellyfin même sans bump de build.yaml."
    )
    print(f"URL dépôt Jellyfin (à coller dans Dashboard → Plugins → Repositories) :")
    print(f"  {base}/manifest.local.json")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
