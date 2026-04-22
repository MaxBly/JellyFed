# Dépôt JellyFed sur le réseau local (développement)

Ce guide explique comment **servir le dossier `repo/` sur votre LAN** (fichiers `*.zip` + manifeste Jellyfin) via **Docker**, sans modifier `scripts/build-repo.sh` ni le flux de déploiement VPS existant.

## À quoi ça sert

- Après `./scripts/build-repo.sh`, vous avez un ZIP du plugin et un `repo/manifest.json` dont les `sourceUrl` pointent vers le dépôt public (`jellyfed.bly-net.com`).
- Pour tester l’installation du plugin depuis **une autre machine Jellyfin sur le même réseau**, il faut un manifeste dont les `sourceUrl` pointent vers **votre machine** (HTTP sur le LAN).
- Le script `scripts/generate-manifest-local.py` produit **`repo/manifest.local.json`** (fichier non versionné) : mêmes entrées que `manifest.json`, mais avec des `sourceUrl` locales et des **libellés « dev »** dans le catalogue Jellyfin.
- **Nginx dans Docker** expose le dossier `repo/` en lecture seule sur un port (par défaut **8765**).

## Quelle version est servie en local ?

Le **fichier ZIP** servi est toujours celui produit par **`./scripts/build-repo.sh`** (nom du type `jellyfed_<version>.zip` selon **`version`** dans [`build.yaml`](build.yaml)) — le binaire à l’intérieur correspond à ce build.

Dans **`manifest.local.json`**, pour le catalogue Jellyfin :

- La **première entrée** de `versions` (la plus récente) a son champ **`version`** forcé à **`1.3.3.7`**, afin de distinguer clairement le dépôt LAN des releases du dépôt public (même GUID, numéros différents).
- Un **lien symbolique** `repo/jellyfed_1.3.3.7.zip` → le ZIP réel du build (ex. `jellyfed_0.1.0.14.zip`) est créé si possible : le `sourceUrl` de cette entrée pointe alors vers `…/jellyfed_1.3.3.7.zip` (même contenu que le build, nom cohérent avec la version catalogue).
- Les entrées plus anciennes du tableau `versions`, si présentes, **conservent** leurs numéros d’origine.
- **`overview`** : préfixe `[Dépôt LAN · dev]`
- **`changelog`** de l’entrée **la plus récente** : texte du type  
  `[Build locale] JellyFed 1.3.3.7 — binaire issu du ZIP jellyfed_<x.y.z>.zip (build-repo.sh / build.yaml).`  
  où `<x.y.z>` est la **version du build** lue depuis le nom du ZIP (plus d’ancien « Release 0.1.0.0 » recopié tel quel).
- **Entrées `versions` plus anciennes** : changelog inchangé par rapport au manifest source, avec préfixe `[Build locale]` si besoin.

## Conflits si JellyFed est déjà dans un autre dépôt ?

- Jellyfin identifie un plugin par son **`guid`** (dans `build.yaml`). Il n’y a **qu’une installation** de JellyFed par serveur pour ce GUID.
- Avoir **plusieurs dépôts** qui listent le même plugin (même GUID) **ne duplique pas** l’installation : vous voyez une entrée dans le catalogue, et les mises à jour peuvent provenir de **l’un ou l’autre** dépôt selon ce que Jellyfin propose / résout.
- **Avantage du dépôt LAN** : l’entrée catalogue la plus récente est en **`1.3.3.7`**, donc en général **plus de collision de numéro** avec le dépôt public (`0.1.0.x`) pour la même ligne « JellyFed ».
- **Risque résiduel** : deux ZIP différents pour **le même numéro** affiché restent possibles si vous bricolez les manifestes ; en pratique, gardez un seul dépôt actif si vous voyez un comportement bizarre de mise à jour.

Il n’y a pas de « conflit » au sens installation cassée automatiquement, mais **évitez deux sources concurrentes pour la même version** si vous comparez des binaires différents.

## Prérequis

