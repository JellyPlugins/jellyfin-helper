# Minimal Node image for the Arr/Seerr/Trakt mock servers.
# No dependencies - the mocks use only Node's built-in http module.
FROM node:26.12.1-alpine
WORKDIR /app
COPY arr-server.js seerr-server.js trakt-server.js ./
# Drop root: the mocks are pure Node stdlib and need no privileged access.
USER node
# Default command is overridden per-service in compose.yml.
CMD ["node", "arr-server.js"]
