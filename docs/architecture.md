# JellyFed — Architecture technique

## Vue d'ensemble

JellyFed est un plugin Jellyfin (C# / .NET 9) qui implémente la fédération de bibliothèques entre instances Jellyfin 10.11+. Il s'appuie exclusivement sur les interfaces publiques de Jellyfin : pas de fork, pas de patch, compatible avec les mises à jour Jellyfin.

---

## Principe fondamental : les fichiers `.strm`

Jellyfin supporte nativement les fichiers `.strm` : un fichier texte contenant une URL. Quand le scanner trouve un `.strm`, il l'indexe comme un média normal (film, épisode) et envoie l'URL comme source de lecture au client.

```
{MetadataPath}/                    ← LibraryPath : manifest + états peers uniquement
  .jellyfed-manifest.json
  .jellyfed-peers.json

{MoviesRoot}/{PeerName}/          ← racine films (MoviesRootPath ou défaut MetadataPath/Films)
  Oppenheimer (2023)/
    Oppenheimer (2023).strm      → "https://peer-b/JellyFed/stream/abc123?token=fed_token"
    …

{SeriesRoot}/{PeerName}/         ← séries TV non-anime
  …

{AnimeRoot}/{PeerName}/          ← films ou séries dont les genres contiennent « Anime »
  …
```

Les trois racines (`MoviesRootPath`, `SeriesRootPath`, `AnimeRootPath`) sont configurables dans le plugin ; si vides, défaut = `{LibraryPath}/Films`, `…/Series`, `…/Animes`. Chaque peer a un sous-dossier dédié : supprimer un peer permet de retirer tout son arbre (`Purge` / suppression du dossier du peer).

**Avantages :**
- Aucune modification des clients Jellyfin (médias transparents)
- Jellyfin gère la décision direct-play vs transcodage HLS sur la base des infos du `.nfo`
- Les métadonnées et l'artwork sont stockés localement
- Pas de proxy applicatif pour les streams

---

## Structure du plugin

```
Jellyfin.Plugin.JellyFed/
  Plugin.cs                        Point d'entrée. Auto-génère FederationToken et LibraryPath
                                   au premier démarrage.
  PluginServiceRegistrator.cs      Enregistrement DI

  Configuration/
    PluginConfiguration.cs         Paramètres : Peers[], FederationToken, LibraryPath,
                                   SyncIntervalHours, SelfUrl, SelfName, JellyfinApiKey,
                                   BlockedPeerUrls[]
    PeerConfiguration.cs           Un peer : Name, Url, FederationToken, AccessToken,
                                   Enabled, SyncMovies, SyncSeries, SyncAnime
    configPage.html                Page admin Jellyfin (JS vanilla, 4 onglets :
                                   Readme / Settings / Peers / Danger Zone)

  Api/
    FederationController.cs        Endpoints /JellyFed/* (catalog, stream, image, peers,
                                   peers/details, register, sync, peer/{name}/sync|purge|remove,
                                   PATCH peer/{name}, reset)
    FederationAuthFilter.cs        ServiceFilter : vérifie Bearer token de fédération
    Dto/
      CatalogItemDto.cs            Film ou série : métadonnées + codec + MediaStreams[]
      CatalogResponseDto.cs        Total + Items[]
      EpisodeDto.cs                Épisode : métadonnées + codec + MediaStreams[]
      SeasonDto.cs / SeasonsResponseDto.cs
      MediaStreamInfoDto.cs        Une piste audio ou sous-titre (Type, Codec, Language, ...)
      PeerDto.cs / PeersResponseDto.cs
      PeerDetailDto.cs / PeerDetailsResponseDto.cs   (onglet Peers)
      PeerSyncResultDto.cs                           (résultat d'un sync par peer)
      AddPeerRequestDto.cs / UpdatePeerRequestDto.cs (CRUD par peer)
      RegisterPeerRequestDto.cs / RegisterPeerResponseDto.cs
      SyncPeerRequestDto.cs / PurgePeerCatalogRequestDto.cs
      ManifestStatsDto.cs / PeerCatalogStatsDto.cs

  Sync/
    FederationSyncTask.cs          IScheduledTask — sync périodique + pruning + mise à jour NFO.
                                   Expose SyncPeerAsync(peer, ct) réutilisé par l'endpoint
                                   /peer/{name}/sync (exécution inline).
    PeerSyncResult.cs              Résultat (added/skipped/pruned/duration/error) d'une passe par peer
    PeerClient.cs                  Client HTTP vers instances distantes (+ HealthCheckAsync)
    StrmWriter.cs                  Génère/met à jour .strm + .nfo + télécharge artwork
    PeerHeartbeatService.cs        IHostedService — ping périodique des peers
    PeerStateStore.cs              Lecture/écriture .jellyfed-peers.json
    Manifest.cs / ManifestEntry.cs Modèles manifest
    PeerStatus.cs                  Modèle statut : heartbeat + LastSyncAt/Status/Error/DurationMs
    FederatedPathHelper.cs         Résolution des 3 racines + suppression dossier par peer
    CatalogItemClassifier.cs       Classification anime vs movie/series selon les genres
```

---

## Interfaces Jellyfin utilisées

### `IScheduledTask` — sync périodique

```
FederationSyncTask.ExecuteAsync() :
  1. BuildLocalTmdbIds() → TMDB IDs locaux (hors jellyfed-library), pour dédup
  2. Pour chaque peer enabled :
     a. GET /JellyFed/catalog → catalogue distant
     b. Pour chaque item :
        - TMDB ID local → skip (dédup)
        - Déjà dans manifest → UpdateMovieNfoAsync() pour rafraîchir codec/tracks, skip
        - Sinon → StrmWriter.WriteMovieAsync() ou WriteSeriesAsync()
          • Série → GET /JellyFed/catalog/series/:id/seasons → .strm par épisode
     c. POST /JellyFed/peer/register (auto-registration si SelfUrl configuré)
  3. Pruning : clés manifest absentes → StrmWriter.DeleteItem()
  4. SaveManifest() → .jellyfed-manifest.json
  5. QueueLibraryScan() → Jellyfin indexe les nouveaux/modifiés .strm
```

### `IHostedService` — heartbeat peers

```
PeerHeartbeatService : toutes les 5 min
  GET /JellyFed/health sur chaque peer
  → écrit résultat dans .jellyfed-peers.json (online, version, movieCount, seriesCount)
```

### `ILibraryManager` — bibliothèque locale

- `GetItemList()` → catalogue local (catalog endpoint + dédup TMDB)
- `QueueLibraryScan()` → déclenche rescan après sync
- `DeleteItem()` → supprime items de la DB Jellyfin (purge/reset)

---

## Flux de synchronisation complet

```
1. Admin configure peer-b dans le panneau JellyFed
2. FederationSyncTask se déclenche (planifié ou Sync Now)
3. BuildLocalTmdbIds() → TMDB IDs présents localement
4. GET /JellyFed/catalog sur peer-b
   Authorization: Bearer <access_token_peer_b>
5. Pour chaque item :
   - TMDB ID dans localTmdbIds → skip
   - Clé dans manifest → UpdateMovieNfoAsync() (mise à jour codec/tracks)
   - Sinon :
     • Movie → StrmWriter.WriteMovieAsync()
       - .strm : "https://peer-b/JellyFed/stream/{id}?token={fedToken}"
       - .nfo  : métadonnées + <fileinfo><streamdetails> (codec, audio, subtitles)
       - poster.jpg + fanart.jpg téléchargés
     • Series → GetSeasonsAsync() → StrmWriter.WriteSeriesAsync()
       - .strm par épisode avec URL stream
       - .nfo par épisode avec codec + pistes
6. Pruning : clés manifest non vues → StrmWriter.DeleteItem()
7. SaveManifest()
8. POST /JellyFed/peer/register → peer-b ajoute cette instance en retour
9. QueueLibraryScan()
```

---

## Proxy stream & image

Depuis v0.1.0.12, les `.strm` ne contiennent plus de clés API Jellyfin. À la place :

```
.strm : https://peer-b/JellyFed/stream/{itemId}?token={federationToken}
```

**`GET /JellyFed/stream/{itemId}?token=...`** (source server) :
- Si `JellyfinApiKey` configurée → redirect `302` vers `/Videos/{id}/stream?api_key={key}&Static=true`
  - `Static=true` : fichier brut avec range request support → seeking fonctionnel
- Sinon → `PhysicalFile(item.Path, mimeType, enableRangeProcessing: true)`

**`GET /JellyFed/image/{itemId}/{type}?token=...`** (source server) :
- Si `JellyfinApiKey` configurée → les URLs de catalog pointent directement vers `/Items/{id}/Images/{type}?api_key=...` (fiable, bonne résolution)
- Sinon → `PhysicalFile(imageInfo.Path, mimeType)` (lit `item.ImageInfos`)

---

## Décision de lecture côté Jellyfin client

Les infos codec dans le NFO sont critiques pour que le Jellyfin client prenne la bonne décision :

```
Jellyfin lit .nfo → codec = hevc, pistes audio fre/eng, sous-titres fre/eng
          ↓
PlaybackInfo → client browser ne supporte pas HEVC
          ↓
Décision : transcode HLS
          ↓
FFmpeg lit depuis https://peer-b/JellyFed/stream/{id}?token=...
  → source supporte range requests → seeking possible
  → FFmpeg transcode H264/AAC → HLS segments
  → browser joue HLS
```

Sans les infos codec dans le NFO : Jellyfin suppose direct-play → browser reçoit MKV brut → fatal player error.

---

## Authentification inter-instances

### Token global (bootstrap)

À la configuration initiale, A présente le token global de B. `FederationAuthFilter` vérifie que le Bearer correspond au `FederationToken` global.

### Tokens d'accès par peer (post auto-registration)

```
1. A POST /JellyFed/peer/register → B génère AccessToken("xyz") pour A → retourné
   A stocke : Peers[B].FederationToken = "xyz"
   B stocke  : Peers[A].AccessToken = "xyz"

2. A GET /JellyFed/catalog sur B → Authorization: Bearer xyz
   B vérifie : AccessToken du peer A == "xyz" → OK

3. A supprime B → AccessToken "xyz" invalidé → B renvoie 401 à A
```

**`FederationAuthFilter` — logique :**
1. Bearer correspond à `AccessToken` d'un peer activé → OK (per-peer)
2. Sinon, Bearer correspond au `FederationToken` global → OK (fallback bootstrap)
3. Sinon → 401

---

## Auto-registration bidirectionnelle

```
1. A configure B manuellement (URL + token de B)
2. A synce depuis B
3. A POST /JellyFed/peer/register { name: "A", url: "...", federationToken: "token-A" }
4. B vérifie blacklist, ajoute A (Enabled=true, SyncMovies=true, SyncSeries=true)
5. Prochain cycle de B → B synce depuis A
```

---

## Manifest

**Clé de manifest :**
- Avec TMDB ID → `"tmdb:{tmdbId}"`
- Sans TMDB ID → `"no-tmdb:{peerName}:{jellyfinId}"`

**Entrée :**
```json
{
  "movies": {
    "tmdb:872585": {
      "path": "/data/jellyfin/data/jellyfed-library/Films/Oppenheimer (2023)",
      "peerName": "instance-b",
      "jellyfinId": "abc123def456",
      "syncedAt": "2026-04-13T01:58:14Z"
    }
  }
}
```

---

## Configuration

```xml
<PluginConfiguration>
  <FederationToken>auto-généré au démarrage</FederationToken>
  <SyncIntervalHours>6</SyncIntervalHours>
  <LibraryPath>auto-défini : {DataPath}/jellyfed-library</LibraryPath>
  <SelfName>mon-serveur</SelfName>
  <SelfUrl>https://mon-jellyfin.example.com</SelfUrl>
  <JellyfinApiKey>optionnel — clé API dédiée Jellyfin locale</JellyfinApiKey>
  <Peers>
    <PeerConfiguration>
      <Name>instance-b</Name>
      <Url>https://peer-b.example.com</Url>
      <FederationToken>token-ou-accessToken-peer-b</FederationToken>
      <AccessToken></AccessToken>  <!-- généré par peer-b après registration -->
      <Enabled>true</Enabled>
      <SyncMovies>true</SyncMovies>
      <SyncSeries>true</SyncSeries>
      <SyncAnime>true</SyncAnime>
    </PeerConfiguration>
  </Peers>
  <BlockedPeerUrls>
    <string>https://peer-ex.example.com</string>
  </BlockedPeerUrls>
</PluginConfiguration>
```

**`LibraryPath` par plateforme :**
| Plateforme | Valeur par défaut |
|---|---|
| Docker Jellyfin | `/config/data/jellyfed-library` |
| Linux standalone | `~/.local/share/jellyfin/data/jellyfed-library` |
| Windows | `%ProgramData%\Jellyfin\Server\data\jellyfed-library` |

---

## Panneau admin — onglets et endpoints Peers

La page `configPage.html` est structurée en 4 onglets côté client :

| Onglet | Contenu |
|---|---|
| **Readme** (défaut) | Tagline, schéma concept, setup rapide 4 étapes, liens docs externes (statique, sans fetch réseau) |
| **Settings** | Token, intervalle, racines (LibraryPath / Movies / Series / Anime), Self URL / Name, Jellyfin API key. Bouton Save → `PUT /System/Configuration/...` via `ApiClient.updatePluginConfiguration` |
| **Peers** | Liste de cartes `PeerDetailDto` + Blocked Peers (déplacés ici) + modal Add peer |
| **Danger Zone** | Bouton Reset Network uniquement |

### Endpoints utilisés par l'onglet Peers

| Endpoint | Rôle |
|---|---|
| `GET /JellyFed/peers/details` | Agrège `PeerConfiguration` + `PeerStatus` + manifest + taille disque + dossiers effectifs. Renvoie `PeerDetailsResponseDto` avec `LastGlobalSyncAt`. |
| `POST /JellyFed/peers` | Crée un peer depuis la modal Add peer. Fait un health-check `/JellyFed/health` sur l'URL fournie, enlève l'URL de `BlockedPeerUrls` et sauve. |
| `POST /JellyFed/peer/{name}/sync` | Sync par peer exécuté *inline* via `FederationSyncTask.SyncPeerAsync`. Renvoie `PeerSyncResultDto` (counts + durée + erreur éventuelle). |
| `POST /JellyFed/peer/{name}/purge` | Supprime les `.strm` + entrées manifest du peer, vide les dossiers, reset les compteurs `PeerStatus` ; la config du peer est conservée. |
| `POST /JellyFed/peer/{name}/remove` | Purge + révoque `AccessToken` + ajoute l'URL à `BlockedPeerUrls` + retire de `config.Peers`. |
| `PATCH /JellyFed/peer/{name}` | Update partiel. Si `Name` change : renomme les dossiers `{Root}/{oldSeg}` → `{Root}/{newSeg}` via `Directory.Move`, réécrit les `Path` + `PeerName` dans le manifest et renomme la clé de `.jellyfed-peers.json`. |

Tous ces endpoints sont protégés par `FederationAuthFilter` (Bearer = `FederationToken`).

### Sync par peer — `SyncPeerAsync`

`FederationSyncTask.ExecuteAsync` itère désormais en appelant `SyncSinglePeerAsync` pour chaque peer, puis marque le `PeerStatus` (`MarkSynced(durationMs)` ou `MarkSyncFailed(error, durationMs)`). `SyncPeerAsync(peer, ct)` réutilise la même passe pour l'endpoint `/peer/{name}/sync` : manifest chargé, pipeline complet, pruning scoped au peer, `QueueLibraryScan()`. Le result (`PeerSyncResult`) est aussi persisté dans `PeerStatus` avant retour à l'UI.

---

## Ce que JellyFed N'est PAS

- **Pas un proxy** : le stream va du client directement vers le peer (ou via FFmpeg local si transcodage)
- **Pas un remplaçant de Jellyfin** : le plugin vit dans Jellyfin, les clients restent inchangés
- **Pas une sync de fichiers** : les fichiers restent chez le peer, seules les métadonnées sont copiées
- **Pas P2P au sens BitTorrent** : architecture fédérée (chaque instance = serveur autonome)
