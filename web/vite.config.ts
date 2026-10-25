import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({
    plugins: [react(), tailwindcss()],
    server: {
        port: 5180,
        proxy: {
            // The panel API (dotnet run in src/TurboManager.Panel, or the Docker dev env). The slash keeps the
            // /api-docs page in the app; ws carries the terminal's WebSocket.
            '/api/': { target: process.env.TS_PANEL_URL ?? 'http://127.0.0.1:7800', changeOrigin: false, ws: true },
        },
    },
    build: {
        outDir: 'dist',
        emptyOutDir: true,
    },
});
