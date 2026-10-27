import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// El Panel se compila dentro del wwwroot del Host, que lo sirve en :5000.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../src/CodeNetix.Host/wwwroot',
    emptyOutDir: true,
  },
})
