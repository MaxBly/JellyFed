# JellyFed — Scope v1

## Objectif

La v1.0.0 doit figer les contrats que les utilisateurs ne doivent pas avoir à réinitialiser à chaque update :

- layout disque ;
- format `.strm` / `.nfo` ;
- manifest ;
- configuration ;
- API `/JellyFed/*` ;
- comportement de sync et pruning.

---

## Contrats v1

| Contrat | État cible |
|---|---|
| Layout disque | `{Root}/{Title} ({Year}) [tmdbid-X]/... [peer-Y].strm` |
| Multi-source | Versions natives Jellyfin via plusieurs `.strm` |
| Manifest | `.jellyfed-manifest.json`, `schemaVersion = 2` |
| Sidecar | Aucun `sources.json` |
| Provider custom | Aucun `FederationMediaSourceProvider` |
| Stream URL | Générée depuis `SelfUrl`, fallback URL requête |
| Pruning | Suppression par fichiers `[peer-X]`, dossier retiré seulement si dernière source |
| Auth | `AccessToken` per-peer puis fallback `FederationToken` |
| Audit | SQLite local `.jellyfed-audit.sqlite3` |

---

## Ce qui est livré

- API unifiée `/JellyFed`.
- Version/protocole/schéma exposés.
- InstanceId stable.
- Discovery v1 manual-add only.
- Audit persistant.
- Layout aplati côté code.
- Suppression du provider multi-source custom.
- `SelfUrl` utilisé pour les URLs catalogue.
- Champs metadata enrichis : runtime, bitrate, taille, HDR/range, édition, media streams complets.

---

## Ce qui reste avant v1

- Validation e2e du layout aplati sur plusieurs instances.
- Confirmation du comportement MergeVersions natif Jellyfin pour `.strm`.
- Fix ou décision documentée pour `BUG-05`.
- Tests de purge/remove/rename peer avec manifest v2.
- Release notes et bump version.

---

## Migration pré-v1

Le passage manifest v1 → v2 n'est pas migré silencieusement si le manifest contient des entrées.

Raison : le layout disque change radicalement :

```text
Avant: {Root}/{PeerName}/{Title}/Title.strm
Après: {Root}/{Title} [tmdbid-X]/Title [peer-Y].strm
```

Pour les builds pré-v1, la stratégie retenue est explicite : reset/purge de l'ancienne bibliothèque fédérée avant de régénérer le layout v2.

Après v1, toute évolution de schéma devra fournir une migration réelle ou refuser de démarrer avec un message clair.

---

## Critères v1.0.0

- [x] Plugin .NET 9 compatible Jellyfin 10.11.x.
- [x] API `/JellyFed/*` unifiée.
- [x] Auth per-peer + fallback token global.
- [x] Discovery v1 manual-add.
- [x] Audit SQLite admin-only.
- [x] Manifest `schemaVersion = 2`.
- [x] Layout aplati par item.
- [x] Multi-source via `.strm` multiples.
- [x] `SelfUrl` utilisé pour les URLs exposées.
- [ ] Tests e2e multi-peer.
- [ ] Validation MergeVersions `.strm`.
- [ ] Validation local + peer.
- [ ] BUG-05 traité.
- [ ] Documentation finale relue contre le code.
- [ ] `build.yaml` et repo manifest bumpés pour release.

---

## Hors scope v1

- Dépendance à un client externe spécifique.
- Gossip récursif automatique.
- Recall de contenu.
- Suppression propagée entre peers.
- Auto-configuration des bibliothèques Jellyfin.
