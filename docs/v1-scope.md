# JellyFed — Scope v1

## Objectif

La **v1.0.0** marque le gel de l'architecture du plugin. Au-delà de cette version :

- Les mises à jour sont installables sans reset ni reconfiguration.
- Les peers configurés, tokens, bibliothèques synchronisées et manifests restent valides.
- Les formats de fichiers (`.strm`, `.nfo`, `manifest.json`), le schéma de config et les routes API évoluent uniquement via des migrations rétrocompatibles.
- Les features ajoutées (post-v1) sont **additives** : nouveaux endpoints, nouveaux champs optionnels avec défauts, nouvelles pages UI — jamais de changement rompant.

Cette contrainte détermine quelles features **doivent** être implémentées avant v1 (celles qui modifient fortement le fonctionnement du plugin) et quelles features peuvent arriver après sans friction.

---

## Contrats publics à figer avant v1

Tout ce qui suit constitue l'interface stable du plugin. Une fois v1 publiée, chaque modification de ces contrats est soit bannie, soit obligatoirement versionnée avec migration.

| Contrat | Support | Impact d'un changement post-v1 |
|---|---|---|
| Layout bibliothèque | `{MoviesRoot|SeriesRoot|AnimeRoot}/{PeerName}/...` sur disque | Migration disque + rescan Jellyfin |
| Format `.strm` | Fichier texte, URL + token | Resync complet de tous les peers |
| Format `.nfo` | XML, `<fileinfo><streamdetails>` | Rescan Jellyfin (perte métadonnées en attendant) |
| Schema `.jellyfed-manifest.json` | JSON local au plugin | Perte de l'historique, pruning cassé |
| Schema `PluginConfiguration` | XML interne Jellyfin | Reconfiguration manuelle des peers |
| Routes API `/JellyFed/...` | HTTP inter-peers | Peers anciennes versions déconnectés |
| DTOs catalogue (`CatalogItemDto`, `EpisodeDto`, `MediaStreamInfoDto`) | JSON wire format | Sync cross-version rompue |

---

## Features obligatoires avant v1

Ces features modifient un ou plusieurs contrats ci-dessus. Les implémenter **après** v1 impliquerait un reset chez chaque utilisateur, ce qui contredit l'objectif de v1.

### 1. Versioning config + manifest (v0.1.0.15)

Ajouter un champ `"schemaVersion": 1` dans :
- `.jellyfed-manifest.json`
- `PluginConfiguration` (sérialisé par Jellyfin)

Mettre en place un `SchemaMigrator` capable de lire des schemas antérieurs et de les réécrire vers la version courante au démarrage du plugin. Sans ce mécanisme, toute évolution ultérieure du schéma (ex : nouveau champ dans `ManifestEntry`, nouveau champ de config peer) forcerait soit un reset, soit du code de migration ad hoc fragile.

**Contrat posé :** chaque document stocké par le plugin porte un numéro de version. Toute version du plugin sait lire les versions antérieures (ou refuse de démarrer avec un message clair si la version est trop récente).

### 2. Versioning de l'API (v0.1.0.16)

Préfixer toutes les routes du `FederationController` par `/JellyFed/v1/` :
- `/JellyFed/v1/catalog`
- `/JellyFed/v1/stream/{id}`
- `/JellyFed/v1/image/{id}/{type}`
- `/JellyFed/v1/series/{id}/seasons`
- `/JellyFed/v1/peer/register`
- `/JellyFed/v1/peer/heartbeat`
- `/JellyFed/v1/manifest/stats`
- etc.

Les routes `/JellyFed/...` (sans préfixe) restent alias vers `/JellyFed/v1/...` pendant la transition pour ne pas casser les peers déjà déployés.

Le `PeerClient` négocie la version lors du premier contact (champ `ProtocolVersion` déjà exposé dans le catalogue). Permet d'introduire plus tard un `/JellyFed/v2/` avec breaking changes, sans casser les peers v1.

**Contrat posé :** les chemins HTTP `/JellyFed/v1/*` sont stables pour la durée de vie de v1.x. Les breaking changes vont dans `/v2/`.

### 3. Layout bibliothèque per-peer — déjà implémenté, à figer proprement (v0.1.0.17)

La branche `temp`, réconciliée sur `main`, a déjà fait le vrai choix d'architecture :

- layout **par peer** ;
- trois racines configurables : `MoviesRootPath`, `SeriesRootPath`, `AnimeRootPath` ;
- sous-arbre dédié par peer : `{Root}/{PeerName}/...` ;
- `SyncAnime` géré séparément du couple films / séries.

Le sujet n'est donc plus « faut-il unified ou per-peer ? », mais :

1. **figer** ce layout comme contrat v1 ;
2. écrire la **migration** depuis les anciennes installations qui rangeaient plus à plat sous `{LibraryPath}` ;
3. documenter qu'un renommage de peer ou de racine implique une réécriture/migration encadrée, pas un changement libre post-v1.

Pourquoi avant v1 : le layout sur disque est déjà visible par l'utilisateur et piloté par la config. Sans migration/versioning explicite, l'upgrade depuis les builds antérieurs restera fragile.

**Contrat posé :** le layout v1 est per-peer. Les futures évolutions doivent être rétrocompatibles ou accompagnées d'une migration versionnée.

### 4. Multi-source / `IMediaSourceProvider` (v0.1.0.18)

Quand le même film (identifié par TMDB ID) est disponible chez plusieurs peers, le client Jellyfin doit voir toutes les sources dans le sélecteur et pouvoir choisir.

