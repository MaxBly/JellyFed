# JellyFed — cibles de développement
.PHONY: dev

# Répertoire du Makefile = racine du dépôt (même si make est invoqué avec -f).
_MAKEFILE_DIR := $(dir $(abspath $(lastword $(MAKEFILE_LIST))))

# Compile (build-repo.sh), ZIP + manifest.json, puis dépôt LAN (manifest.local + Docker).
# Si le conteneur tourne déjà, il est arrêté puis relancé pour servir le repo/ à jour.
dev:
	@export JELLYFED_REPO_DIR="$(_MAKEFILE_DIR)repo" && \
	  ./scripts/build-repo.sh && \
	  ./scripts/dev-repo-up.sh --restart
