# JellyFed

Plugin Jellyfin pour fédérer nativement plusieurs instances Jellyfin.

JellyFed synchronise les catalogues de peers distants sous forme de fichiers `.strm` et `.nfo`. Les clients Jellyfin voient ensuite les médias distants comme des médias locaux, avec métadonnées, artwork, pistes audio/sous-titres et transcodage HLS piloté par Jellyfin.

JellyFed est autonome : il ne dépend d'aucun client spécifique. Les clients officiels Jellyfin, Infuse, Kodi ou une interface web externe consomment simplement Jellyfin.

---

## Concept

```text
[Client Jellyfin]
       |
       v
[Instance A + JellyFed] <---- federation ----> [Instance B + JellyFed]
       |
       v
Bibliothèque locale Jellyfin
  + médias locaux
  + versions distantes matérialisées en .strm
```

Chaque peer expose son catalogue via `/JellyFed/catalog`. L'instance consommatrice écrit une version `.strm` par peer dans un dossier logique commun :

```text
{MoviesRoot}/
  Anora (2024) [tmdbid-1064213]/
    Anora (2024) [peer-b].strm
    Anora (2024) [peer-b].nfo
    Anora (2024) [peer-c].strm
    Anora (2024) [peer-c].nfo
    poster.jpg
    fanart.jpg
```

Jellyfin peut alors fusionner ces fichiers comme des versions d'un même média grâce aux IDs TMDB/NFO. Le sélecteur de version est celui du player Jellyfin, pas un provider custom JellyFed.

---

## Fonctionnalités

### Catalogue et lecture

- `GET /JellyFed/catalog` expose films et séries locaux, hors contenu déjà fédéré.
- `GET /JellyFed/catalog/series/{id}/seasons` expose épisodes et infos de pistes.
- `.strm` sans clé API Jellyfin : URL `/JellyFed/stream/{id}?token=...`.
- Images via `/JellyFed/image/{id}/{type}?token=...` ou URL Jellyfin native si `JellyfinApiKey` est configurée.
- NFO enrichis avec `<fileinfo><streamdetails>` pour aider Jellyfin à décider direct-play, direct-stream ou transcodage HLS.
- Versions multiples par item via fichiers `.strm` multiples dans le même dossier logique.

### Synchronisation

- Tâche planifiée `IScheduledTask`, intervalle configurable, défaut 6h.
- Manifest JSON local `.jellyfed-manifest.json`, schéma v2.
- Layout aplati par item : pas de sous-dossier par peer.
- Pruning par source : si un peer perd un item, seuls ses fichiers `[peer-X]` sont retirés ; l'item reste si d'autres sources existent.
- Rescan Jellyfin déclenché après sync.

### Peers et discovery

- Handshake `GET /JellyFed/system/info` et version `GET /JellyFed/version`.
- Peers directs configurés manuellement dans l'UI.
- Discovery v1 limitée aux suggestions second-hop, sans sync automatique.
- Toggle par peer : Enabled, Films, Séries, Anime.
- Actions : test, sync d'un peer, purge, remove, edit.

### Sécurité et audit

- Token de fédération auto-généré au démarrage.
- `InstanceId` stable en configuration.
- Access tokens per-peer révocables après registration.
- `JellyfinApiKey` optionnelle, utilisée uniquement côté serveur.
- Audit persistant SQLite dans `.jellyfed-audit.sqlite3`.
- Endpoints logs admin-only sous `/JellyFed/logs/*`.

---

## Compatibilité

- Jellyfin : `10.11.x`
- .NET : `9.0`
- Plugin actuel : `0.1.0.18-dev` côté code, manifest de release à bumper au prochain build publié

---

## Installation

### Via dépôt

Dans Jellyfin → Dashboard → Plugins → Repositories :

```text
https://jellyfed.bly-net.com/repo/manifest.json
```

Puis installer JellyFed depuis le catalogue.

### Manuelle

1. Télécharger la release.
2. Extraire `Jellyfin.Plugin.JellyFed.dll`.
3. Copier dans `{config}/plugins/JellyFed_{version}/`.
4. Redémarrer Jellyfin.

### Configuration minimale

```text
Federation Token : auto-généré
Instance ID      : auto-généré
Instance Name    : mon-serveur
Self URL         : https://mon-jellyfin.example.com
Discoverable     : true/false
Sync Interval    : 6
Metadata Path    : {DataPath}/jellyfed-library
Movies Root      : {Metadata Path}/Films
Series Root      : {Metadata Path}/Series
Anime Root       : {Metadata Path}/Animes
```

`Self URL` est important : les URLs écrites dans les `.strm` doivent être joignables par le FFmpeg de l'instance qui lit le média. En environnement mixte LAN/public, choisissez l'URL réellement atteignable par les peers consommateurs.

### Bibliothèques Jellyfin

Ajoutez dans Jellyfin des bibliothèques qui scannent les racines JellyFed :

- Films : `MoviesRoot`
- Séries : `SeriesRoot`
- Anime : `AnimeRoot`, selon votre usage

Si vous voulez que Jellyfin fusionne médias locaux et versions fédérées, ajoutez la racine locale et la racine JellyFed dans la même bibliothèque Jellyfin.

---

## Documentation

- [`docs/architecture.md`](docs/architecture.md) : architecture et flux de sync
- [`docs/api.md`](docs/api.md) : endpoints publics, fédération et admin
- [`docs/strm.md`](docs/strm.md) : layout `.strm` / `.nfo` et lecture
- [`docs/roadmap.md`](docs/roadmap.md) : état d'avancement et bugs connus
- [`docs/v1-scope.md`](docs/v1-scope.md) : critères v1
- [`docs/validation-v1.md`](docs/validation-v1.md) : validations runtime avant release v1
- [`docs/dev-local-repo.md`](docs/dev-local-repo.md) : dépôt plugin local de développement
- [`docs/gitea-ci.md`](docs/gitea-ci.md) : dépôt Jellyfin de test via Gitea Actions et release `latest`

Les fichiers `docs/handoff-2026-06-01.md` et `docs/dew-integration-gaps.md` sont des notes internes/historiques, pas de la documentation produit.

---

## Limitations connues

| ID | Description | Statut |
|---|---|---|
| BUG-05 | Sous-titres SRT/ASS soft-sub à valider/corriger après layout aplati | P1 |
| BUG-06 | PGS brûlés en hard-sub lors du transcodage HLS | Limitation Jellyfin |
