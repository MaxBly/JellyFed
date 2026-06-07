# JellyFed — Architecture

JellyFed est un plugin Jellyfin C# / .NET 9 qui fédère des bibliothèques entre instances Jellyfin 10.11+. Il ne modifie pas les clients et ne fork pas Jellyfin : il s'appuie sur les APIs publiques et sur le support natif des fichiers `.strm`.

---

## Principe

Un peer source expose son catalogue. Un peer consommateur matérialise chaque source distante comme une version `.strm` dans sa bibliothèque Jellyfin.

```text
Peer B
  /JellyFed/catalog
  /JellyFed/stream/{id}
        |
        v
Peer A
  {MoviesRoot}/Anora (2024) [tmdbid-1064213]/
    Anora (2024) [peer-b].strm
    Anora (2024) [peer-b].nfo
    Anora (2024) [peer-c].strm
    Anora (2024) [peer-c].nfo
```

Les versions d'un même média vivent dans le même dossier logique. Jellyfin peut les fusionner nativement grâce aux IDs TMDB/NFO et les exposer comme versions dans le player.

---

## Layout disque v2

`LibraryPath` stocke les fichiers de contrôle JellyFed. Les médias fédérés utilisent trois racines configurables.

```text
{LibraryPath}/
  .jellyfed-manifest.json
  .jellyfed-peers.json
  .jellyfed-audit.sqlite3

{MoviesRoot}/
  Film (2024) [tmdbid-123]/
    Film (2024) [peer-a].strm
    Film (2024) [peer-a].nfo
    Film (2024) [edition-Director's Cut] [peer-b].strm
    Film (2024) [edition-Director's Cut] [peer-b].nfo
    poster.jpg
    fanart.jpg

{SeriesRoot}/
  Serie (2008) [tmdbid-456]/
    tvshow.nfo
    poster.jpg
    fanart.jpg
    Season 01/
      S01E01 - Pilot [peer-a].strm
      S01E01 - Pilot [peer-a].nfo
      S01E01 - Pilot [peer-b].strm
      S01E01 - Pilot [peer-b].nfo
```

Il n'y a plus de sous-dossier par peer et plus de `sources.json`. Le manifest garde la provenance et les chemins représentatifs des sources.

---

## Structure du plugin

```text
Jellyfin.Plugin.JellyFed/
  Plugin.cs
  PluginServiceRegistrator.cs
  FederationProtocol.cs

  Configuration/
    PluginConfiguration.cs
    PeerConfiguration.cs
    SchemaMigrator.cs
    configPage.html

  Api/
    FederationController.cs
    AuditLogsController.cs
    FederationAuthFilter.cs
    AdminAccessFilter.cs
    Dto/

  Sync/
    FederationSyncTask.cs
    PeerClient.cs
    StrmWriter.cs
    Manifest*.cs
    PeerHeartbeatService.cs
    PeerStateStore.cs
    FederatedPathHelper.cs

  Audit/
    AuditLogStore.cs
    AuditLogService.cs
```

`FederationMediaSourceProvider` et `sources.json` ont été retirés : la sélection de version est déléguée à Jellyfin.

---

## Flux de sync

```text
1. Admin ajoute un peer direct.
2. FederationSyncTask appelle GET /JellyFed/catalog.
3. Pour chaque film :
   - clé manifest = tmdb:{id} ou no-tmdb:{peer}:{id}
   - upsert de la source dans manifest.sources[]
   - écriture {Title} [peer-X].strm + .nfo
4. Pour chaque série :
   - GET /JellyFed/catalog/series/{id}/seasons
   - écriture d'un .strm/.nfo par épisode et par peer
5. Pruning :
   - si une source peer disparaît, seuls ses fichiers [peer-X] sont supprimés
   - si aucune source ne reste, le dossier logique est supprimé
6. Sauvegarde manifest + QueueLibraryScan()
```

JellyFed ne saute plus un item sous prétexte qu'il existe localement avec le même TMDB ID. Cela permet à Jellyfin de fusionner médias locaux et versions fédérées si les racines sont dans la même bibliothèque.

---

## Manifest v2

```json
{
  "schemaVersion": 2,
  "movies": {
    "tmdb:1064213": {
      "path": "/jellyfed-library/Films/Anora (2024) [tmdbid-1064213]",
      "peerName": "peer-b",
      "jellyfinId": "abc123",
      "syncedAt": "2026-06-07T17:00:00Z",
      "sources": [
        {
          "peerName": "peer-b",
          "jellyfinId": "abc123",
          "strmPath": ".../Anora (2024) [peer-b].strm",
          "nfoPath": ".../Anora (2024) [peer-b].nfo",
          "streamUrl": "https://peer-b/JellyFed/stream/abc123?token=...",
          "videoCodec": "hevc",
          "audioCodec": "eac3",
          "width": 3840,
          "height": 2160,
          "runtimeTicks": 72600000000,
          "bitRate": 52000000,
          "sizeBytes": 64000000000,
          "videoRange": "HDR10",
          "edition": "Director's Cut"
        }
      ]
    }
  },
  "series": {}
}
```

`peerName` / `jellyfinId` dans `ManifestEntry` restent une source d'affichage par défaut. Ils ne pilotent plus la lecture : les fichiers `.strm` sont les versions réelles que Jellyfin scanne.

Un manifest v1 non vide n'est pas migré automatiquement vers v2. Le layout disque a changé, donc l'upgrade pré-v1 doit passer par un reset/purge explicite.

---

## Authentification

Les endpoints inter-peers utilisent `Authorization: Bearer <token>`.

L'ordre d'acceptation est :

1. `AccessToken` per-peer actif.
2. `FederationToken` global de l'instance.
3. Sinon `401`.

Les `.strm` ne peuvent pas envoyer de headers. Les URLs de stream/image portent donc un token en query string :

```text
https://peer-b.example.com/JellyFed/stream/{itemId}?token=...
```

---

## URLs et reverse proxy

Les URLs exposées dans le catalogue préfèrent `SelfUrl`. Si `SelfUrl` est vide, JellyFed retombe sur l'URL de la requête courante en respectant `X-Forwarded-Proto`.

Contrat important : l'URL inscrite dans un `.strm` doit être joignable par l'instance Jellyfin qui lit le média, car son FFmpeg ira chercher le fichier distant.

---

## Audit

JellyFed écrit un audit persistant SQLite local :

- sécurité : tokens manquants/invalides ;
- peer connections : heartbeat, sync, register ;
- peer access : catalog export, stream/image access ;
- admin : purge, remove, reset.

Les endpoints `/JellyFed/logs/overview` et `/JellyFed/logs/feed` sont réservés aux administrateurs Jellyfin.

---

## Ce que JellyFed n'est pas

- Pas un proxy permanent : les médias restent chez les peers.
- Pas une sync de fichiers : seules les métadonnées et URLs sont matérialisées.
- Pas lié à un client particulier : dew ou tout autre client consomme Jellyfin, pas JellyFed directement.
