# JellyFed — Roadmap

## État d'avancement

| Phase | Statut | Notes |
|---|---|---|
| Scaffolding plugin Jellyfin | Fait | Plugin, config, page admin |
| API catalogue | Fait | `/JellyFed/catalog`, séries/saisons |
| Sync `.strm` / NFO | Fait | Layout v2 aplati |
| Gestion peers | Fait | CRUD, toggles, purge/remove |
| Tokens per-peer | Fait | Access tokens révocables |
| Stream/image proxy | Fait | Pas de clé API Jellyfin dans `.strm` |
| Jellyfin 10.11 / .NET 9 | Fait | Packages 10.11.8 |
| Discovery v1 | Fait | Suggestions manual-add only |
| Audit SQLite | Fait | Logs admin-only |
| A2/A3/A6 metadata sources | Fait | Durée, streams complets, bitrate/size/HDR/edition |
| A1 layout aplati | Fait côté code | À valider en environnement 3 instances |
| A5 joignabilité streamUrl | Fait côté code/doc | `SelfUrl` préféré, fallback requête |
| BUG-05 SRT/ASS soft-sub | À faire | Validation et fix runtime |
| Tests/hardening v1 | À faire | E2E, migration/reset, multi-clients |

---

## Priorités

1. Valider le layout aplati sur 3 instances Jellyfin.
2. Vérifier la fusion native Jellyfin des `.strm` multi-versions.
3. Valider local + peer dans une même bibliothèque Jellyfin.
4. Reproduire et corriger `BUG-05`.
5. Ajouter tests ciblés ou scripts de validation.
6. Bumper `build.yaml` et publier une release propre.

---

## Architecture actuelle

L'approche `sources.json` + `FederationMediaSourceProvider` est abandonnée. Elle exposait des `MediaSourceInfo.Id` non-Guid et cassait le transcodage de sources alternatives.

L'architecture v2 matérialise une version `.strm` par peer dans un dossier logique commun :

```text
{MoviesRoot}/Film (2024) [tmdbid-123]/
  Film (2024) [peer-a].strm
  Film (2024) [peer-b].strm
```

La sélection de version est confiée à Jellyfin.

---

## Tests manuels prioritaires

### TEST-01 — Sync film multi-peer

Un même film présent sur deux peers doit produire deux `.strm` dans le même dossier `[tmdbid-X]`.

Critères :

- un seul dossier item ;
- deux `.strm` suffixés `[peer-X]` ;
- NFO avec même TMDB ID ;
- Jellyfin affiche une seule carte ou permet la fusion en versions.

### TEST-02 — Sync série multi-peer

Une même série présente sur deux peers doit produire des épisodes suffixés par peer dans chaque dossier saison.

### TEST-03 — Local + peer

Un média local et une version fédérée avec même TMDB ID doivent coexister/fusionner quand les racines sont dans la même bibliothèque Jellyfin.

### TEST-04 — Transcodage source alternative

Choisir une version HEVC/MKV distante depuis le player Jellyfin doit lancer un transcodage HLS sans erreur `Guid.Parse`.

### TEST-05 — Purge peer

Purger un peer doit supprimer uniquement ses fichiers `[peer-X]` et conserver l'item si une autre source reste.

### TEST-06 — Remove peer

Remove doit purger, révoquer l'access token, retirer la config et blacklister l'URL.

### TEST-07 — Rename peer

Renommer un peer doit renommer les fichiers `[peer-old]` vers `[peer-new]` et mettre à jour le manifest.

### TEST-08 — SelfUrl joignable

Les URLs `.strm` doivent utiliser `SelfUrl` quand configuré et être joignables depuis le conteneur/processus Jellyfin consommateur.

### TEST-09 — Seeking

Le seek doit fonctionner en direct-play et en HLS transcodé.

### TEST-10 — Audio tracks

Les pistes audio exposées dans les NFO doivent apparaître dans le player.

### TEST-11 — BUG-05 SRT/ASS

Les sous-titres texte doivent être validés en HLS distant. Si absents, investiguer le pipeline Jellyfin/FFmpeg WebVTT.

---

## Bugs connus

### BUG-05 — Sous-titres SRT/ASS non affichés

Statut : ouvert, P1.

Symptôme : les sous-titres texte peuvent ne pas apparaître lors du transcodage HLS depuis une URL HTTP distante.

Pistes :

- vérifier le comportement après layout aplati natif ;
- inspecter les commandes FFmpeg Jellyfin ;
- investiguer l'extraction WebVTT depuis source distante.

### BUG-06 — PGS hard-sub

Statut : limitation Jellyfin.

Les sous-titres PGS bitmap peuvent être brûlés dans l'image pendant le transcodage HLS.

---

## Post-v1

- Discovery plus riche / gossip récursif.
- Recall de catalogue.
- Suppression propagée.
- Refonte UI settings.
- Distribution publique stabilisée.
