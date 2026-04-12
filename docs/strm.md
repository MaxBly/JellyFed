# JellyFed — Fichiers `.strm` dans Jellyfin

## Qu'est-ce qu'un fichier `.strm` ?

Un `.strm` est un fichier texte contenant une seule URL. Jellyfin le supporte nativement depuis longtemps (héritage de Kodi/XBMC). Quand le scanner le trouve, il crée un item de bibliothèque normal — le `.strm` est transparent pour les clients.

```
# Oppenheimer (2023).strm
https://peer-b.example.com/Videos/abc123/stream?api_key=fed_key_xyz&Static=true
```

---

## Structure de dossier générée par JellyFed

### Films

```
/jellyfed-library/
  Films/
    Oppenheimer (2023)/
      Oppenheimer (2023).strm
      Oppenheimer (2023).nfo
      poster.jpg
      fanart.jpg
```

Le dossier `Oppenheimer (2023)/` est le nom canonique — Jellyfin utilise le dossier parent pour le titre et l'année.

### Séries

```
/jellyfed-library/
  Series/
    Breaking Bad/
      tvshow.nfo
      poster.jpg
      fanart.jpg
      Season 01/
        S01E01 - Pilot.strm
        S01E01 - Pilot.nfo
        S01E02 - Cat's in the Bag.strm
        S01E02 - Cat's in the Bag.nfo
```

---

## Fichiers `.nfo` — métadonnées locales

Jellyfin lit les fichiers `.nfo` au format Kodi/NFO. Stocker les métadonnées localement évite de re-requêter le peer à chaque accès.

**Film NFO (`Oppenheimer (2023).nfo`) :**
```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<movie>
  <title>Oppenheimer</title>
  <originaltitle>Oppenheimer</originaltitle>
  <year>2023</year>
  <plot>The story of J. Robert Oppenheimer...</plot>
  <runtime>181</runtime>
  <rating>8.1</rating>
  <genre>Drama</genre>
  <genre>History</genre>
  <uniqueid type="tmdb" default="true">872585</uniqueid>
  <uniqueid type="imdb">tt15398776</uniqueid>
  <thumb aspect="poster">poster.jpg</thumb>
  <fanart>
    <thumb>fanart.jpg</thumb>
  </fanart>
  <jellyfed_peer>peer-b</jellyfed_peer>
  <jellyfed_id>abc123</jellyfed_id>
</movie>
```

Les tags `jellyfed_peer` et `jellyfed_id` sont des extensions custom — Jellyfin les ignore mais JellyFed les lit pour savoir quel peer contacter lors d'une resync.

**Épisode NFO (`S01E01 - Pilot.nfo`) :**
```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<episodedetails>
  <title>Pilot</title>
  <season>1</season>
  <episode>1</episode>
  <plot>Walt starts cooking meth...</plot>
  <aired>2008-01-20</aired>
  <runtime>47</runtime>
  <thumb>https://peer-b.example.com/Items/ep001/Images/Primary</thumb>
  <jellyfed_peer>peer-b</jellyfed_peer>
  <jellyfed_id>ep001</jellyfed_id>
</episodedetails>
```

---

## Comportement Jellyfin avec un `.strm`

1. Scanner trouve `Oppenheimer (2023).strm`
2. Jellyfin lit le `.nfo` → remplit les métadonnées (titre, année, genres, artwork)
3. Si pas de `.nfo` → Jellyfin tente une recherche TMDB via le nom de fichier (fallback)
4. Lors de la lecture : Jellyfin envoie l'URL du `.strm` au client comme `PlaybackInfo`
5. Le client Jellyfin (web, Android, Infuse...) streame directement depuis l'URL → **le serveur local n'est pas dans le chemin du stream**

---

## Cas particulier : transcodage

Si le client demande un transcodage (codec non supporté, qualité réduite) :
- Jellyfin local reçoit la demande de transcode
- Il démarre ffmpeg en lisant depuis l'URL du `.strm` (le stream distant)
- ffmpeg streame depuis peer-b → transcode → envoie au client

C'est transparent, mais ça consomme la bande passante du serveur local ET du peer. À éviter pour les clients modernes (DirectPlay natif).

---

## Artwork

JellyFed télécharge les artworks lors de la sync et les stocke localement dans le dossier du film/série. Deux raisons :

1. **Rapidité** : Jellyfin sert l'artwork depuis le disque local, pas de requête vers le peer à chaque accès
2. **Résilience** : si le peer est hors ligne, l'artwork reste disponible

Fichiers téléchargés :
- `poster.jpg` — poster principal (140×210px minimum)
- `fanart.jpg` — backdrop/fanart (1280×720px minimum)

---

## Gestion des suppressions

Lors d'une resync, si un item n'est plus dans le catalogue du peer :
1. JellyFed détecte l'absence
2. Supprime le `.strm`, `.nfo`, et artwork correspondants
3. Notifie le scanner Jellyfin
4. L'item disparaît de la bibliothèque

Grace period configurable (ex : 7 jours) avant suppression définitive, pour éviter les faux positifs lors d'une indisponibilité temporaire du peer.
