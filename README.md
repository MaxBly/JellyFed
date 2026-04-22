# JellyFed

Plugin Jellyfin pour la fédération native d'instances.

Connecte plusieurs serveurs Jellyfin entre eux : depuis un seul client, on accède aux bibliothèques de toutes les instances fédérées — avec artwork, métadonnées, transcodage HLS et sélection de pistes — de façon transparente pour tous les clients officiels.

---

## Concept

```
[Client Jellyfin]
       │
       ▼
[Instance A  ←──── JellyFed ────→  Instance B]
       │                                 │
  Bibliothèque A                   Bibliothèque B
  (locale)                         (partagée via .strm + NFO)
```

Instance A installe JellyFed. Elle se connecte à l'Instance B. Le plugin synchronise le catalogue de B dans A sous forme de fichiers `.strm` + `.nfo` dans une bibliothèque virtuelle. Les clients voient les médias de B comme s'ils étaient locaux — avec artwork, pistes audio/sous-titres, transcodage HLS si nécessaire.

---

## Fonctionnalités

### Catalogue & streaming
- Exposition du catalogue local via `GET /JellyFed/catalog` (films + séries + codec info)
- Proxy stream `/JellyFed/stream/{id}?token=...` — aucune clé API dans les `.strm`
- Proxy image `/JellyFed/image/{id}/{type}?token=...` — fallback si pas de `JellyfinApiKey`
- Infos codec + toutes les pistes audio/sous-titres exposées dans le catalogue
- Décision transcodage HLS correcte grâce aux infos `<fileinfo><streamdetails>` dans les NFO
- Seeking fonctionnel (range requests sur le fichier source)

### Synchronisation
- Tâche planifiée `IScheduledTask` (intervalle configurable, défaut 6h)
- Manifest JSON — évite la re-création des `.strm` déjà présents
- Mise à jour automatique des NFO existants à chaque sync (codec, pistes audio, sous-titres)
- Pruning automatique des `.strm` dont les items ont disparu du peer
- Déduplication par TMDB ID (pas de doublon si contenu déjà présent localement)
- Rescan Jellyfin déclenché après chaque sync

### Gestion des peers
- Configuration via le panneau admin Jellyfin
- Statut online/offline via heartbeat toutes les 5 minutes
- Auto-registration bidirectionnelle (A configure B → B découvre A automatiquement)
- Tokens d'accès par peer — révocation immédiate à la suppression
- Blacklist des peers supprimés manuellement
- Sync manuelle par peer ou globale

### Sécurité
- Token de fédération auto-généré au démarrage (non éditable)
- Clé API Jellyfin optionnelle (`JellyfinApiKey`) — reste côté serveur, jamais dans les `.strm`
- Bouton "Reset Network" : nouveau token + suppression de tous les peers et `.strm`
- `X-Forwarded-Proto` respecté derrière un reverse proxy

### UI admin
- Peers avec statut, Sync Now par peer, stats catalogue (films/séries par peer)
- Purge catalogue par peer, Blocked Peers avec déblocage
- Token de fédération en lecture seule avec bouton Copy
- Danger Zone : Reset Network

---

## Compatibilité

- **Jellyfin** : 10.11.x
- **.NET** : 9.0
- **Clients** : tous (web, Android, iOS, Infuse, Kodi...)

---

## Installation

### Via le dépôt (recommandé)

Ajoutez dans Jellyfin → Dashboard → Plugins → Repositories :
```
https://jellyfed.bly-net.com/repo/manifest.json
```
Puis installez JellyFed depuis le catalogue.

### Manuelle

1. Télécharger la dernière release depuis GitHub
2. Extraire `Jellyfin.Plugin.JellyFed.dll`
3. Copier dans `{config}/plugins/JellyFed_{version}/`
4. Redémarrer Jellyfin

### Configuration minimale

```
Federation Token : <auto-généré>
Instance Name    : mon-serveur
Self URL         : https://mon-jellyfin.example.com
Sync Interval    : 6 (heures)
Library Path     : <auto-défini>
```

Ajouter un peer (URL + token du peer distant) et cliquer Save.

### Bibliothèques Jellyfin

Après la première sync :
- Ajouter `{LibraryPath}/Films` → type **Films**
- Ajouter `{LibraryPath}/Series` → type **Séries**

### JellyfinApiKey (optionnel)

Créer une clé dédiée dans Dashboard → API Keys.
La renseigner dans la config JellyFed. Permet :
- Images en qualité native via l'API Jellyfin
- Redirect du stream vers le pipeline natif Jellyfin (transcodage avancé)

---

## Documentation

- [`docs/architecture.md`](docs/architecture.md) — Architecture technique, flux de sync, authentification
- [`docs/api.md`](docs/api.md) — Référence API (tous les endpoints + DTOs)
- [`docs/strm.md`](docs/strm.md) — Fichiers .strm, NFO format, comportement lecture/transcodage
- [`docs/roadmap.md`](docs/roadmap.md) — État d'avancement, tests, bugs connus, features à venir
- [`docs/dev-local-repo.md`](docs/dev-local-repo.md) — Servir le dépôt plugin (`repo/`) sur le LAN avec Docker (dev) ; raccourci : `make dev` (build + ZIP + serveur)

---

## Limitations connues

| # | Description | Statut |
|---|---|---|
| BUG-05 | Sous-titres SRT/ASS non affichés (soft-sub WebVTT) | 🔴 P1 |
| BUG-06 | PGS brûlés en hard-sub (non désactivable) | 🟡 Limitation Jellyfin |
