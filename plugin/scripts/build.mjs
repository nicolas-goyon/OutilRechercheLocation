import { build, context } from 'esbuild';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

// Copié du template Tampermonkey-MultiFile-Plugin-Template v2 (auto-update).
// Adapté au monorepo : le plugin est dans plugin/ et ses tags sont "plugin-vX.Y.Z".

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(__dirname, '..');
const distDir = path.join(root, 'dist');
const userscriptsDir = path.join(root, 'userscripts');
const watch = process.argv.includes('--watch');
const pkg = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'));

// Bundle unique : window.TMRechercheLogement.init({ ... }).
const entry = {
  in: path.join(root, 'src', 'index.ts'),
  out: 'recherche-logement',
  globalName: 'TMRechercheLogement',
};

// Préfixe des tags du plugin. (Le dépôt contient aussi le serveur.)
const TAG_PREFIX = 'plugin-v';

// Chemin du plugin dans le dépôt (pour les URL jsDelivr).
const PLUGIN_DIR_IN_REPO = 'plugin';

// ---------------------------------------------------------------------------
// Coordonnées de la release
// ---------------------------------------------------------------------------

// La CI transmet le tag publié (RELEASE_VERSION=plugin-v1.2.3). En local, le build utilise la version de package.json.
const version = (process.env.RELEASE_VERSION || pkg.version).replace(/^(plugin-)?v/, '');
if (!/^\d+\.\d+\.\d+/.test(version)) {
  throw new Error(`Version incorrecte "${version}". Format attendu : X.Y.Z.`);
}

// "owner/repo" : GITHUB_REPOSITORY en CI. Sinon, "repository" de package.json.
const repo = process.env.GITHUB_REPOSITORY || repoFromPackageJson();