- [Docker](https://docs.docker.com/get-docker/) avec la commande `docker compose` (plugin Compose v2).
- [.NET 9 SDK](https://dotnet.microsoft.com/download) pour compiler si vous utilisez le script de build officiel.
- Python 3 (déjà utilisé ailleurs dans le dépôt, ex. `build-repo.sh`).

## Étapes

### 1. Tout-en-un : build + dépôt LAN

À la racine du dépôt :

```bash
make dev
```

Enchaîne dans l’ordre :

1. **`./scripts/build-repo.sh`** — compilation Release, ZIP `repo/jellyfed_<version>.zip`, mise à jour de `repo/manifest.json` (URLs de production dans ce fichier, c’est normal).
2. **`./scripts/dev-repo-up.sh --restart`** — génère `manifest.local.json`, arrête le conteneur Nginx s’il tourne, affiche l’URL Jellyfin, puis `docker compose up` (logs en avant-plan).

À chaque `make dev`, le **ZIP est régénéré** et le manifeste local reflète le build courant.

### 2. Sans Make (build puis serveur)

```bash
./scripts/build-repo.sh
chmod +x scripts/dev-repo-up.sh scripts/detect_lan_ip.py  # une seule fois si besoin
./scripts/dev-repo-up.sh --restart   # avec arrêt préalable du conteneur
./scripts/dev-repo-up.sh             # sans arrêt forcé (échoue si le port est déjà pris)
```

Le script :

1. **Détecte l’IPv4 LAN** de la machine courante (`scripts/detect_lan_ip.py` : route par défaut, puis repli macOS / `hostname -I` sous Linux).
2. Génère **`repo/manifest.local.json`** avec des `sourceUrl` basés sur `http://<IP>:<port>/` et les libellés **dev** (overview / changelog).
3. Affiche **immédiatement** l’URL à coller dans Jellyfin (Plugins → Repositories).
4. Lance **`docker compose up`** en **premier plan** : les **logs Nginx** (accès / erreurs) s’affichent dans la même console jusqu’à **Ctrl+C**.

Le port hôte est **`8765`** par défaut, publié sur **`0.0.0.0`** pour être joignable depuis tout le réseau local. Pour un autre port :

```bash
export JELLYFED_DEV_REPO_PORT=9080
./scripts/dev-repo-up.sh
```

**Forcer une URL de base** (ex. Jellyfin en conteneur qui doit joindre `host.docker.internal` ou `172.17.0.1`) :

```bash
./scripts/dev-repo-up.sh "http://172.17.0.1:8765"
```

### 3. Variante manuelle (sans le script de lancement)

Générer le manifeste seul :

```bash
python3 scripts/generate-manifest-local.py --base-url "http://192.168.1.42:8765"
```

Puis, depuis la racine du dépôt, Nginx en arrière-plan :

```bash
docker compose -f docker/dev-repo/docker-compose.yml up -d
```

Ou en avant-plan pour voir les logs :

```bash
docker compose -f docker/dev-repo/docker-compose.yml up
```

Si un ZIP référencé dans `manifest.json` est absent de `repo/`, `generate-manifest-local.py` affiche un **avertissement** : relancez `./scripts/build-repo.sh`.

## Configurer Jellyfin

1. Dashboard → **Plugins** → **Repositories** → **Add** (ou équivalent).
2. Coller l’URL du manifeste local :  
   `http://<ip-joignable>:<port>/manifest.local.json`
3. Rafraîchir le catalogue des plugins et installer / mettre à jour **JellyFed** depuis ce dépôt.

### HTTP sur le LAN

Ce mode sert du contenu en **HTTP** non chiffré, acceptable sur un réseau de confiance pour du dev. Jellyfin accepte en général les dépôts HTTP pour des URL locales ; si votre instance refuse les dépôts non HTTPS, testez depuis un client sur le même LAN ou ajustez la politique de sécurité de votre instance.

## Arrêter le serveur

```bash
docker compose -f docker/dev-repo/docker-compose.yml down
```

## Fichiers concernés

| Fichier | Rôle |
|---------|------|
| `docker/dev-repo/docker-compose.yml` | Service `nginx:alpine`, montage du dossier `repo/`. |
| `docker/dev-repo/nginx.conf` | Config Nginx (listing de répertoire activé pour repérer vite les ZIP). |
| `scripts/generate-manifest-local.py` | Crée `repo/manifest.local.json` (URLs LAN, overview dev, changelog aligné 1.3.3.7 + ZIP de build). |
| `scripts/detect_lan_ip.py` | Détection de l’IPv4 LAN pour construire l’URL du dépôt. |
| `scripts/dev-repo-up.sh` | Manifest local + `docker compose up` (premier plan, logs console). |
| `repo/manifest.local.json` | Généré, **non versionné** (voir `.gitignore`). |

## Dépannage

- **Détection d’IP impossible** : sans route réseau vers Internet, le script peut échouer. Lancez `./scripts/dev-repo-up.sh "http://<ip>:8765"` en passant l’URL à la main (voir aussi Jellyfin dans Docker : `host.docker.internal`, `172.17.0.1`, etc.).
- **404 sur le manifeste** (`…/manifest.local.json`) : Jellyfin ne trouve pas le fichier sur le serveur HTTP. Causes fréquentes :
  - le fichier **`repo/manifest.local.json` n’existe pas** sur la machine où tourne Docker (il n’est **pas** versionné dans Git : il faut lancer `make dev` ou `generate-manifest-local.py` **sur cette machine** avant / en même temps que Nginx) ;
  - le volume Docker ne pointait pas vers le bon dossier : depuis la racine du dépôt, préférez **`make dev`** (montage absolu via `JELLYFED_REPO_DIR`) plutôt qu’un `docker compose up` manuel sans variable d’environnement ;
  - après `make dev`, vérifiez dans la console la liste **`Fichiers servis par Nginx`** : `manifest.local.json` doit apparaître ; testez dans un navigateur ou avec `curl -fI "http://<ip>:8765/manifest.local.json"` depuis une autre machine du LAN.
- **Installation du plugin : `HttpRequestException` 404** : Jellyfin télécharge le ZIP via l’URL exacte du champ **`sourceUrl`** du manifeste (requête HTTP `GET`). Un **404** signifie presque toujours que **Nginx a répondu « fichier absent »** : le nom dans l’URL ne correspond pas à un fichier sous `repo/` (ou le serveur `make dev` n’est pas lancé). Vérifications :
  1. `make dev` / `docker compose` du dépôt LAN **tourne** pendant l’installation.
  2. Après **`./scripts/build-repo.sh`**, relancez **`make dev`** pour régénérer `manifest.local.json` et le lien `jellyfed_1.3.3.7.zip`.
  3. Sur la **même machine que le processus Jellyfin** (ou depuis le conteneur Jellyfin si Docker) :  
     `curl -fIL "http://<hôte>:8765/jellyfed_1.3.3.7.zip"`  
     (ou l’URL exacte indiquée dans `repo/manifest.local.json` pour `sourceUrl` de la version `1.3.3.7`). Si `curl` échoue, Jellyfin échouera pareil.
  4. Jellyfin **dans Docker** : l’IP ou le hostname dans le manifeste doit être **joignable depuis ce conteneur** (souvent IP LAN de l’hôte, `172.17.0.1`, ou `host.docker.internal` selon l’OS / la config).
- **Échec du téléchargement du ZIP dans Jellyfin** : le `sourceUrl` doit utiliser une IP/hostname que **le processus Jellyfin** peut résoudre et joindre. Depuis la machine Jellyfin : `curl -I http://.../jellyfed_x.y.z.zip`.
- **Firewall** : autorisez le port choisi (8765 par défaut) en entrée sur la machine qui exécute Docker.
- **Après un nouveau build** : relancez `./scripts/build-repo.sh`, puis regénérez `manifest.local.json` et redémarrez ou rafraîchissez le cache plugins côté Jellyfin si besoin.

## Relation avec `build-repo.sh` et le VPS

- **`build-repo.sh`** : inchangé ; continue de produire le ZIP, de mettre à jour `manifest.json` et optionnellement de déployer sur le VPS avec `--deploy`.
- **Docker dev-repo** : uniquement pour exposer **`repo/`** en local avec un manifeste aux **`sourceUrl`** adaptés (`manifest.local.json`).

Les deux peuvent coexister : le manifeste versionné reste orienté production ; le flux LAN utilise `manifest.local.json` + conteneur Nginx.
