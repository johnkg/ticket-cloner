import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

/**
 * Where the app is mounted, with a trailing slash.
 *
 * "/" is the ordinary case - its own IIS site, or the dev server. Set APP_BASE
 * when it is an APPLICATION INSIDE another site, e.g. Default Web Site with an
 * alias of ticket-cloner, which serves it at /ticket-cloner/.
 *
 * Vite bakes this into index.html's asset paths AND into import.meta.env.BASE_URL,
 * which is what api.ts prefixes its calls with. Getting it wrong is not subtle:
 * index.html asks the SERVER ROOT for its JavaScript, IIS answers with an HTML
 * error page, and the browser refuses it on MIME type. The page is simply blank.
 *
 * It is baked at BUILD time, so a build made for one mount point cannot be
 * dropped at another.
 */
const base = process.env.APP_BASE ?? '/'

// https://vite.dev/config/
export default defineConfig({
  base,
  plugins: [react()],
  server: {
    // 5174, not Vite's default 5173, so CompanionTool's client can run at the
    // same time. strictPort makes a clash fail loudly instead of silently
    // moving to another port the API proxy knows nothing about.
    port: 5174,
    strictPort: true,
    proxy: {
      // Same-origin in production (the SPA is served from wwwroot), so the dev
      // server has to stand in for that. Target must match the API's
      // launchSettings applicationUrl.
      '/api': {
        target: 'http://localhost:5002',
        changeOrigin: false,
      },
    },
  },
  build: {
    // Published into the API's wwwroot by the BuildSpa target in the csproj.
    outDir: 'dist',
    emptyOutDir: true,
  },
})
