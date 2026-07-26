import js from '@eslint/js';
import { defineConfig } from 'eslint/config';
import { createTypeScriptImportResolver } from 'eslint-import-resolver-typescript';
import { importX } from 'eslint-plugin-import-x';
import globals from 'globals';
import { configs as tseslintConfigs } from 'typescript-eslint';

export default defineConfig(
  {
    ignores: ['.vite/**', 'out/**'],
  },
  {
    files: ['**/*.{js,cjs,mjs,jsx,ts,cts,mts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslintConfigs.recommended,
      importX.flatConfigs.recommended,
      importX.flatConfigs.electron,
      importX.flatConfigs.typescript,
    ],
    languageOptions: {
      globals: {
        ...globals.browser,
        ...globals.node,
      },
    },
    settings: {
      'import-x/resolver-next': [
        createTypeScriptImportResolver({
          noWarnOnMultipleProjects: true,
          project: [
            './tsconfig.main.json',
            './tsconfig.preload.json',
            './tsconfig.renderer.json',
            './tsconfig.tools.json',
          ],
        }),
      ],
    },
  },
);
