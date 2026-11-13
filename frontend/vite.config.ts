import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', '');
  return {
    plugins: [react()],
    server: {
      port: 5173,
      strictPort: true,
      proxy: {
        '/api': {
          target: env.API_PROXY_TARGET || (mode === 'demo' ? 'http://127.0.0.1:5189' : 'http://localhost:5160'),
          changeOrigin: true,
        },
      },
    },
  };
});
