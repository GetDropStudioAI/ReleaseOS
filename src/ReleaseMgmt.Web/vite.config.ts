import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'node:path'
import http from 'node:http'

const api = 'http://127.0.0.1:6080'   // explicit IPv4: on Windows 'localhost' can resolve to ::1 first
const control = 'http://127.0.0.1:5099' // start.py supervisor (Reset / Exit); absent when the app is run another way
// Reuse connections to the backend. The proxy (http-proxy) otherwise opens a new TCP connection for every proxied request and closes it,
// leaving one TIME_WAIT socket each: three e2e specs left about 1,300, and a full run on Windows ran out of local ports
// (ERR_NO_BUFFER_SPACE in CI runs 93 and 105).
const backend = { target: api, agent: new http.Agent({ keepAlive: true, maxSockets: 64 }) }

export default defineConfig({
  plugins: [react()],
  build: { outDir: '../ReleaseMgmt.Api/wwwroot', emptyOutDir: true },
  server: {
    // Loopback only, IPv4, on purpose: 'localhost' binds ::1 alone on Windows (so http://127.0.0.1 was refused), and 0.0.0.0 would expose the dev login to the network.
    host: '127.0.0.1',
    port: 6273,
    fs: { allow: [path.resolve(__dirname, '../..')] }, // docs/ui/tokens.css lives outside this project
    proxy: { '/api': backend, '/hub': { target: api, ws: true }, '/auth': backend, '/healthz': backend, '/control': control },
  },
})