Implémentation :
- Un fichier `sources.json` à côté de chaque `.strm` — liste des peers disposant du contenu, avec URL stream par peer et métriques (résolution, codec, bitrate).
- `FederationMediaSourceProvider : IMediaSourceProvider` — lit `sources.json` et expose toutes les sources au pipeline Jellyfin.
- Le `.strm` principal continue de pointer vers la source par défaut (pour compat clients qui ignorent `IMediaSourceProvider`).

Pourquoi avant v1 : l'ajout de `sources.json` modifie la structure du layout (nouveau fichier par item). Introduire ce fichier en v1.x forcerait un resync complet pour le générer, et casserait le pruning qui ne saurait pas gérer les items orphelins.

**Contrat posé :** `sources.json` est la source de vérité pour les sources alternatives. Absence = une seule source (le peer qui a produit le `.strm`).

### 5. Tag peer dans NFO (v0.1.0.19, combiné avec fix SRT)

Ajouter `<studio>JellyFed:{PeerName}</studio>` dans chaque `.nfo` généré. Permet de filtrer nativement par peer dans l'interface Jellyfin (la propriété "Studios" est exposée dans les filtres standards).

Pourquoi avant v1 : ajouter le tag après v1 = rewrite forcé de tous les NFO existants au premier sync post-upgrade. Faisable techniquement via `UpdateMovieNfoAsync` (déjà en place), mais impose un passage unique coûteux et une fenêtre où les anciens items n'ont pas encore le tag. Préférable de l'avoir dès la v1.

**Contrat posé :** le format des `<studio>` dans les NFO générés inclut `JellyFed:{PeerName}`.

### 6. Fix SRT/ASS soft-sub (BUG-05, v0.1.0.19)

Les sous-titres texte (SRT, ASS, SubRip) ne s'affichent pas dans le player lors du transcodage HLS depuis une source HTTP distante. Les PGS sont brûlés en hard-sub (BUG-06, limitation Jellyfin assumée).

Investigation : comprendre pourquoi le pipeline FFmpeg de Jellyfin n'extrait pas les pistes texte en WebVTT quand la source est remote. Piste : option `-map 0:s:0 -c:s webvtt` dans le profil de transcodage, ou exposition explicite des pistes sous-titres dans le `MediaSourceInfo` renvoyé par `IMediaSourceProvider` (ce qui rejoint FEAT-08 ci-dessus).

Pourquoi avant v1 : c'est un bug fonctionnel majeur pour tout utilisateur avec du contenu sous-titré. Pas un breaking change en soi, mais indispensable pour qualifier la v1 de "prête".

---

## Plan de versions

```
v0.1.0.15  Release de réconciliation temp -> main (UI peers + layout per-peer + anime roots)
v0.1.0.16  Versioning config + manifest (schemaVersion, SchemaMigrator)
v0.1.0.17  Versioning API (/JellyFed/v1/ + alias transitoires)
v0.1.0.18  Migration legacy layout -> layout per-peer figé
v0.1.0.19  Multi-source (sources.json + IMediaSourceProvider)
v0.1.0.20  Tag <studio>JellyFed:peer</studio> + fix SRT soft-sub
v0.1.0.21  Tests d'intégration + hardening (migrations, edge cases)
v1.0.0     Release stable — architecture figée
```

Chaque version intermédiaire est mergeable indépendamment. Tests TEST-10 à TEST-19 (cf. `roadmap.md`) à valider avant chaque merge.

---

## Post-v1 — features non-breaking

Ces features n'affectent aucun contrat public et peuvent être ajoutées en v1.x sans migration.

| Feature | Version cible | Nature |
|---|---|---|
| UI settings refonte | v1.1 | Interne plugin, zéro impact format |
| FEAT-03 peer-of-peer discovery | v1.2 | Nouveau endpoint + champ config optionnel (`SharePeers`, défaut false) |
| FEAT-04 recall | v1.3 | Nouveau endpoint, opération manuelle |
| FEAT-05 suppression propagée | v1.3 | Nouveau endpoint `/peer/leave`, handler côté cible |
| TECH-01 rechargement config à chaud | v1.x | Interne |
| TECH-02 dernier message d'erreur par peer | v1.x | Champ additif dans `PeerStateStore`, avec défaut |
| TECH-03 tests d'intégration étendus | Continu | Zéro impact runtime |
| Distribution publique | v1.0+ | Packaging du repo manifest.json, pas d'impact code |

Toute feature future qui voudrait modifier un contrat public suivra le pattern :
1. Ajout d'un champ optionnel avec défaut rétrocompatible.
2. Bump `schemaVersion` dans le document concerné, migration écrite dans `SchemaMigrator`.
3. Si breaking inévitable : nouveau préfixe d'API `/JellyFed/v2/`, coexistence avec v1.

---

## Critères de validation v1.0.0

Avant de tagger la v1.0.0 :

1. Tous les tests TEST-01 à TEST-19 passent (cf. `roadmap.md`).
2. Tests de migration : installer v0.1.0.14 → upgrade vers v1.0.0 → vérifier que peers, tokens, manifest et `.strm` sont tous intacts et fonctionnels.
3. Tests cross-version : un peer en v0.1.0.14 doit continuer à sync avec un peer en v1.0.0 (via les alias `/JellyFed/...` → `/JellyFed/v1/...`).
4. Documentation figée : `architecture.md`, `api.md`, `strm.md` reflètent exactement l'état v1.
5. Changelog complet dans `build.yaml`.
