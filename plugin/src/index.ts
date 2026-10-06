/**
 * Point d'entrée du plugin.
 *
 * esbuild produit le bundle dist/recherche-logement.js. Le bundle expose ce
 * module sur window.TMRechercheLogement (voir scripts/build.mjs).
 *
 * Le script installé (dist/recherche-logement.user.js) est généré depuis
 * userscripts/recherche-logement.mjs. Il contient seulement cet appel :
 *   window.TMRechercheLogement.init({ serverUrl: 'http://localhost:5080' });
 *
 * Le token n'est PAS transmis ici, car GitHub publie le script. L'utilisateur
 * saisit le token dans le panneau 🏠 > Connexion. Le plugin garde le token
 * dans le stockage Tampermonkey.
 */
import { Tracker } from './app/tracker';
import { ApiClient, type ServerInfo } from './core/api';
import { ConnectionStore, normalizeUrl, type ConnectionSettings } from './core/connection';
import { registerMenuCommand } from './core/menuCommand';
import { createStore } from './core/storage';
import { SyncEngine } from './core/sync';
import type { PropertyStatus } from './core/types';
import { findAdapter } from './sites';
import { installFloatingButton } from './ui/floatingButton';
import { isModalOpen } from './ui/modal';
import { showPanel } from './ui/views';
import { THEME } from './ui/theme';

/** Version de la release. Le build injecte cette valeur (= @version du script installé). */
export const VERSION: string = typeof __PLUGIN_VERSION__ === 'string' ? __PLUGIN_VERSION__ : 'dev';

export interface InitConfig {
  /** URL du serveur local. Default: 'http://localhost:5080'. Le panneau permet de la modifier. */
  serverUrl?: string;
  /**
   * Ancien mode (script collé à la main) : token transmis dans la config.
   * Utiliser de préférence le panneau. La valeur du panneau a priorité sur cette valeur.
   */
  apiToken?: string;
  /** Statuts masqués. Default: ['rejected']. */
  hideStatuses?: PropertyStatus[];
  /** Statuts dont les doublons PROBABLES (non confirmés) sont aussi masqués. Default: []. */
  hideSuggestedDuplicatesOf?: PropertyStatus[];
  buttonOffset?: { right?: number; bottom?: number };
  debug?: boolean;
}

/** Pour le débogage dans la console : window.TMRechercheLogement.instance. */
export interface PluginHandle {
  sync: SyncEngine;
  tracker: Tracker;
  connection: ConnectionStore;
}

export let instance: PluginHandle | undefined;

export function init(config: InitConfig = {}): PluginHandle | undefined {
  if (window.self !== window.top) return undefined;
  if (instance) return instance;

  const adapter = findAdapter(location);
  if (!adapter) {
    if (config.debug) console.debug('[RechercheLogement] site d\'annonces non compatible', location.hostname);
    return undefined;
  }

  const store = createStore('recherche-logement');
  const connection = new ConnectionStore(store, { serverUrl: config.serverUrl, token: config.apiToken });
  const initial = connection.get();
  const api = new ApiClient({ baseUrl: initial.serverUrl, token: initial.token });
  const sync = new SyncEngine(store, api, { clientVersion: VERSION });
  connection.onChange((s) => {
    api.configure(s.serverUrl, s.token);
    sync.retryNow();
  });
  window.addEventListener('pagehide', () => void sync.flush());

  const tracker = new Tracker(adapter, sync, {
    hideStatuses: config.hideStatuses ?? ['rejected'],
    hideSuggestedDuplicatesOf: config.hideSuggestedDuplicatesOf ?? [],
    debug: config.debug ?? false,
  });

  const testConnection = async (s: ConnectionSettings): Promise<string> => {
    const info: ServerInfo = await new ApiClient({ baseUrl: normalizeUrl(s.serverUrl), token: s.token.trim(), timeoutMs: 5000 }).ping();
    return `Connecté (${info.name} v${info.version})`;
  };

  const openPanel = () =>
    showPanel({
      sync,
      connection,
      testConnection,
      version: VERSION,
      pageCounts: () => tracker.pageCounts(),
      showHidden: tracker.showHidden,
      setShowHidden: (v) => tracker.setShowHidden(v),
    });

  const button = installFloatingButton({
    id: 'tmrl-button',
    label: '🏠',
    title: `Suivi de recherche logement (v${VERSION})`,
    onClick: openPanel,
    offset: config.buttonOffset,
  });
  const refreshBadge = () => {
    const c = tracker.pageCounts();
    if (sync.state === 'unauthorized' || sync.state === 'unconfigured') button.setBadge('!', THEME.rejected);
    else if (sync.state === 'offline') button.setBadge('⚠', THEME.suggest);
    else if (c.suggested > 0) button.setBadge(`≈${c.suggested}`, THEME.suggest);
    else button.setBadge(c.hidden > 0 ? String(c.hidden) : null, THEME.rejected);
  };
  tracker.onCounts(refreshBadge);
  sync.onChange(refreshBadge);

  registerMenuCommand('Ouvrir le panneau (connexion, réglages)', openPanel);
  registerMenuCommand('Afficher / masquer les annonces masquées', () => tracker.setShowHidden(!tracker.showHidden));
  registerMenuCommand('Ouvrir le serveur local', () => window.open(connection.get().serverUrl, '_blank'));

  tracker.start();

  // Première installation : le token est absent. Le plugin ouvre alors le panneau.
  if (!connection.isConfigured()) setTimeout(() => !isModalOpen() && openPanel(), 800);

  instance = { sync, tracker, connection };
  return instance;
}
