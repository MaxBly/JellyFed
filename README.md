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
- Exposition du catalogue local via `GET /JellyFed/v1/catalog` (films + séries + codec info)
- Proxy stream `/JellyFed/v1/stream/{id}?token=...` — aucune clé API dans les `.strm`
- Proxy image `/JellyFed/v1/image/{id}/{type}?token=...` — fallback si pas de `JellyfinApiKey`
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
- Endpoint handshake `GET /JellyFed/v1/system/info` (version, protocolVersion, schemaVersion, instanceId, capabilities)
- Onglet dédié « Peers » dans la page de configuration (Readme / Settings / Peers / Danger Zone)
- Cartes par peer avec statut online/offline, version, dernière sync (badge ok/failed/never + erreur), durée
- Compteurs synced par peer : catalogue distant (films / séries) vs local (films / séries / anime) + disque utilisé
- Toggles par peer : Enabled, Films, Séries, Anime (PATCH live sans bouton Save)
- Actions fine-grained : Resync ce peer, Purge .strm, Edit (nom / URL / token, avec renommage des dossiers), Remove (purge + révocation token + blacklist)
- Ajout de peer via modal avec health-check préalable
- Auto-registration bidirectionnelle + heartbeat toutes les 5 minutes
- Blacklist automatique des peers supprimés (dépose les URLs dans Blocked Peers)

### Sécurité
- Token de fédération auto-généré au démarrage (non éditable)
- `InstanceId` stable auto-généré côté config pour les handshakes / diagnostics inter-peers
- Clé API Jellyfin optionnelle (`JellyfinApiKey`) — reste côté serveur, jamais dans les `.strm`
- Bouton "Reset Network" : nouveau token + suppression de tous les peers et `.strm`
- `X-Forwarded-Proto` respecté derrière un reverse proxy

### UI admin
- Page avec 4 onglets : **Readme** (intro + setup, ouvert par défaut), **Settings** (globaux), **Peers** (liste + actions), **Danger Zone** (reset network)
- Token de fédération en lecture seule avec bouton Copy
- Blocked Peers déplacés dans l'onglet Peers (unblock + save)
- Reset Network isolé dans son propre onglet pour éviter les clics accidentels

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
Instance ID      : <auto-généré, stable>
Instance Name    : mon-serveur
Self URL         : https://mon-jellyfin.example.com
Sync Interval    : 6 (heures)
Library Path     : <auto-défini>
```

Ajouter un peer (URL + token du peer distant) et cliquer Save.

### Bibliothèques Jellyfin

Après la première sync, ajoutez des bibliothèques Jellyfin qui pointent vers les **racines** configurées (ou les défauts sous le dossier métadonnées) :
- **Films** : dossier `Movies root` (défaut `{LibraryPath}/Films`) — type **Films**
- **Séries** : dossier `TV series root` (défaut `{LibraryPath}/Series`) — type **Séries**
- **Animes** (optionnel) : dossier `Anime root` (défaut `{LibraryPath}/Animes`) — type **Films** ou **Séries** selon ce que tu y synchronises

Les `.strm` sont rangés par **peer** : `{Racine}/{NomDuPeer}/…`.

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
- [`docs/v1-scope.md`](docs/v1-scope.md) — Plan v1 : contrats publics à figer, features obligatoires avant release stable, critères de validation
- [`docs/dev-local-repo.md`](docs/dev-local-repo.md) — Servir le dépôt plugin (`repo/`) sur le LAN avec Docker (dev) ; raccourci : `make dev` (build + ZIP + serveur)

---

## Limitations connues

| # | Description | Statut |
|---|---|---|
| BUG-05 | Sous-titres SRT/ASS non affichés (soft-sub WebVTT) | 🔴 P1 |
| BUG-06 | PGS brûlés en hard-sub (non désactivable) | 🟡 Limitation Jellyfin |
