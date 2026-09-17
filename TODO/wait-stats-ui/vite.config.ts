import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  // Relative base so asset paths work when served from any localhost port
  base: './',
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      // Dev-server only: proxy /api to the standalone API process
      '/api': {
        target: 'http://localhost:5236',
        changeOrigin: true,
      },
    },
  },
  preview: {
    port: 4173,
  },
})
