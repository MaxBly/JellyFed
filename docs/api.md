# JellyFed — API de fédération

Le préfixe **canonique** est `/JellyFed/v1/`.
Les anciennes routes `/JellyFed/...` restent exposées comme alias rétrocompatibles pour les peers plus anciens.

**Authentification :** header `Authorization: Bearer <token>` sur les routes protégées.
Le token est soit l'`AccessToken` per-peer (post auto-registration), soit le `FederationToken` global (bootstrap).

---

## Endpoints publics (sans authentification)

### `GET /JellyFed/v1/health`

Heartbeat. Utilisé par `PeerHeartbeatService` toutes les 5 minutes.

**Réponse 200 :**
```json
{ "version": "0.1.0", "name": "JellyFed", "status": "ok" }
```

---

### `GET /JellyFed/v1/system/info`

Endpoint de handshake / découverte de capacités. Utilisé pour exposer la version JellyFed,
la version de protocole, la version de schéma persisté et l'`instanceId` stable de l'instance.

**Réponse 200 :**
```json
{
  "name": "JellyFed",
  "version": "0.1.0",
  "instanceId": "4f3d6a9e4d9b4d9ebaf17d6f7f6fbb8a",
  "serverName": "instance-a",
  "protocolVersion": 1,
  "schemaVersion": 1,
  "preferredRoutePrefix": "/JellyFed/v1",
  "routePrefixes": ["/JellyFed/v1", "/JellyFed"],
  "capabilities": [
    "schema-versioning",
    "versioned-routes",
    "legacy-route-aliases",
    "stable-instance-id",
    "per-peer-access-tokens",
    "per-peer-roots",
    "sync-anime-toggle",
    "stream-proxy",
    "image-proxy"
  ]
}
```

---

### `GET /JellyFed/v1/stream/{itemId}?token={federationToken}`

Sert ou redirige le flux vidéo d'un item. Utilisé par les fichiers `.strm` — les players ne peuvent pas envoyer de headers d'auth, d'où le token en query param.

**Comportement :**
- Si `JellyfinApiKey` configurée → `302` vers `/Videos/{itemId}/stream?api_key={key}&Static=true`
  (`Static=true` pour le support des range requests → seeking fonctionnel)
- Sinon → `PhysicalFile` du fichier avec `enableRangeProcessing: true`

**Réponses :**
- `200` / `206` — stream ou fichier
- `302` — redirect vers Jellyfin natif (si `JellyfinApiKey` configurée)
- `401` — token manquant ou invalide
- `404` — item introuvable

---

### `GET /JellyFed/v1/image/{itemId}/{imageType}?token={federationToken}`

Sert une image d'item (poster ou backdrop). Utilisé quand `JellyfinApiKey` n'est pas configurée.

`{imageType}` : `Primary` ou `Backdrop`

**Comportement :** lit `item.ImageInfos` et sert le fichier image via `PhysicalFile`.

**Réponses :** `200`, `400` (type invalide), `401`, `404`

---

### `POST /JellyFed/v1/peer/register`

Enregistrement d'une instance distante. Appelé automatiquement après chaque sync (auto-registration bidirectionnelle).

**Body :**
```json
{
  "name": "instance-a",
  "url": "https://jellyfin-a.example.com",
  "federationToken": "token-instance-a"
}
```

**Réponses :**
- `200 {"status": "ok", "accessToken": "xyz..."}` — peer enregistré. Stocker `accessToken` pour tous les appels futurs vers cette instance.
- `200 {"status": "blocked"}` — URL dans la blacklist
- `400` — champs manquants
- `503` — config indisponible

---

## Endpoints protégés (Bearer requis)

### `GET /JellyFed/v1/catalog`

Catalogue de cette instance (films + séries de la bibliothèque locale). Les `.strm` de la jellyfed-library sont exclus.

**Query params :**
| Param | Défaut | Description |
|-------|--------|-------------|
| `type` | tous | `"Movie"` ou `"Series"` |
| `since` | tous | ISO 8601 — items modifiés après cette date |
| `limit` | 5000 | Items max |
| `offset` | 0 | Pagination |

**Réponse 200 :**
```json
{
  "total": 3,
  "items": [
    {
      "jellyfinId": "abc123def456",
      "tmdbId": "872585",
      "imdbId": "tt15398776",
      "type": "Movie",
      "title": "Oppenheimer",
      "originalTitle": "Oppenheimer",
      "year": 2023,
      "overview": "...",
      "genres": ["Drama", "History"],
      "runtimeMinutes": 181,
      "voteAverage": 8.1,
      "posterUrl": "https://peer-b/Items/abc123/Images/Primary?api_key=KEY",
      "backdropUrl": "https://peer-b/Items/abc123/Images/Backdrop?api_key=KEY",
      "streamUrl": "https://peer-b/JellyFed/v1/stream/abc123?token=FED_TOKEN",
      "addedAt": "2026-01-15T10:30:00Z",
      "updatedAt": "2026-01-15T10:30:00Z",
      "container": "mkv",
      "videoCodec": "hevc",
      "width": 1920,
      "height": 1080,
      "audioCodec": "eac3",
      "mediaStreams": [
        { "type": "Audio", "codec": "eac3", "language": "eng", "title": "English (Atmos)", "isDefault": true, "isForced": false },
        { "type": "Audio", "codec": "aac", "language": "fre", "title": "Français", "isDefault": false, "isForced": false },
        { "type": "Subtitle", "codec": "subrip", "language": "eng", "title": "English", "isDefault": false, "isForced": false },
        { "type": "Subtitle", "codec": "pgs", "language": "fre", "title": "Français", "isDefault": false, "isForced": false }
      ]
    }
  ]
}
```

