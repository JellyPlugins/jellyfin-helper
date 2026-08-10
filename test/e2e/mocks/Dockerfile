# Minimal Node image for the Arr/Seerr mock servers.
# No dependencies - the mocks use only Node's built-in http module.
FROM node:22-alpine
WORKDIR /app
COPY arr-server.js seerr-server.js ./
# Default command is overridden per-service in compose.yml.
CMD ["node", "arr-server.js"]
