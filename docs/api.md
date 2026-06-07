# JellyFed — API

Préfixe canonique : `/JellyFed`.

Les routes inter-peers utilisent `Authorization: Bearer <token>`, sauf les URLs de stream/image appelées depuis les `.strm`, qui utilisent `?token=...`.

---

## Routes publiques

### `GET /JellyFed/health`

Heartbeat simple.

```json
{ "version": "0.1.0", "name": "JellyFed", "status": "ok" }
```

### `GET /JellyFed/version`

Expose la version plugin, protocole, schéma et identité locale.

```json
{
  "version": "0.1.0",
  "protocolVersion": 1,
  "schemaVersion": 2,
  "instanceId": "4f3d...",
  "serverName": "instance-a"
}
```

### `GET /JellyFed/system/info`

Handshake de capacités pour peers.

```json
{
  "name": "JellyFed",
  "version": "0.1.0",
  "instanceId": "4f3d...",
  "serverName": "instance-a",
  "protocolVersion": 1,
  "schemaVersion": 2,
  "capabilities": [
    "stable-instance-id",
    "per-peer-access-tokens",
    "flattened-multi-version-layout",
    "sync-anime-toggle",
    "stream-proxy",
    "image-proxy"
  ]
}
```

---

## Catalogue protégé

### `GET /JellyFed/catalog`

Retourne les films et séries locaux, en excluant les contenus déjà sous les racines JellyFed.

Query params :

| Param | Défaut | Description |
|---|---:|---|
| `type` | tous | `Movie` ou `Series` |
| `since` | tous | ISO 8601 |
| `limit` | `5000` | taille max |
| `offset` | `0` | pagination |

Extrait :

```json
{
  "total": 1,
  "items": [
    {
      "jellyfinId": "abc123",
      "tmdbId": "1064213",
      "type": "Movie",
      "title": "Anora",
      "year": 2024,
      "posterUrl": "https://peer/JellyFed/image/abc123/Primary?token=...",
      "streamUrl": "https://peer/JellyFed/stream/abc123?token=...",
      "container": "mkv",
      "videoCodec": "hevc",
      "width": 3840,
      "height": 2160,
      "audioCodec": "eac3",
      "bitRate": 52000000,
      "sizeBytes": 64000000000,
      "videoRange": "HDR10",
      "edition": "Director's Cut",
      "mediaStreams": [
        { "type": "Video", "codec": "hevc", "index": 0, "width": 3840, "height": 2160, "videoRange": "HDR10" },
        { "type": "Audio", "codec": "eac3", "language": "eng", "index": 1, "channels": 6 },
        { "type": "Subtitle", "codec": "subrip", "language": "fre", "index": 2 }
      ]
    }
  ]
}
```

Les URLs de catalogue préfèrent `SelfUrl` de l'instance source. Si `SelfUrl` est vide, elles utilisent l'URL de la requête courante.

### `GET /JellyFed/catalog/series/{seriesId}/seasons`

Retourne saisons et épisodes d'une série, avec stream URL et infos techniques par épisode.

---

## Stream et images

### `GET /JellyFed/stream/{itemId}?token=...`

Utilisé par les fichiers `.strm`.

Comportement :

- si `JellyfinApiKey` est configurée : redirect `302` vers `/Videos/{id}/stream?api_key=...&Static=true` ;
- sinon : `PhysicalFile` du média avec range requests.

Réponses : `200`, `206`, `302`, `401`, `404`.

### `GET /JellyFed/image/{itemId}/{imageType}?token=...`

Sert `Primary` ou `Backdrop` depuis `ImageInfos` Jellyfin quand le catalogue n'utilise pas une URL Jellyfin native.

---

## Peers et sync

### `GET /JellyFed/peers`

Liste compacte des peers configurés.

### `GET /JellyFed/peers/details`

Payload riche pour l'onglet Peers : santé, version, dernière sync, compteurs locaux, taille disque par fichiers source, racines configurées.

### `POST /JellyFed/peers/test`

Teste une URL + token sans modifier la configuration.

### `POST /JellyFed/peers`

Ajoute un peer direct après health-check best-effort.

### `PATCH /JellyFed/peer/{name}`

Met à jour nom, URL, token et toggles. En cas de renommage, JellyFed renomme les fichiers `[peer-X]` existants et met à jour le manifest.

### `POST /JellyFed/peer/{name}/sync`

Exécute une sync inline pour un peer.

### `POST /JellyFed/peer/{name}/purge`

Supprime les fichiers `.strm/.nfo` de ce peer uniquement. Les items multi-source restent si d'autres peers les exposent encore.

### `POST /JellyFed/peer/{name}/remove`

Purge le peer, révoque son access token, retire la configuration et ajoute l'URL à la blacklist de discovery.

### `POST /JellyFed/peer/sync`

Déclenche la tâche planifiée globale.

### `GET /JellyFed/manifest/stats`

Stats locales par peer depuis le manifest.

---

## Discovery

### `GET /JellyFed/discovery`

Route protégée qui retourne :

- `self` : cette instance ;
- `directPeers` : peers directs discoverable connus.

La discovery v1 reste suggestion-only : aucun peer découvert n'est synchronisé tant qu'un admin ne l'ajoute pas explicitement.

### `POST /JellyFed/peer/register`

Handshake optionnel pour obtenir un `AccessToken` per-peer. Ne crée pas automatiquement de peer.

---

## Audit admin-only

Ces routes utilisent l'authentification admin Jellyfin, pas le token de fédération.

### `GET /JellyFed/logs/overview`

Compteurs globaux et facettes peers.

### `GET /JellyFed/logs/feed?scope=all&peerId=...&limit=100&beforeId=...`

Feed paginé. Scopes :

- `all`
- `security`
- `peer-connections`
- `peer-access`

---

## Reset

### `POST /JellyFed/network/reset`

Génère un nouveau token de fédération, supprime peers, état local et contenu fédéré.

---

## Routes supprimées

Les routes `/JellyFed/admin/sources` et `/JellyFed/admin/sources/select` ont été supprimées avec l'abandon de `FederationMediaSourceProvider`. La sélection de version est maintenant gérée par Jellyfin à partir des `.strm` multiples.