Pour les séries, `streamUrl`, `container`, `videoCodec`, `width`, `height`, `audioCodec`, `mediaStreams` sont `null`/vides (les URLs et codecs sont au niveau épisode).

---

### `GET /JellyFed/v1/catalog/series/{seriesId}/seasons`

Saisons et épisodes d'une série. Codec info incluse par épisode.

**Réponse 200 :**
```json
{
  "seriesId": "xyz789",
  "seasons": [
    {
      "jellyfinId": "s01id",
      "seasonNumber": 1,
      "title": "Season 1",
      "episodes": [
        {
          "jellyfinId": "ep001",
          "episodeNumber": 1,
          "title": "Pilot",
          "overview": "...",
          "airDate": "2008-01-20",
          "runtimeMinutes": 47,
          "stillUrl": "https://peer-b/Items/ep001/Images/Primary?api_key=KEY",
          "streamUrl": "https://peer-b/JellyFed/v1/stream/ep001?token=FED_TOKEN",
          "container": "mkv",
          "videoCodec": "h264",
          "width": 1920,
          "height": 1080,
          "audioCodec": "aac",
          "mediaStreams": [
            { "type": "Audio", "codec": "aac", "language": "eng", "isDefault": true, "isForced": false },
            { "type": "Subtitle", "codec": "subrip", "language": "eng", "isDefault": false, "isForced": false }
          ]
        }
      ]
    }
  ]
}
```

**Erreurs :** `400` (GUID invalide), `401`, `404`

---

### `GET /JellyFed/v1/peers`

Peers configurés avec statut online/offline.

**Réponse 200 :**
```json
{
  "peers": [
    {
      "name": "instance-b",
      "url": "https://peer-b.example.com",
      "enabled": true,
      "online": true,
      "lastSeen": "2026-04-15T20:00:00Z",
      "version": "0.1.0",
      "movieCount": 3,
      "seriesCount": 3
    }
  ]
}
```

---

### `POST /JellyFed/v1/peer/sync`

Déclenche une sync manuelle (queue `FederationSyncTask`).

**Body :** `{ "peerName": "instance-b" }` — `null` pour syncer tous les peers.

**Réponse 202 :** `{ "status": "queued" }`

---

### `GET /JellyFed/v1/manifest/stats`

Stats du manifest par peer (items synced).

**Réponse 200 :**
```json
{
  "peers": [
    { "name": "instance-b", "movieCount": 3, "seriesCount": 2 }
  ]
}
```

---

### `POST /JellyFed/v1/peer/purge`

Supprime tous les `.strm` d'un peer du manifest et du filesystem.

**Body :** `{ "peerName": "instance-b" }`

**Réponse 200 :** `{ "status": "ok", "deletedMovies": 3, "deletedSeries": 2 }`

---

### `GET /JellyFed/v1/peers/details`

Vue riche utilisée par l'onglet **Peers** de la page admin : agrège configuration, heartbeat, dernière sync et compteurs locaux (films / séries / anime + taille disque) calculés depuis le manifest et le filesystem.

**Réponse 200 :**
```json
{
  "peers": [
    {
      "name": "instance-b",
      "url": "https://peer-b.example.com",
      "enabled": true, "syncMovies": true, "syncSeries": true, "syncAnime": true,
      "hasAccessToken": true,
      "online": true, "lastSeen": "2026-04-15T20:00:00Z", "version": "0.1.0",
      "lastSyncAt": "2026-04-15T19:45:00Z", "lastSyncStatus": "ok",
      "lastSyncError": null, "lastSyncDurationMs": 12450,
      "peerMovieCount": 120, "peerSeriesCount": 40,
      "localMovieCount": 118, "localSeriesCount": 38, "localAnimeCount": 12,
      "localDiskBytes": 251658240,
      "moviesFolder": "/data/.../Films/instance-b",
      "seriesFolder": "/data/.../Series/instance-b",
      "animeFolder": "/data/.../Animes/instance-b"
    }
  ],
  "lastGlobalSyncAt": "2026-04-15T19:45:00Z"
}
```

---

### `POST /JellyFed/v1/peers`