function repoFromPackageJson() {
  const raw = typeof pkg.repository === 'string' ? pkg.repository : pkg.repository?.url;
  const m = raw && raw.match(/github(?:\.com)?[:/]([^/]+\/[^/.#]+)/);
  if (!m) throw new Error('Dépôt GitHub inconnu. Remplir "repository" dans package.json ou définir GITHUB_REPOSITORY.');
  return m[1];
}

// URL épinglée sur le tag. Tampermonkey garde les @require en cache sans limite
// de durée. L'URL doit donc changer à chaque nouveau contenu.
const bundleUrl = `https://cdn.jsdelivr.net/gh/${repo}@${TAG_PREFIX}${version}/${PLUGIN_DIR_IN_REPO}/dist/${entry.out}.js`;

// GitHub redirige releases/latest/download/<fichier> vers la release la plus
// récente. Tampermonkey lit cette URL fixe pour trouver les mises à jour.
// (Dans ce dépôt, seul le plugin publie des releases GitHub. Voir plugin.yml.)
const latestAssetUrl = (file) => `https://github.com/${repo}/releases/latest/download/${file}`;

// ---------------------------------------------------------------------------
// Bundle
// ---------------------------------------------------------------------------

const options = {
  bundle: true,
  format: 'iife',
  target: 'es2020',
  sourcemap: true,
  logLevel: 'info',
  charset: 'utf8',
  entryPoints: [entry.in],
  outfile: path.join(distDir, `${entry.out}.js`),
  globalName: entry.globalName,
  define: {
    __PLUGIN_VERSION__: JSON.stringify(version),
  },
  // Dans le sandbox Tampermonkey (@grant != none), le `var` de l'IIFE reste
  // local. Ce code l'attache donc à window.
  footer: {
    js: `if (typeof window !== 'undefined') { window.${entry.globalName} = ${entry.globalName}; }`,
  },
};

// ---------------------------------------------------------------------------
// Userscripts (un par userscripts/<id>.mjs)
// ---------------------------------------------------------------------------

// Le build génère ces clés. Les fichiers d'instance ne peuvent pas les définir.
const RESERVED = new Set(['version', 'updateURL', 'downloadURL', 'installURL']);

const DEFAULT_HEADERS = {
  noframes: true,
  'run-at': 'document-idle',
  grant: ['unsafeWindow', 'GM_registerMenuCommand'],
};

const HEADER_ORDER = [
  'name', 'namespace', 'version', 'description', 'author', 'icon', 'homepageURL', 'supportURL',
  'match', 'include', 'exclude-match', 'exclude', 'noframes', 'run-at', 'grant', 'connect',
  'require', 'resource', 'updateURL', 'downloadURL',
];

// Clés de config interdites dans un .user.js publié.
const SECRET_CONFIG_KEYS = ['apiToken'];

async function loadInstances() {
  if (!fs.existsSync(userscriptsDir)) return [];
  const files = fs
    .readdirSync(userscriptsDir)
    .filter((f) => f.endsWith('.mjs') && !f.startsWith('_'))
    .sort();

  const instances = [];
  const seen = new Map();
  for (const file of files) {
    const id = path.basename(file, '.mjs');
    if (!/^[a-z0-9][a-z0-9-]*$/.test(id)) {
      throw new Error(`userscripts/${file} : le nom de fichier doit être en kebab-case. (Il devient ${id}.user.js.)`);
    }
    const mod = (await import(pathToFileURL(path.join(userscriptsDir, file)).href)).default;
    if (!mod || typeof mod !== 'object' || !mod.headers || !mod.config) {
      throw new Error(`userscripts/${file} : l'export par défaut doit être { headers, config }`);
    }
    const { headers, config } = mod;
    for (const key of ['name', 'namespace', 'match']) {
      if (!headers[key]) throw new Error(`userscripts/${file} : headers.${key} est obligatoire`);
    }
    for (const key of Object.keys(headers)) {
      if (RESERVED.has(key)) throw new Error(`userscripts/${file} : le build génère headers.${key}. Retirer cette clé.`);
    }
    for (const key of SECRET_CONFIG_KEYS) {
      if (config[key]) {
        throw new Error(`userscripts/${file} : config.${key} est secret et le .user.js est public. Retirer cette clé. L'utilisateur saisit cette valeur dans le plugin (panneau 🏠 > Connexion).`);
      }
    }
    // Tampermonkey identifie un script par @name + @namespace.
    const identity = `${headers.namespace}\u0000${headers.name}`;
    if (seen.has(identity)) throw new Error(`userscripts/${file} et ${seen.get(identity)} ont le même @name + @namespace`);
    seen.set(identity, `userscripts/${file}`);
    instances.push({ id, headers, config });
  }
  return instances;
}

function renderHeader(headers) {
  const keys = [
    ...HEADER_ORDER.filter((k) => k in headers),
    ...Object.keys(headers).filter((k) => !HEADER_ORDER.includes(k)),
  ];
  const lines = [];
  for (const key of keys) {
    const value = headers[key];
    if (value === false || value == null) continue;
    for (const v of Array.isArray(value) ? value : [value]) {
      lines.push(v === true ? `// @${key}` : `// ${`@${key}`.padEnd(14)} ${v}`);
    }
  }
  return ['// ==UserScript==', ...lines, '// ==/UserScript=='].join('\n');
}

function renderInstance({ id, headers, config }) {
  const extraRequires = [].concat(headers.require ?? []);
  const full = {
    ...DEFAULT_HEADERS,
    homepageURL: `https://github.com/${repo}`,
    ...headers,
    version,
    require: [bundleUrl, ...extraRequires],
    updateURL: latestAssetUrl(`${id}.meta.js`),
    downloadURL: latestAssetUrl(`${id}.user.js`),
  };
  const header = renderHeader(full);
  const configJson = JSON.stringify(config, null, 2).replace(/\n/g, '\n  ');
  const body = [
    '',
    `// GÉNÉRÉ par plugin/scripts/build.mjs depuis plugin/userscripts/${id}.mjs. Ne pas modifier.`,
    '// Tampermonkey écrase ce script à chaque mise à jour automatique. Pour le',
    '// changer, modifier le fichier d\'instance dans le dépôt et publier une nouvelle version.',
    '// Ce script ne contient pas le token du serveur local. L\'utilisateur saisit',
    '// le token dans le plugin (bouton 🏠 > Connexion). Le token reste dans le stockage Tampermonkey.',
    '',
    '(function () {',
    "  'use strict';",
    '',
    `  window.${entry.globalName}.init(${configJson});`,
    '})();',
    '',
  ].join('\n');
  return { meta: `${header}\n`, user: `${header}\n${body}` };
}

async function writeUserscripts() {
  const instances = await loadInstances();
  fs.mkdirSync(distDir, { recursive: true });
  for (const instance of instances) {
    const { meta, user } = renderInstance(instance);
    fs.writeFileSync(path.join(distDir, `${instance.id}.user.js`), user);
    fs.writeFileSync(path.join(distDir, `${instance.id}.meta.js`), meta);
    console.log(`[userscript] -> dist/${instance.id}.user.js + .meta.js (v${version})`);
  }
  if (instances.length === 0) console.warn('[userscript] aucun fichier userscripts/*.mjs. Aucun script généré.');
}

async function run() {
  console.log(`[build] version ${version}, dépôt ${repo}`);
  if (watch) {
    await writeUserscripts();
    const ctx = await context(options);
    await ctx.watch();
    console.log(`[watch] -> dist/${entry.out}.js (window.${entry.globalName})`);
  } else {
    await build(options);
    console.log(`[build] -> dist/${entry.out}.js (window.${entry.globalName})`);
    await writeUserscripts();
  }
}

run().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
