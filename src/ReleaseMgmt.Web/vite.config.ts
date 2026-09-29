import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'node:path'

const api = 'http://localhost:5080'

export default defineConfig({
  plugins: [react()],
  build: { outDir: '../ReleaseMgmt.Api/wwwroot', emptyOutDir: true },
  server: {
    port: 5173,
    fs: { allow: [path.resolve(__dirname, '../..')] }, // docs/ui/tokens.css lives outside this project
    proxy: { '/api': api, '/hub': { target: api, ws: true }, '/auth': api, '/healthz': api },
  },
})