Ajoute un peer depuis la modal *Add peer* du panneau admin. Fait un handshake `/JellyFed/v1/system/info` (avec fallback legacy `/JellyFed/system/info` puis `/JellyFed/health`) sur l'URL fournie avant de persister ; le peer est stocké même si unreachable (admin peut configurer en avance). L'URL est retirée automatiquement de `BlockedPeerUrls`.

**Body :** `AddPeerRequestDto` — `Name`, `Url`, `FederationToken`, `Enabled`, `SyncMovies`, `SyncSeries`, `SyncAnime`.

**Réponse 200 :** `{ "status": "ok", "reachable": true, "version": "0.1.0" }` — `reachable=false` si le health check a échoué.

**Conflits :** `409 Conflict` si un peer utilise déjà ce nom ou cette URL.

---

### `POST /JellyFed/v1/peers/test`

Teste la joignabilité d'un peer candidat **sans rien modifier** à la configuration. Utilisé par le bouton *Test connection* de la modal *Add peer* pour valider URL + token avant de confirmer l'ajout.

**Body :** `AddPeerRequestDto` — seuls `Url` et `FederationToken` sont requis (les autres champs sont ignorés).

**Réponse 200 :**
```json
{
  "status": "ok",          // "ok" si reachable, "unreachable" sinon
  "reachable": true,
  "version": "0.1.0",      // version JellyFed rapportée, null si injoignable
  "message": "Peer reachable (JellyFed v0.1.0)."
}
```

**400 Bad Request :** `Url` ou `FederationToken` manquant.

---

### `POST /JellyFed/v1/peer/{name}/sync`

Sync inline pour un seul peer. Exécute `FederationSyncTask.SyncPeerAsync(peer, ct)` — même pipeline que la tâche planifiée, scoped à ce peer (pruning limité à ses entrées manifest).

**Réponse 200 :** `PeerSyncResultDto` — `addedMovies`, `addedSeries`, `skippedMovies`, `skippedSeries`, `pruned`, `durationMs`, `error?`.

**404 :** le peer n'existe pas.

---

### `POST /JellyFed/v1/peer/{name}/purge`

Équivalent de `/peer/purge` en REST path-based. Supprime `.strm` + entrées manifest + dossiers per-peer, reset les compteurs `PeerStatus` (status → `never`). La config du peer est conservée.

**Réponse 200 :** `{ "status": "ok", "deletedMovies": N, "deletedSeries": N }`

---

### `POST /JellyFed/v1/peer/{name}/remove`

Retire définitivement un peer : purge des `.strm`, révocation de `AccessToken` (les prochaines requêtes du peer retournent 401), ajout de son URL dans `BlockedPeerUrls` pour bloquer l'auto-registration, et retrait de `config.Peers`.

**Réponse 200 :** `{ "status": "ok", "deletedMovies": N, "deletedSeries": N, "blockedUrl": "..." }`

---

### `PATCH /JellyFed/v1/peer/{name}`

Update partiel d'un peer depuis l'UI. Seuls les champs non-null sont appliqués.

**Body :** `UpdatePeerRequestDto` — `Name?`, `Url?`, `FederationToken?`, `Enabled?`, `SyncMovies?`, `SyncSeries?`, `SyncAnime?`.

**Cas particulier `Name` :**
1. Validation d'unicité (nom + URL) — `409` en cas de conflit.
2. Calcul des segments sanitizés via `StrmWriter.SanitizePeerFolderSegment`.
3. `Directory.Move` pour chaque racine (Movies / Series / Anime) si `{Root}/{oldSeg}` existe.
4. Réécriture du manifest : `PeerName` + préfixe `Path` pour toutes les entrées du peer.
5. Renommage de la clé dans `.jellyfed-peers.json`.

**Réponse 200 :** `{ "status": "ok" }`

---

### `POST /JellyFed/v1/network/reset`

Reset total : nouveau token de fédération, suppression de tous les peers et `.strm`.
Les peers ayant l'ancien token recevront `401` lors de leur prochain accès.

**Réponse 200 :** `{ "status": "ok", "newToken": "nouveau_token" }`

---

## Notes d'implémentation

**URLs d'images :** si `JellyfinApiKey` est configurée sur l'instance source, le catalogue retourne des URLs directes vers l'API Jellyfin (`/Items/{id}/Images/...?api_key=KEY`). Sinon, il retourne des URLs vers le proxy JellyFed (`/JellyFed/v1/image/{id}/{type}?token=...`). Dans les deux cas, l'artwork est aussi téléchargé localement lors de la sync (`poster.jpg`, `fanart.jpg`).

**Delta sync :** le champ `since` permet de ne synchroniser que les nouveautés. Non encore exploité dans `FederationSyncTask` (toujours sync complète avec déduplication via manifest).

**`JellyfinApiKey` :** clé API dédiée à créer dans Jellyfin (Dashboard → API Keys). Doit avoir accès en lecture aux médias. Ne jamais utiliser la clé admin. Elle n'apparaît jamais dans les fichiers `.strm`.
