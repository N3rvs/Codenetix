import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// El Panel se compila dentro del wwwroot del Host, que lo sirve en :5000.
// En desarrollo (`npm run dev`, :5173) Vite reenvía la API y el hub al Host.
const hostUrl = 'http://localhost:5000'

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': hostUrl,
      '/openapi': hostUrl,
      '/hubs': { target: hostUrl, ws: true },
    },
  },
  build: {
    outDir: '../src/CodeNetix.Host/wwwroot',
    emptyOutDir: true,
  },
})
