FROM node:24-bookworm-slim

ENV NODE_ENV=production
WORKDIR /app

COPY package.json ./
COPY server.js ./
COPY public ./public

EXPOSE 3000
CMD ["node", "server.js"]
