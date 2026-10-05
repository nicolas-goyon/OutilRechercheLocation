import { build, context } from 'esbuild';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(__dirname, '..');
const watch = process.argv.includes('--watch');

// Single bundle regrouping the whole plugin. One @require on the
// Tampermonkey side is enough: window.TMRechercheLogement.init({ serverUrl, apiToken }).
const entry = {
  in: path.join(root, 'src', 'index.ts'),
  out: 'recherche-logement',
  globalName: 'TMRechercheLogement',
};

const options = {
  bundle: true,
  format: 'iife',
  target: 'es2020',
  sourcemap: true,
  logLevel: 'info',
  entryPoints: [entry.in],
  outfile: path.join(root, 'dist', `${entry.out}.js`),
  globalName: entry.globalName,
  // Inside the Tampermonkey sandbox (any @grant != none), the top-level
  // `var TMRechercheLogement` of the IIFE bundle stays local to the wrapper:
  // window.TMRechercheLogement would be undefined. Attach it explicitly so
  // `window.TMRechercheLogement.init(...)` works everywhere.
  footer: {
    js: `if (typeof window !== 'undefined') { window.${entry.globalName} = ${entry.globalName}; }`,
  },
};

async function run() {
  if (watch) {
    const ctx = await context(options);
    await ctx.watch();
    console.log(`[watch] -> dist/${entry.out}.js (window.${entry.globalName})`);
  } else {
    await build(options);
    console.log(`[build] -> dist/${entry.out}.js (window.${entry.globalName})`);
  }
}

run().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
