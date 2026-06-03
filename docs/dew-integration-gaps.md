# JellyFed — manques pour l'intégration native (client dew)

> Identifié 2026-05-23 en testant l'intégration **dew ↔ JellyFed** multi-source sur l'env de test (3 instances).
> **Source de vérité complète** (contexte, contrats, env de test, travaux côté dew) : **`~/dew/docs/jellyfed-integration.md`**.
> Ce fichier = checklist actionnable côté JellyFed. Travail en cours depuis **2026-06-01** (handoff : `docs/handoff-2026-06-01.md`).

## Contexte

dew lit un Jellyfin qui héberge la bibliothèque JellyFed (`.strm` + `IMediaSourceProvider`). La chaîne multi-source est **validée bout-en-bout** : quand un TMDB est possédé par ≥2 peers, `PlaybackInfo` renvoie plusieurs `MediaSources`, et dew les affiche (sélecteur de versions #198). Restent les manques ci-dessous pour que l'expérience soit propre et jouable (dans dew **et** tout client Jellyfin natif).

## Checklist (priorité décroissante)

- [x] **A1 🔴 Lecture des sources alternatives (transcodage)** — décision archi prise : **aplatissement du layout** (1 dossier par item, fichiers de tous les peers dans le même dossier avec suffixe `[peer-X]`). Auto-merge JF natif des multi-versions remplace `FederationMediaSourceProvider`. **À implémenter** — voir `docs/handoff-2026-06-01.md` §3.
- [x] **A2 🟠 Durée par source** — **livré**. `ManifestSource.RuntimeTicks`, `BuildSource` calcule depuis `CatalogItemDto.RuntimeMinutes`, `BuildEpisodeSources` idem. Provider utilise `source.RuntimeTicks ?? item.RunTimeTicks`. cf. commit handoff.
- [x] **A3 🟠 MediaStreams par source complets** — **livré**. `MediaStreamInfoDto` enrichi : `Index, Channels, BitRate, Width, Height, SampleRate, VideoRange`. Le stream `Video` est aussi exposé (auparavant absorbé en codec/width/height seulement). `ExtractStreamInfo` peuple depuis JF MediaStream natif. `ToMediaStream` mappe fidèlement (vrais index).
- [x] **A4 🟡 Fusion « local + peer »** — **résolu par A1** (aplatissement). Le skip ligne 286 saute automatiquement avec le nouveau modèle ; la bibliothèque JF scanne `/srv/media/` + `/jellyfed-library/` comme une seule racine et l'auto-merge fusionne local+peers. Pas de sidecar local. **Ne pas implémenter séparément.**
- [ ] **A5 🟡 Joignabilité des `streamUrl`** — basées sur `SelfUrl` du peer ; figer le contrat LAN vs public (le ffmpeg du consommateur doit joindre l'URL). Pas attaqué cette session, à faire après A1.
- [x] **A6 🟢 Métadonnées de source** — **livré**. `ManifestSource` + `CatalogItemDto` + `EpisodeDto` portent `BitRate, SizeBytes, VideoRange, Edition` (édition extraite du filename `[edition-XXX]`). `MediaSourceInfo.Bitrate`/`Size` propagés au provider.

## Note

Un fix JellyFed a été appliqué le 2026-05-23 (dans ce repo) : le provider **n'émet plus la source du peer primaire** (déjà couverte par le `.strm` natif) → `PlaybackInfo` sans doublon. `build.yaml` est resté en `0.1.0.17` (dll rebuildée et réinstallée à la main dans les instances de test) — **bumper la version** au prochain vrai build/release.

Avec l'aplatissement (A1), le `FederationMediaSourceProvider` et le sidecar `sources.json` deviennent **inutiles** : la fusion est assurée par MergeVersions natif JF, pas par un provider custom. Prévoir leur suppression dans le commit qui livre A1.
