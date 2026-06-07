# JellyFed — fichiers `.strm` et NFO

Un fichier `.strm` est un fichier texte contenant une URL de média. Jellyfin le scanne comme un média normal, puis lit l'URL au moment du playback.

```text
https://peer-b.example.com/JellyFed/stream/abc123?token=...
```

JellyFed utilise ce mécanisme pour représenter des médias distants sans copier les fichiers vidéo.

---

## Layout v2

### Films

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

### Séries

```text
{SeriesRoot}/
  Breaking Bad (2008) [tmdbid-1396]/
    tvshow.nfo
    poster.jpg
    fanart.jpg
    Season 01/
      S01E01 - Pilot [peer-b].strm
      S01E01 - Pilot [peer-b].nfo
      S01E01 - Pilot [peer-c].strm
      S01E01 - Pilot [peer-c].nfo
```

Le suffixe `[peer-X]` identifie la source. Plusieurs fichiers `.strm` dans le même dossier représentent plusieurs versions du même item. Jellyfin peut les fusionner nativement grâce aux IDs TMDB dans les NFO.

---

## NFO

JellyFed écrit des NFO Kodi pour donner à Jellyfin les métadonnées et les infos techniques nécessaires.

Exemple film :

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<movie>
  <title>Anora</title>
  <year>2024</year>
  <uniqueid type="tmdb" default="true">1064213</uniqueid>
  <jellyfed_peer>peer-b</jellyfed_peer>
  <jellyfed_id>abc123</jellyfed_id>
  <jellyfed_source_count>2</jellyfed_source_count>
  <tag>JellyFed</tag>
  <tag>JellyFed:multi-source</tag>
  <studio>JellyFed:peer-b</studio>
  <studio>JellyFed:peer-c</studio>
  <fileinfo>
    <streamdetails>
      <video>
        <codec>hevc</codec>
        <width>3840</width>
        <height>2160</height>
      </video>
      <audio>
        <codec>eac3</codec>
        <language>eng</language>
      </audio>
      <subtitle>
        <language>fre</language>
      </subtitle>
    </streamdetails>
  </fileinfo>
</movie>
```

`<fileinfo><streamdetails>` est critique : sans ces infos, Jellyfin peut supposer à tort qu'un navigateur sait lire un MKV/HEVC distant en direct.

---

## Playback

```text
1. Jellyfin scanne les .strm + .nfo.
2. Les fichiers avec même TMDB ID sont vus comme versions du même item.
3. Le client demande PlaybackInfo à l'instance locale.
4. Jellyfin choisit direct-play, direct-stream ou transcodage HLS.
5. Si transcodage, le FFmpeg local lit l'URL /JellyFed/stream/{id}?token=...
```

Si `JellyfinApiKey` est configurée sur le peer source, `/JellyFed/stream/{id}` redirige vers le pipeline Jellyfin natif avec `Static=true`, ce qui conserve les range requests et le seeking.

Sinon, JellyFed sert le fichier physique directement avec `enableRangeProcessing`.

---

## Contrat `SelfUrl`

Les URLs écrites dans les `.strm` sont générées depuis `SelfUrl` quand il est configuré. Sinon, JellyFed utilise l'URL de la requête courante.

La règle importante :

```text
SelfUrl du peer source doit être joignable par le FFmpeg du peer consommateur.
```

Exemples :

- si les instances sont sur le même LAN, une URL LAN stable peut être préférable ;
- si une instance distante consomme via Internet, `SelfUrl` doit être public/HTTPS ;
- dans un setup Docker, l'URL doit être joignable depuis le conteneur Jellyfin, pas seulement depuis le navigateur admin.

---

## Pruning

Lors d'une sync :

1. JellyFed marque les sources vues dans le catalogue du peer.
2. Les sources absentes sont retirées du manifest.
3. Les fichiers `*[peer-X].strm` et `*[peer-X].nfo` sont supprimés.
4. Si d'autres sources restent, l'item logique reste.
5. Si aucune source ne reste, le dossier de l'item est supprimé.

Il n'y a plus de promotion de source primaire : chaque fichier `.strm` est une version indépendante.

---

## Artwork

JellyFed télécharge :

- `poster.jpg`
- `fanart.jpg`

Ces fichiers sont partagés par l'item logique et ne sont pas dupliqués par peer.

---

## Sous-titres

Les pistes de sous-titres sont exposées dans les NFO et dans le catalogue.

Limites connues :

- `BUG-05` : les sous-titres texte SRT/ASS/SubRip doivent encore être validés/corrigés en soft-sub lors du transcodage HLS depuis source HTTP distante.
- `BUG-06` : les sous-titres PGS peuvent être brûlés en hard-sub par Jellyfin/FFmpeg, comportement attendu côté Jellyfin.
