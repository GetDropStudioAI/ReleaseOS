import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'node:path'

const api = 'http://127.0.0.1:6080'   // explicit IPv4: on Windows 'localhost' can resolve to ::1 first
const control = 'http://127.0.0.1:5099' // start.py supervisor (Reset / Exit); absent when the app is run another way

export default defineConfig({
  plugins: [react()],
  build: { outDir: '../ReleaseMgmt.Api/wwwroot', emptyOutDir: true },
  server: {
    // Loopback only, IPv4, on purpose: 'localhost' binds ::1 alone on Windows (so http://127.0.0.1 was refused), and 0.0.0.0 would expose the dev login to the network.
    host: '127.0.0.1',
    port: 6273,
    fs: { allow: [path.resolve(__dirname, '../..')] }, // docs/ui/tokens.css lives outside this project
    proxy: { '/api': api, '/hub': { target: api, ws: true }, '/auth': api, '/healthz': api, '/control': control },
  },
})
