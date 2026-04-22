#!/usr/bin/env python3
"""Affiche l'adresse IPv4 locale utilisée pour sortir vers Internet (souvent l'IP LAN).

Utilisé par dev-repo-up.sh pour construire l'URL du dépôt Jellyfin sans la saisir à la main.
"""
from __future__ import annotations

import socket
import subprocess
import sys


def _via_default_route() -> str | None:
    """IP source de la route par défaut (UDP connect, aucun paquet significatif)."""
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.settimeout(1.0)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        return ip if ip else None
    except OSError:
        return None
    finally:
        try:
            s.close()
        except Exception:
            pass


def _via_macos_ipconfig() -> str | None:
    for iface in ("en0", "en1", "en2", "en3", "en4", "en5"):
        try:
            out = subprocess.run(
                ["ipconfig", "getifaddr", iface],
                check=True,
                capture_output=True,
                text=True,
            )
            ip = out.stdout.strip()
            if ip and not ip.startswith("127."):
                return ip
        except (subprocess.CalledProcessError, FileNotFoundError):
            continue
    return None


def _via_hostname_i() -> str | None:
    try:
        out = subprocess.run(
            ["hostname", "-I"],
            check=True,
            capture_output=True,
            text=True,
        )
    except (subprocess.CalledProcessError, FileNotFoundError):
        return None
    for ip in out.stdout.split():
        if ip and not ip.startswith("127."):
            return ip
    return None


def main() -> int:
    ip = _via_default_route()
    if not ip or ip.startswith("127."):
        ip = _via_macos_ipconfig()
    if not ip or ip.startswith("127."):
        ip = _via_hostname_i()
    if not ip or ip.startswith("127."):
        print(
            "Impossible de déterminer l'IPv4 LAN. "
            "Passez une URL en argument : ./scripts/dev-repo-up.sh http://<ip>:8765",
            file=sys.stderr,
        )
        return 1
    print(ip, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
