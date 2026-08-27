import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // 5174, not Vite's default 5173, so ReleaseTool's client can run at the
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
