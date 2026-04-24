# JellyFed — Roadmap

## État d'avancement

| Phase | Statut | Version |
|-------|--------|---------|
| 0 — Scaffolding | ✅ | 0.1.0.1 |
| 1 — API catalogue | ✅ | 0.1.0.2 |
| 2 — Sync + .strm | ✅ | 0.1.0.3 |
| 3 — Gestion peers (base) | ✅ | 0.1.0.4 |
| 3b — Auto-registration bidirectionnelle | ✅ | 0.1.0.6 |
| 3c — Blacklist peers | ✅ | 0.1.0.7 |
| 3d — UI : Blocked Peers, Sync Now, Catalogue stats | ✅ | 0.1.0.8 |
| 3e — Tokens d'accès par peer (révocation) | ✅ | 0.1.0.9 |
| 3f — Exclusion .strm du catalogue exposé | ✅ | 0.1.0.10 |
| 3g — SelfName configurable | ✅ | 0.1.0.11 |
| 4a — Proxy stream/image (no API key in .strm) | ✅ | 0.1.0.12 |
| 4b — Compat Jellyfin 10.11 + .NET 9 | ✅ | 0.1.0.12 |
| 4c — HTTPS X-Forwarded-Proto + images natives | ✅ | 0.1.0.13 |
| 4d — Codec info NFO + seeking + pistes audio/sous-titres | ✅ | 0.1.0.14 |
| 4e — UI peers avancée + layout par peer + anime roots | ✅ | 0.1.0.15 |
| 5a — Versioning config + manifest | 🔜 | 0.1.0.16 |
| 5b — Versioning API `/JellyFed/v1/` | 🔜 | 0.1.0.17 |
| 5c — Migration legacy layout + gel du contrat disque | 🔜 | 0.1.0.18 |
| 5d — Multi-source (`sources.json` + `IMediaSourceProvider`) | 🔜 | 0.1.0.19 |
| 5e — Tag `<studio>` peer dans NFO + fix SRT soft-sub | 🔜 | 0.1.0.20 |
| 5f — Tests d'intégration + hardening | 🔜 | 0.1.0.21 |
| **v1.0.0 — Release stable (architecture figée)** | 🎯 | **1.0.0** |
| 6 — UI settings refonte | Post-v1 | v1.1 |
| 7 — Peer-of-peer discovery (FEAT-03) | Post-v1 | v1.2 |
| 8 — Recall + suppression propagée (FEAT-04/05) | Post-v1 | v1.3 |
| 9 — Distribution publique | Post-v1 | v1.x |

Le plan détaillé de la v1 (contrats à figer, motivations, critères de validation) est dans [`v1-scope.md`](v1-scope.md).

---

## Priorités

**Objectif v1 : figer l'architecture.** Toutes les features listées dans la phase 5 modifient des contrats publics (layout, schémas, API) et doivent être implémentées avant v1 pour que les versions suivantes soient upgradables sans reset.

```
P1  Versioning config + manifest (v0.1.0.16)          — prérequis migrations
P2  Versioning API /JellyFed/v1/ (v0.1.0.17)             — prérequis coexistence v1/v2
P3  Migration legacy layout + gel du layout par peer  — sécuriser l'upgrade depuis l'ancien layout plat
P4  Multi-source sources.json (v0.1.0.19)             — nouveau fichier par item
P5  Tag <studio> peer + fix SRT BUG-05 (v0.1.0.20)    — format NFO final
P6  Tests d'intégration + hardening (v0.1.0.21)       — validation migrations
```

Post-v1 (non-breaking, safe à ajouter en v1.x) : UI refonte, peer-of-peer discovery, recall, suppression propagée, distribution publique.

---

## Tests à valider

### Streaming & lecture (v0.1.0.12–0.1.0.14)

