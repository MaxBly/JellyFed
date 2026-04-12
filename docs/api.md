# JellyFed — API de fédération

Tous les endpoints sont préfixés `/JellyFed/`. Ils coexistent avec l'API Jellyfin standard sur le même port.

Authentication : header `Authorization: Bearer <federation_token>` sur les routes protégées.

---

## Endpoints publics (health / discovery)

### `GET /JellyFed/health`

Heartbeat. Retourne le statut de l'instance.

**Réponse 200 :**
```json
{
  "version": "0.1.0",
  "instanceId": "a1b2c3...",
  "name": "Mon Jellyfin",
  "uptime": 86400
}
```

---

## Endpoints protégés (federation_token requis)

### `GET /JellyFed/catalog`

Retourne le catalogue complet de cette instance.

**Query params :**
- `type=Movie|Series|Episode` — filtrer par type (défaut : Movie + Series)
- `since=2024-01-01T00:00:00Z` — delta sync (items modifiés depuis cette date)
- `limit=1000&offset=0` — pagination

**Réponse 200 :**
```json
{
  "total": 1542,
  "items": [
    {
      "jellyfinId": "abc123",
      "tmdbId": 872585,
      "imdbId": "tt15398776",
      "type": "Movie",
      "title": "Oppenheimer",
      "originalTitle": "Oppenheimer",
      "year": 2023,
      "overview": "...",
      "genres": ["Drama", "History"],
      "runtimeMinutes": 181,
      "voteAverage": 8.1,
      "posterUrl": "https://peer-a.example.com/Items/abc123/Images/Primary",
      "backdropUrl": "https://peer-a.example.com/Items/abc123/Images/Backdrop",
      "streamUrl": "https://peer-a.example.com/Videos/abc123/stream?api_key=FEDERATION_KEY",
      "addedAt": "2024-01-15T10:30:00Z",
      "updatedAt": "2024-01-15T10:30:00Z"
    }
  ]
}
```

### `GET /JellyFed/catalog/series/:jellyfinId/seasons`

Retourne les saisons + épisodes d'une série.

**Réponse 200 :**
```json
{
  "seriesId": "xyz789",
  "seasons": [
    {
      "seasonNumber": 1,
      "episodes": [
        {
          "jellyfinId": "ep001",
          "episodeNumber": 1,
          "title": "Pilot",
          "overview": "...",
          "airDate": "2008-01-20",
          "runtimeMinutes": 47,
          "stillUrl": "https://...",
          "streamUrl": "https://..."
        }
      ]
    }
  ]
}
```

---

## Endpoints de gestion des peers

### `GET /JellyFed/peers`

Liste les peers connus (leur adresse est partagée selon la trust list).

**Réponse 200 :**
```json
{
  "peers": [
    {
      "name": "peer-b",
      "url": "https://jellyfin.friend.example.com",
      "status": "online",
      "lastSeen": "2024-01-15T12:00:00Z",
      "catalogSize": { "movies": 800, "series": 120 },
      "trustLevel": "full"
    }
  ]
}
```

### `POST /JellyFed/peer/register`

Demande de connexion d'une instance tierce.

**Body :**
```json
{
  "name": "mon-jellyfin",
  "url": "https://jellyfin.example.com",
  "federationToken": "abc123...",
  "publicKey": "..."
}
```

**Réponse 200 :** `{"status": "pending"}` (admin doit approuver)  
**Réponse 200 :** `{"status": "approved"}` (si auto-approve configuré)

### `POST /JellyFed/peer/sync`

Déclenche une synchronisation manuelle avec un peer.

**Body :** `{"peerName": "peer-b"}`

**Réponse 202 :** `{"taskId": "sync-123", "status": "started"}`

---

## Notes d'implémentation

**streamUrl dans le catalogue** : l'URL contient la `federation_key` (API key Jellyfin minimaliste, lecture seule). C'est cette clé qui va dans le `.strm` et dans les réponses catalogue. Elle ne donne accès qu'aux streams, pas à l'administration.

**Pagination delta sync** : le champ `since` permet aux peers de ne télécharger que les nouveautés depuis la dernière sync. Réduire la charge réseau pour les instances avec beaucoup de médias.

**Versioning API** : préfixe `/JellyFed/v1/` à terme pour permettre des changements de schéma sans casser la compat.
