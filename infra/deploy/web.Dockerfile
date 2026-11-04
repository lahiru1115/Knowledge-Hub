# Caddy with the built web app inside, for the same-origin fallback (Caddyfile.same-origin).
# Build from the repository root: docker build -f infra/deploy/web.Dockerfile .
FROM node:22-alpine AS build
WORKDIR /web
COPY apps/web/package.json apps/web/package-lock.json ./
RUN npm ci --ignore-scripts
COPY apps/web ./
COPY contracts /contracts
RUN npm run build

FROM caddy:2
COPY --from=build /web/dist /srv/web
