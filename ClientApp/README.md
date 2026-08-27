# ClientApp

The TicketCloner front end: React + TypeScript, built with Vite.

Everything worth knowing — how to run it, which ports, how it talks to the API —
is in the [root README](../README.md). This directory holds no independent
setup.

```bash
npm install
npm run dev
```

The dev server proxies `/api` to the backend on port 5002; they only share an
origin after `dotnet publish`, which builds this into the API's `wwwroot`.
