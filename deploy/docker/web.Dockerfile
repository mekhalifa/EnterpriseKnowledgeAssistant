# syntax=docker/dockerfile:1
#
# The Angular client behind nginx (Step 14, ADR-0034). Build context: the repository root.

# ── build ──────────────────────────────────────────────────────────────────────────────
FROM node:24.15.0-alpine AS build
WORKDIR /web

# Dependencies first, from the lock file exactly (npm ci), so this layer is reused until
# package-lock.json changes.
COPY src/EKA.Web/package.json src/EKA.Web/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY src/EKA.Web/ ./
# check:api fails the build if the generated API types are stale against the checked-in
# OpenAPI document (ADR-0033): the image never ships a client that disagrees with its API.
RUN npm run check:api && npm run build

# ── serve ──────────────────────────────────────────────────────────────────────────────
# The unprivileged variant: nginx runs as uid 101 and listens on 8080, not as root on 80.
FROM nginxinc/nginx-unprivileged:1.29.3-alpine AS web

COPY --from=build /web/dist/eka-web/browser /usr/share/nginx/html
# The development config.json must not ship: the container writes its own at start.
USER root
RUN rm -f /usr/share/nginx/html/config.json
USER 101

COPY deploy/web/default.conf.template /etc/nginx/templates/default.conf.template
# 0755 although it is sourced, not run: the entrypoint sources a *.envsh only if it is
# executable, and silently skips it otherwise ("Ignoring ..., not executable"). With 0644 the
# CSP's ${EKA_AUTH_ORIGIN} stayed unsubstituted and nginx refused to start (Step 14 drill).
COPY --chmod=0755 deploy/web/10-eka-config.envsh /docker-entrypoint.d/10-eka-config.envsh
COPY --chmod=0755 deploy/web/40-runtime-config.sh /docker-entrypoint.d/40-runtime-config.sh

# The template step substitutes only EKA_* variables, never a stray environment variable that
# happens to share a name with one of nginx's own ($host, $uri...).
ENV NGINX_ENVSUBST_FILTER=^EKA_

EXPOSE 8080
