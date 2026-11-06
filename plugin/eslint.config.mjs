// ESLint : règles recommandées JavaScript + TypeScript (avec les types), réglées pour un userscript.
import js from '@eslint/js';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  { ignores: ['dist/', 'node_modules/', 'userscripts/*.d.ts'] },
  js.configs.recommended,
  ...tseslint.configs.recommendedTypeChecked,
  {
    languageOptions: {
      globals: { ...globals.browser, ...globals.node, GM_info: 'readonly', GM_getValue: 'readonly', GM_setValue: 'readonly', GM_xmlhttpRequest: 'readonly', unsafeWindow: 'readonly' },
      parserOptions: { projectService: { allowDefaultProject: ['*.mjs', 'scripts/*.mjs', 'userscripts/*.mjs', 'tests/*.ts', 'tests-live/*.ts'] }, tsconfigRootDir: import.meta.dirname },
    },
    rules: {
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_', varsIgnorePattern: '^_' }],
      eqeqeq: ['error', 'always', { null: 'ignore' }],
      'no-console': ['error', { allow: ['warn', 'error', 'info', 'debug'] }],
    },
  },
  {
    // Tests : node:test renvoie des promesses volontairement non attendues ; les faux objets sont async sans await ;
    // les extraits JSON des sites ne sont pas typés.
    files: ['tests/**/*.ts', 'tests-live/**/*.ts'],
    rules: {
      '@typescript-eslint/no-floating-promises': 'off',
      '@typescript-eslint/require-await': 'off',
      '@typescript-eslint/no-unsafe-argument': 'off',
      '@typescript-eslint/no-unsafe-assignment': 'off',
      '@typescript-eslint/no-unsafe-member-access': 'off',
      '@typescript-eslint/no-unsafe-call': 'off',
      '@typescript-eslint/no-unsafe-return': 'off',
    },
  },
  { files: ['**/*.mjs'], ...tseslint.configs.disableTypeChecked },
  // Script de build : il affiche sa progression.
  { files: ['scripts/**/*.mjs'], rules: { 'no-console': 'off' } },
);
