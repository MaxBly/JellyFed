# JellyFed — validation v1

Ce document liste les validations runtime à exécuter avant tag v1. Il complète les tests manuels de `docs/roadmap.md`.

## Environnement recommandé

- 3 instances Jellyfin 10.11.x.
- JellyFed installé sur chaque instance.
- `SelfUrl` configuré avec une URL joignable depuis les autres instances.
- Une bibliothèque Jellyfin qui contient à la fois une racine locale et une racine JellyFed pour tester local + peer.

## MergeVersions natif

### Film sur deux peers

1. Peer B et peer C exposent le même film avec le même TMDB ID.
2. Peer A sync B puis C.
3. Vérifier sur disque :

```text
{MoviesRoot}/Film (2024) [tmdbid-XXX]/
  Film (2024) [peer-b].strm
  Film (2024) [peer-c].strm
```

4. Lancer un rescan Jellyfin.
5. Vérifier que Jellyfin présente un seul item avec plusieurs versions, ou que les versions peuvent être fusionnées nativement.

### Série sur deux peers

Même validation au niveau épisode :

```text
Season 01/
  S01E01 - Pilot [peer-b].strm
  S01E01 - Pilot [peer-c].strm
```

## Transcodage

1. Choisir une version distante HEVC/MKV depuis le player Jellyfin.
2. Vérifier que le transcodage HLS démarre sans erreur `Guid.Parse`.
3. Vérifier le seeking.

## Local + peer

1. Ajouter une racine média locale et `MoviesRoot` dans la même bibliothèque Jellyfin.
2. Avoir un média local et un média fédéré avec le même TMDB ID.
3. Vérifier la fusion ou la capacité à fusionner les versions dans Jellyfin.

## BUG-05 sous-titres texte

1. Utiliser une source distante avec SRT/ASS/SubRip.
2. Vérifier que les NFO générés contiennent les pistes `<subtitle><codec>...`.
3. Lancer une lecture nécessitant HLS.
4. Vérifier si Jellyfin génère des WebVTT et si le player affiche la piste.
5. Si absent, récupérer la commande FFmpeg Jellyfin et investiguer l'extraction WebVTT depuis URL HTTP distante.

## A5 SelfUrl

Depuis le processus ou conteneur Jellyfin consommateur :

```bash
curl -I "https://peer-source.example.com/JellyFed/stream/<itemId>?token=<token>"
```

Le test doit répondre `200`, `206` ou `302`.
