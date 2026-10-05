/**
 * Point d'entrée, bundlé par esbuild -> dist/recherche-logement.js, exposé
 * sur window.TMRechercheLogement (voir scripts/build.mjs).
 *
 * Le script Tampermonkey se résume à :
 *   window.TMRechercheLogement.init({ serverUrl, apiToken });
 */
import { Tracker } from './app/tracker';
import { ApiClient } from './core/api';
import { registerMenuCommand } from './core/menuCommand';
import { createStore } from './core/storage';
import { SyncEngine } from './core/sync';
import type { PropertyStatus } from './core/types';
import { findAdapter } from './sites';
import { installFloatingButton } from './ui/floatingButton';
import { showPanel } from './ui/views';
import { THEME } from './ui/theme';

export const VERSION = '0.2.0';

export interface InitConfig {
  /** URL du site local. Default: 'http://localhost:5080'. */
  serverUrl?: string;
  /** Token affiché dans Paramètres du site local (obligatoire). */
  apiToken: string;
  /** Statuts masqués. Default: ['rejected']. */
  hideStatuses?: PropertyStatus[];
  /** Masquer aussi les doublons PROBABLES (non confirmés) de biens ayant ces statuts. Default: []. */
  hideSuggestedDuplicatesOf?: PropertyStatus[];
  buttonOffset?: { right?: number; bottom?: number };
  debug?: boolean;
}

/** Accès de débogage depuis la console : window.TMRechercheLogement.instance. */
export interface PluginHandle {
  sync: SyncEngine;
  tracker: Tracker;
}

export let instance: PluginHandle | undefined;

export function init(config: InitConfig): PluginHandle | undefined {
  if (window.self !== window.top) return undefined;
  if (instance) return instance;

  const adapter = findAdapter(location);
  if (!adapter) {
    if (config.debug) console.debug('[RechercheLogement] site non supporté', location.hostname);
    return undefined;
  }
  if (!config.apiToken) {
    console.warn('[RechercheLogement] apiToken manquant : copier le snippet depuis Paramètres du site local.');
  }

  const serverUrl = (config.serverUrl ?? 'http://localhost:5080').replace(/\/+$/, '');
  const api = new ApiClient({ baseUrl: serverUrl, token: config.apiToken ?? '' });
  const sync = new SyncEngine(createStore('recherche-logement'), api, { clientVersion: VERSION });
  window.addEventListener('pagehide', () => void sync.flush());

  const tracker = new Tracker(adapter, sync, {
    hideStatuses: config.hideStatuses ?? ['rejected'],
    hideSuggestedDuplicatesOf: config.hideSuggestedDuplicatesOf ?? [],
    debug: config.debug ?? false,
  });

  const openPanel = () =>
    showPanel({
      sync,
      serverUrl,
      pageCounts: () => tracker.pageCounts(),
      showHidden: tracker.showHidden,
      setShowHidden: (v) => tracker.setShowHidden(v),
    });

  const button = installFloatingButton({
    id: 'tmrl-button',
    label: '🏠',
    title: 'Suivi de recherche logement',
    onClick: openPanel,
    offset: config.buttonOffset,
  });
  const refreshBadge = () => {
    const c = tracker.pageCounts();
    if (sync.state === 'unauthorized') button.setBadge('!', THEME.rejected);
    else if (sync.state === 'offline') button.setBadge('⚠', THEME.suggest);
    else if (c.suggested > 0) button.setBadge(`≈${c.suggested}`, THEME.suggest);
    else button.setBadge(c.hidden > 0 ? String(c.hidden) : null, THEME.rejected);
  };
  tracker.onCounts(refreshBadge);
  sync.onChange(refreshBadge);

  registerMenuCommand('Ouvrir le panneau', openPanel);
  registerMenuCommand('Afficher / cacher les annonces masquées', () => tracker.setShowHidden(!tracker.showHidden));
  registerMenuCommand('Ouvrir le site local', () => window.open(serverUrl, '_blank'));

  tracker.start();
  instance = { sync, tracker };
  return instance;
}
