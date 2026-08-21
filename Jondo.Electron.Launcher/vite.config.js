import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';

export default defineConfig({
  base: './',
  publicDir: path.resolve(import.meta.dirname, '../launcher_assets'),
  plugins: [react()],
  build: { outDir: 'build/renderer', emptyOutDir: true }
});