#### TEST-10 — Direct play H264/MP4
**Contexte :** Un film en H264/MP4 depuis le peer doit se lire sans transcodage.
**À vérifier :**
- La lecture démarre sans délai
- Aucun transcodage visible dans les logs Jellyfin du client
- La qualité est native (aucune dégradation)
- **Critère de succès :** lecture fluide, `PlayMethod: DirectPlay` ou `DirectStream` dans les logs

#### TEST-11 — Transcodage HLS MKV/HEVC
**Contexte :** Un film en HEVC/MKV doit être transcodé en H264/HLS par le Jellyfin client.
**Prérequis :** sync effectuée + rescan bibliothèque.
**À vérifier :**
- Le NFO du film contient `<fileinfo><streamdetails><video><codec>hevc</codec>...`
- Jellyfin décide de transcoder (visible dans les logs ou l'indicateur de qualité)
- La lecture démarre et la vidéo s'affiche correctement
- **Critère de succès :** lecture fluide, indicateur "Transcoding" visible dans le dashboard Jellyfin

#### TEST-12 — Seeking (saut temporel)
**Contexte :** L'utilisateur saute à un timestamp arbitraire (ex : 45:30) dans un film.
**À vérifier :**
- En direct play H264 : la barre de progression répond, le saut fonctionne
- En HLS transcodé : Jellyfin redémarre FFmpeg depuis le timestamp cible
- Le seek ne provoque pas d'erreur ni de rechargement de page
- Tester à différents points : début, milieu, dernières minutes
- **Critère de succès :** seek en < 3 secondes, aucune erreur

#### TEST-13 — Sélection piste audio
**Contexte :** Un film avec plusieurs pistes audio (ex : VO anglais + VF français).
**À vérifier :**
- Le sélecteur de piste audio apparaît dans le player Jellyfin
- Le nombre de pistes correspond aux pistes réelles du fichier source
- Changer de piste redémarre la lecture sur la bonne piste
- Les labels de langue (eng, fre...) sont affichés correctement
- **Critère de succès :** toutes les pistes audio sont visibles et sélectionnables

#### TEST-14 — Sous-titres PGS (bitmap)
**Comportement attendu :** Les sous-titres PGS (Blu-ray) sont brûlés dans l'image vidéo lors du transcodage (hard-sub). Pas de sélection possible, mais visibles si la piste était activée côté source.
**À vérifier :**
- Les sous-titres PGS s'affichent dans la vidéo transcodée
- Ils ne peuvent pas être désactivés (limitation connue — voir BUG-06)
- **Critère de succès :** sous-titres PGS visibles dans la vidéo

#### TEST-15 — Sous-titres SRT/ASS (texte) ← BUG CONNU
**Comportement attendu :** Les sous-titres texte (SRT, ASS, SubRip) devraient apparaître comme piste soft-sub sélectionnable dans le player.
**Comportement actuel :** Les sous-titres SRT/ASS ne s'affichent pas.
**Voir :** BUG-05

#### TEST-16 — Mise à jour automatique NFO existants
**Contexte :** Un film déjà synced (avant v0.1.0.14) doit récupérer les infos codec sans Reset Network.
**À vérifier :**
- Déclencher une sync (Sync Now)
- Vérifier que le `.nfo` du film contient maintenant `<fileinfo><streamdetails>`
- Effectuer un rescan de la bibliothèque dans Jellyfin
- Vérifier que les pistes audio/sous-titres apparaissent dans le player
- **Critère de succès :** infos codec présentes dans le NFO après sync, pistes visibles après rescan

### Authentification & tokens

#### TEST-01 — Fichiers distants supprimés chez les peers
**Contexte :** Quand A supprime le peer B, les `.strm` de B doivent disparaître.
**À vérifier :**
- Cliquer "Remove" sur le peer B depuis l'interface de A
- `{LibraryPath}/Films/` et `{LibraryPath}/Series/` vidés des items de B
- Items disparaissent de l'interface Jellyfin (sans rescan manuel)
- `.jellyfed-manifest.json` ne contient plus d'entrées pour B
- **Critère de succès :** disparition immédiate ou lors du prochain scan

#### TEST-02 — Accès au catalogue révoqué après suppression peer
**À vérifier :**
- Après que A supprime B, appeler `GET /JellyFed/v1/catalog` sur A avec l'ancien token de B
- **Critère de succès :** `401 Unauthorized`

#### TEST-03 — Auto-registration bidirectionnelle
**À vérifier :**
- A configure B manuellement
- A effectue une sync
- B voit A apparaître automatiquement dans sa liste de peers
- B est activé et synchronise les films de A au prochain cycle
- **Critère de succès :** A visible dans les peers de B, sync de B vers A fonctionnelle

### Images & URLs

#### TEST-17 — Images via proxy JellyFed (sans JellyfinApiKey)
**Contexte :** `JellyfinApiKey` non configurée → URLs `/JellyFed/v1/image/{id}/{type}?token=...`
**À vérifier :**
- Les posters s'affichent dans Jellyfin (en local depuis le disque — téléchargés à la sync)
- Les backdrops s'affichent
- **Critère de succès :** artwork visible dans la bibliothèque fédérée

#### TEST-18 — Images via API Jellyfin native (avec JellyfinApiKey)
**Contexte :** `JellyfinApiKey` configurée → URLs `/Items/{id}/Images/Primary?api_key=...`
**À vérifier :**
- Pas d'erreurs 404 dans les logs Jellyfin du peer source
- Artwork affiché correctement en haute résolution
- **Critère de succès :** aucune erreur 404 image dans les journaux

#### TEST-19 — HTTPS derrière nginx (X-Forwarded-Proto)
**Contexte :** Jellyfin derrière un reverse proxy nginx avec TLS.
**À vérifier :**
- Les URLs générées dans le catalogue utilisent `https://` (pas `http://`)
- Vérifier dans `GET /JellyFed/v1/catalog` que `streamUrl`, `posterUrl`, `backdropUrl` commencent par `https://`
- **Critère de succès :** toutes les URLs en `https://`

---

## Bugs connus

### BUG-05 — Sous-titres SRT/ASS non affichés
**Symptôme :** Les sous-titres texte (SRT, SubRip, ASS) ne s'affichent pas dans le player Jellyfin côté client. Les sous-titres PGS (bitmap Blu-ray) sont eux brûlés dans la vidéo lors du transcodage (hard-sub, non désactivable).
**Cause probable :** Lors du transcodage HLS depuis une URL HTTP distante, Jellyfin/FFmpeg n'extrait pas les pistes texte en WebVTT séparées. Le mécanisme de soft-sub requiert que la piste soit extraite dans le manifest HLS, ce qui n'est pas automatique pour les sources HTTP distantes.
**Statut :** 🔴 À investiguer en priorité P1
**Piste :** Explorer les options FFmpeg pour l'extraction WebVTT (`-map 0:s:0 -c:s webvtt`) dans le profil de transcodage Jellyfin pour les sources HTTP.

### BUG-06 — PGS brûlés en hard-sub (non désactivable)
**Symptôme :** Sous-titres PGS toujours visibles, pas de sélection possible.
**Cause :** FFmpeg brûle les sous-titres bitmap dans la vidéo lors du transcodage — comportement attendu de Jellyfin pour les PGS. La sélection soft-sub PGS n'est pas supportée en transcodage HLS.
**Statut :** 🟡 Limitation connue, pas prioritaire

### BUG-01 — Bouton Remove Peer : carré blanc sans texte
**Fix :** `class="emby-button raised button-cancel"` + wrapper `<span>`.
**Statut :** ✅ Corrigé

### BUG-02 — Propagation en chaîne des titres (année dupliquée)
**Symptôme :** `Titre (2025) (2025) (2025)` après plusieurs hops de sync.
**Fix :** `QueryItems()` exclut les items dont le path commence par `LibraryPath`.
**Statut :** ✅ Corrigé

### BUG-03 — Peer supprimé conserve l'accès au catalogue
**Fix :** Tokens d'accès par peer — révocation immédiate à la suppression.
**Statut :** ✅ Corrigé

### BUG-04 — TypeLoadException SortOrder + MissingMethodException GetItemList (Jellyfin 10.11)
**Fix :** Migration vers `Jellyfin.Database.Implementations.Enums.SortOrder`, packages 10.11.8, .NET 9.
**Statut :** ✅ Corrigé

---

## Features à implémenter

### FEAT-07 — Gestion bibliothèques par peer
**Statut :** ✅ Implémenté sur la branche réconciliée `temp -> main`.

**Contrat actuel :**
- Layout par peer : `{MoviesRoot}/{PeerName}/…`, `{SeriesRoot}/{PeerName}/…`, `{AnimeRoot}/{PeerName}/…`
- Trois racines configurables (`MoviesRootPath`, `SeriesRootPath`, `AnimeRootPath`)
- Toggle `SyncAnime` par peer
- Suppression / purge d'un peer = suppression ciblée de son sous-arbre

**Conséquence sur le plan :**
- le choix d'architecture n'est plus « unified vs per-peer » ; le layout par peer est désormais la base réelle du plugin ;
- le travail restant avant v1 porte surtout sur la migration/compatibilité avec les installations antérieures qui utilisaient le layout plus ancien.

---

### FEAT-03 — Peer-of-peer discovery / gossip (P4)
**Contexte :** Si A est peer avec B et C, B et C ne se connaissent pas.
**Comportement :**
- Option `SharePeers` dans la config (désactivé par défaut)
- Lors de la sync, A envoie à B la liste de ses autres peers (C)
- B les ajoute en "pending" — l'admin approuve ou auto-approve si configuré
- Propagation limitée à 1 hop

**Endpoints :** `GET /JellyFed/v1/peers` → déjà disponible. `POST /JellyFed/v1/peer/register` → déjà disponible.

---

### FEAT-08 — Multi-source / IMediaSourceProvider (P5)
**Contexte :** Même film disponible chez plusieurs peers → proposer plusieurs sources au client.
- `sources.json` par item (stocké à côté du `.strm`)
- `FederationMediaSourceProvider.cs` implémente `IMediaSourceProvider`
- Tri des sources : qualité décroissante, peer le plus rapide en premier
- Le client Jellyfin voit toutes les sources et peut choisir

---

### FEAT-04 — Rapatriement de catalogue ("recall")
**Contexte :** A peut vouloir "rappeler" ses propres items depuis ses peers (utile après perte du disque).
- Endpoint ou bouton "Recall my content from peers"
- A interroge ses peers, identifie les `.strm` qui pointent vers A, et récupère les métadonnées

---

### FEAT-05 — Suppression propagée
**Contexte :** Si A supprime B, les `.strm` de B restent chez les peers de A.
- Signal `POST /JellyFed/v1/peer/leave` envoyé à B quand A supprime B
- B supprime les `.strm` de A de sa bibliothèque

---

### FEAT-06 — Tag peer dans les items Jellyfin (partiellement implémenté)
**Statut :** ✅ `GET /JellyFed/v1/manifest/stats` + `POST /JellyFed/v1/peer/purge` + section "Synced Catalogue" dans l'UI.
**Restant :** Tag `<studio>JellyFed:peer-b</studio>` dans le `.nfo` pour filtrage natif Jellyfin (optionnel).

---

## Améliorations techniques

### TECH-01 — Rechargement config à chaud
Config modifiée hors UI → plugin garde l'ancienne en mémoire jusqu'au redémarrage.

### TECH-02 — Dernier message d'erreur par peer dans l'UI
**Statut :** ✅ Implémenté.

`PeerStatus` stocke désormais `LastSyncStatus`, `LastSyncError`, `LastSyncDurationMs` et l'onglet Peers les affiche directement.

### TECH-03 — Tests d'intégration
Tester `FederationSyncTask` (mock `PeerClient`), `StrmWriter` (filesystem en mémoire), `FederationController` (WebApplicationFactory).
