# JellyFed — Architecture technique

## Vue d'ensemble

JellyFed est un plugin Jellyfin (C# / .NET 8) qui implémente la fédération de bibliothèques entre instances. Il s'appuie exclusivement sur les interfaces publiques de Jellyfin : pas de fork, pas de patch, compat avec les mises à jour Jellyfin.

---

## Principe fondamental : les fichiers `.strm`

Jellyfin supporte nativement les fichiers `.strm` : un fichier texte qui contient une URL. Quand le scanner trouve un `.strm`, il l'indexe comme un média normal (film, épisode) et envoie l'URL comme source de lecture au client.

```
/jellyfed-library/
  Films/
    Oppenheimer (2023)/
      Oppenheimer (2023).strm         → "https://peer-b.example.com/Videos/abc123/stream?api_key=..."
      Oppenheimer (2023).nfo          → métadonnées XML (titre, année, TMDB ID, synopsis, etc.)
      poster.jpg                      → téléchargé depuis peer-b
  Series/
    Breaking Bad/
      Season 01/
        S01E01 - Pilot.strm           → URL stream épisode
        S01E01 - Pilot.nfo
```

**Avantages :**
- Aucune modification des clients Jellyfin (ils voient des médias normaux)
- Jellyfin gère le transcodage local si nécessaire (ou DirectPlay vers l'URL distante)
- Les métadonnées (artwork, synopsis, genres) sont stockées localement dans les `.nfo`
- Pas de proxy applicatif : le client streame directement depuis le peer distant

---

## Interfaces Jellyfin utilisées

### `IScheduledTask` — sync périodique

```csharp
public class FederationSyncTask : IScheduledTask
{
    // S'exécute toutes les N heures (configurable)
    // Pour chaque peer : GetItems() → générer .strm + .nfo + télécharger poster
    // Gestion des suppressions : si un média a disparu chez le peer, supprimer le .strm
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
}
```

### `IMediaSourceProvider` — override source de lecture (option avancée)

Dans la plupart des cas, le `.strm` suffit : Jellyfin l'envoie directement au client.

Pour les cas avancés (token rotatif, authentification dynamique) :

```csharp
public class FederationMediaSourceProvider : IMediaSourceProvider
{
    // Intercepte les demandes de lecture pour les items JellyFed
    // Peut injecter un token frais, choisir la meilleure qualité, etc.
    public Task<IEnumerable<MediaSourceInfo>> GetAdditionalSources(
        string itemId, CancellationToken cancellationToken)
}
```

### API custom — `/JellyFed/` endpoints

Jellyfin permet d'enregistrer des contrôleurs API via `IRestfulApiPlugin` ou en héritant de `BaseApiController` :

```
GET  /JellyFed/catalog            Retourne le catalogue de cette instance (films + séries)
GET  /JellyFed/peers              Liste les peers connus et leur statut
POST /JellyFed/peer/register      Enregistrer un nouveau peer (avec auth token)
POST /JellyFed/peer/sync          Déclencher une sync manuelle avec un peer
GET  /JellyFed/health             Heartbeat (pour gossip protocol)
```

---

## Flux de synchronisation

```
1. Admin configure peer-b (URL + API key) dans le panneau JellyFed
2. IScheduledTask se déclenche (ou sync manuelle)
3. GET /JellyFed/catalog sur peer-b
   → retourne liste {JellyfinID, TMDBID, titre, année, type, ...}
4. Déduplication par TMDB ID :
   - Item déjà dans la bibliothèque locale → skip (ou mise à jour metadata)
   - Item inconnu → créer .strm + .nfo dans /jellyfed-library/
5. Téléchargement artwork (poster, backdrop) → stocké localement
6. Notification scanner Jellyfin → ILibraryManager.ValidateMediaLibrary()
7. Jellyfin indexe les nouveaux .strm → apparaissent dans le catalogue
```

---

## Déduplication multi-sources

Problème : même film disponible sur peer-b ET peer-c → on ne veut pas deux entrées.

Solution : le TMDB ID est la clé de déduplication.

```
/jellyfed-library/Films/Oppenheimer (2023)/
  Oppenheimer (2023).strm        → URL peer-b (source primaire)
  sources.json                   → {"tmdb": 872585, "sources": [
                                      {"peer": "peer-b", "url": "...", "quality": "4K"},
                                      {"peer": "peer-c", "url": "...", "quality": "1080p"}
                                    ]}
```

`IMediaSourceProvider` retourne les deux sources — le client Jellyfin les voit toutes les deux et peut choisir.

Films sans TMDB ID → déduplication par `{titre, année}` normalisé.

---

## Découverte P2P (gossip protocol)

Chaque instance connaît ses peers directs. Les peers échangent leurs listes de peers connus :

```
Peer-a connaît : [peer-b]
Peer-b connaît : [peer-a, peer-c, peer-d]

Peer-a → GET /JellyFed/peers sur peer-b
→ découvre peer-c et peer-d
→ peut décider de se connecter à peer-c (avec accord de l'admin)
```

**Règles gossip :**
- Pas d'auto-connexion : admin doit approuver chaque nouveau peer
- Propagation limitée à 2 hops (anti-spam)
- Chaque peer publie sa "trust list" : peers dont il partage l'adresse

---

## Authentification inter-instances

```
[Instance A]                    [Instance B]
     │                               │
     │  POST /JellyFed/peer/register │
     │  {"url": "https://peer-a...", │
     │   "federation_token": "..."}  │
     │ ──────────────────────────────▶
     │                               │
     │  {"status": "pending"}        │
     │ ◀──────────────────────────────
     │                               │
     │  (admin peer-b approuve)      │
     │                               │
     │  GET /JellyFed/catalog        │
     │  Authorization: Bearer <tok>  │
     │ ──────────────────────────────▶
```

Le `federation_token` est distinct de l'API key Jellyfin — droit minimal (lecture catalogue + stream uniquement).

---

## Structure du plugin C#

```
JellyFed/
  Plugin.cs                      Point d'entrée, IPlugin
  PluginConfiguration.cs         Paramètres (peers, sync interval, library path)
  
  Api/
    FederationController.cs      Endpoints /JellyFed/*
    CatalogDto.cs                DTOs JSON catalogue
    PeerDto.cs                   DTOs JSON peers
  
  Sync/
    FederationSyncTask.cs        IScheduledTask — sync périodique
    StrmWriter.cs                Génère .strm + .nfo + artwork
    PeerClient.cs                Client HTTP vers instances distantes
    Deduplicator.cs              Logique déduplication TMDB ID
  
  Media/
    FederationMediaSourceProvider.cs   IMediaSourceProvider (optionnel)
  
  Discovery/
    GossipService.cs             Échange de listes de peers
```

---

## Configuration (panneau admin Jellyfin)

```json
{
  "SyncIntervalHours": 6,
  "LibraryPath": "/jellyfed-library",
  "FederationToken": "abc123...",
  "Peers": [
    {
      "Name": "peer-b",
      "Url": "https://jellyfin.friend.example.com",
      "FederationToken": "xyz789...",
      "Enabled": true,
      "SyncMovies": true,
      "SyncSeries": true
    }
  ]
}
```

---

## Ce que JellyFed N'est PAS

- **Pas un proxy** : le stream va directement du client au peer (ou via l'instance locale si transcodage demandé)
- **Pas un remplaçant de Jellyfin** : le plugin vit dans Jellyfin, les clients restent inchangés
- **Pas une solution de sync de fichiers** : les fichiers restent chez le peer, seules les métadonnées sont copiées localement
- **Pas P2P au sens BitTorrent** : architecture fédérée (chaque instance = serveur), pas de DHT ni de swarm
