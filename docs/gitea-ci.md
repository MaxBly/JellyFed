# JellyFed — CI Gitea latest release

Ce dépôt contient un workflow Gitea Actions :

```text
.gitea/workflows/jellyfed-latest-release.yml
```

Il build le plugin JellyFed, génère un ZIP et un manifest Jellyfin, puis publie les deux comme assets d'une release Gitea flottante taggée `latest` via `gitea-release-action`.

## URL Jellyfin stable

Dans Jellyfin → Dashboard → Plugins → Repositories, utilisez :

```text
https://<gitea>/<owner>/<repo>/releases/download/latest/manifest.json
```

Exemple :

```text
https://gitea.home.example/qcormand/JellyFed/releases/download/latest/manifest.json
```

Le manifest pointe ensuite vers le ZIP publié dans la même release `latest`.

## Secret requis

Créez dans Gitea un secret de dépôt :

```text
GITEA_TOKEN
```

Ce token doit pouvoir créer/mettre à jour les releases et leurs assets sur le dépôt.

## Version générée

Le workflow lit `build.yaml`, puis génère une version Jellyfin à 4 composants :

```text
<major>.<minor>.<patch>.<timestamp>
```

Exemple avec `build.yaml` en `0.1.0.18` :

```text
0.1.0.1780850000
```

Cela garantit que Jellyfin voit chaque build CI comme une upgrade.

## Assets publiés

À chaque build, la release `latest` est mise à jour avec ces assets :

```text
manifest.json
jellyfed_<version>.zip
```

## Déclenchement

Le workflow se déclenche sur :

- push vers `main`, `master`, `develop` ;
- push vers `feature/**` ;
- lancement manuel via `workflow_dispatch`.

## Remarque sur le tag `latest`

Le tag/release `latest` sert surtout de point d'accès stable pour Jellyfin. Cela évite de changer l'URL du dépôt dans Jellyfin entre deux builds.
