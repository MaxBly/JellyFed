# JellyFed

Plugin Jellyfin pour la fédération native d'instances.

Connecte plusieurs serveurs Jellyfin entre eux : depuis un seul client, on accède aux bibliothèques de toutes les instances fédérées — sans proxy, sans frontend custom, de façon transparente pour les clients officiels.

---

## Concept

```
[Client Jellyfin]
       │
       ▼
[Instance A  ←──── JellyFed ────→  Instance B]
       │                                 │
  Bibliothèque A                   Bibliothèque B
  (locale)                         (partagée via .strm)
```

Instance A installe JellyFed. Elle se connecte à l'Instance B (un ami, un serveur communautaire). Le plugin synchronise le catalogue de B dans A sous forme de fichiers `.strm` dans une bibliothèque virtuelle. Les clients Jellyfin voient les médias de B exactement comme s'ils étaient locaux — avec artwork, métadonnées, lecture directe.

---

## Documentation

- [`docs/architecture.md`](docs/architecture.md) — Architecture technique complète
- [`docs/api.md`](docs/api.md) — API de fédération (endpoints custom)
- [`docs/roadmap.md`](docs/roadmap.md) — Phases d'implémentation
- [`docs/strm.md`](docs/strm.md) — Fonctionnement des fichiers .strm dans Jellyfin

---

## Repo

Projet privé. Pas de push GitHub.
