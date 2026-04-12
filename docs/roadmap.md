# JellyFed — Roadmap

## Phase 0 — Scaffolding (1-2h)

**Objectif :** plugin qui se charge dans Jellyfin sans erreur.

- [ ] Cloner `jellyfin/jellyfin-plugin-template`
- [ ] Renommer → `JellyFed`, mettre à jour `build.yaml`, `GUID`
- [ ] `Plugin.cs` minimal : nom, version, description
- [ ] `PluginConfiguration.cs` : `Peers[]`, `SyncIntervalHours`, `LibraryPath`
- [ ] Page config dans le panneau admin (razor page ou JSON schema)
- [ ] `dotnet build` → `.dll` → charger dans Jellyfin local → apparaît dans Plugins

**Vérification :** plugin visible dans Administration → Plugins de Jellyfin.

---

## Phase 1 — API de fédération (2-3h)

**Objectif :** `/JellyFed/catalog` répond avec le catalogue réel.

- [ ] `FederationController.cs` — endpoint `GET /JellyFed/catalog`
  - Requête `ILibraryManager` → films + séries de la bibliothèque locale
  - Sérialiser en `CatalogDto` (voir `docs/api.md`)
- [ ] `GET /JellyFed/health` — heartbeat
- [ ] Authentification : `federation_token` dans la config → `IAuthorizationContext` ou middleware custom
- [ ] `PeerClient.cs` — client HTTP pour requêter `/JellyFed/catalog` sur un peer distant

**Vérification :** `curl https://mon-jellyfin/JellyFed/catalog?api_key=fed_token` → JSON catalogue.

---

## Phase 2 — Sync + génération `.strm` (3-4h)

**Objectif :** `dew catalog sync` → fichiers `.strm` créés, bibliothèque Jellyfin mise à jour.

- [ ] `FederationSyncTask.cs` — `IScheduledTask`
  - Pour chaque peer activé : `PeerClient.GetCatalog()`
  - `Deduplicator.cs` : dédup par TMDB ID + `{titre, année}` fallback
  - `StrmWriter.cs` : écrire `.strm` + `.nfo` + télécharger artwork
  - `ILibraryManager.ValidateMediaLibraryAsync()` après sync
- [ ] Gestion des suppressions (items disparus du peer)
- [ ] Delta sync via `?since=` pour les syncs suivantes
- [ ] Logs détaillés : N films ajoutés, N séries ajoutées, N supprimés, N skippés (dédup)

**Vérification :** configurer un peer → déclencher sync → fichiers `.strm` dans `/jellyfed-library/` → films du peer visibles dans Jellyfin.

---

## Phase 3 — Gestion des peers (2h)

**Objectif :** workflow complet d'ajout d'un peer via l'interface admin.

- [ ] `POST /JellyFed/peer/register` — demande de connexion
- [ ] `GET /JellyFed/peers` — liste des peers avec statut online/offline
- [ ] `POST /JellyFed/peer/sync` — sync manuelle
- [ ] Interface admin : liste des peers + bouton sync + statut dernière sync
- [ ] Heartbeat périodique : GET `/JellyFed/health` sur chaque peer → marquer online/offline

**Vérification :** ajouter peer-b depuis l'UI → approuver chez peer-b → sync démarre → catalogue apparaît.

---

## Phase 4 — Multi-source + `IMediaSourceProvider` (2h)

**Objectif :** même film disponible chez plusieurs peers → deux sources proposées au client.

- [ ] `sources.json` par item (stocké à côté du `.strm`)
- [ ] `FederationMediaSourceProvider.cs` — `IMediaSourceProvider`
  - Détecte les items JellyFed (tag dans les métadonnées)
  - Retourne toutes les sources disponibles avec leur qualité
- [ ] Tri des sources : qualité décroissante, peer préféré en premier

**Vérification :** film présent chez peer-b (1080p) et peer-c (4K) → client Jellyfin voit deux sources.

---

## Phase 5 — Gossip protocol (1-2h)

**Objectif :** découverte automatique de peers via les peers déjà connectés.

- [ ] `GossipService.cs` : périodiquement GET `/JellyFed/peers` sur les peers actifs
- [ ] Nouveaux peers découverts → ajoutés en mode "pending" (admin doit approuver)
- [ ] Limitation : propagation max 2 hops, rate limiting
- [ ] UI : section "Peers découverts" dans le panneau admin

---

## Phase 6 — Distribution publique

- [ ] Packaging : `manifest.json` pour le dépôt de plugins Jellyfin
- [ ] Releases GitHub avec binaires versionnés
- [ ] Documentation d'installation utilisateur
- [ ] Tests d'intégration (mock peer HTTP)

---

## Stack technique

| Composant | Technologie |
|-----------|-------------|
| Plugin | C# / .NET 8 |
| Framework plugin | `Jellyfin.Controller` (NuGet) |
| HTTP client (peers) | `HttpClient` natif .NET |
| Sérialisation JSON | `System.Text.Json` |
| Tests | xUnit |
| CI | GitHub Actions |

**Références :**
- Template officiel : https://github.com/jellyfin/jellyfin-plugin-template
- SDK Jellyfin : https://www.nuget.org/packages/Jellyfin.Controller
- Plugins existants : https://github.com/jellyfin/jellyfin-plugin-reports (exemple simple)

---

## État actuel

| Phase | Statut |
|-------|--------|
| 0 — Scaffolding | 🔜 à démarrer |
| 1 — API catalogue | 🔜 |
| 2 — Sync + .strm | 🔜 |
| 3 — Gestion peers | 🔜 |
| 4 — Multi-source | 🔜 |
| 5 — Gossip | 🔜 |
| 6 — Distribution | 🔜 |
