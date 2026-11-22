# eSkrb dashboard (web)

Vue 3 + TypeScript + Pinia + Vue Router, built with Vite. The staff dashboard for the eSkrb backend; project overview, secrets and conventions are in the repo root (`README.md`, `CLAUDE.md`).

```bash
npm install
npm run dev          # http://localhost:5173, proxies /api/* to the API (API_URL, default http://localhost:5000)
npm run type-check
npm run lint
npm run format
npm run build
```

Requires Node `^22.18 || >=24.12`. Sign in by pasting an access token (see `reseed.sh` in the repo root for the seeded demo tokens).
