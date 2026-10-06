/**
 * Réglages de connexion au serveur local (URL + token).
 *
 * Tampermonkey met à jour le script installé depuis une GitHub Release
 * publique. Le script ne peut donc pas contenir le token. L'utilisateur saisit
 * le token une fois dans le plugin (panneau 🏠 > Connexion). Le plugin garde
 * le token dans le stockage Tampermonkey. Les mises à jour du script ne
 * changent pas ce stockage.
 *
 * Ordre de priorité :
 *  1. valeur saisie dans le plugin.
 *  2. config de init() (anciens scripts collés à la main avec le token).
 *  3. valeurs par défaut.
 */
import type { KeyValueStore } from './storage';

export interface ConnectionSettings {
  serverUrl: string;
  token: string;
}

const KEY = 'connection';
export const DEFAULT_SERVER_URL = 'http://localhost:5080';

export class ConnectionStore {
  private listeners = new Set<(s: ConnectionSettings) => void>();

  constructor(
    private readonly store: KeyValueStore,
    private readonly fallback: Partial<ConnectionSettings>,
  ) {
    // Si un autre onglet modifie le réglage, cet onglet applique la modification.
    store.onRemoteChange(KEY, () => this.emit());
  }

  get(): ConnectionSettings {
    const saved = this.store.get<Partial<ConnectionSettings>>(KEY, {});
    return {
      serverUrl: normalizeUrl(saved.serverUrl || this.fallback.serverUrl || DEFAULT_SERVER_URL),
      token: (saved.token || this.fallback.token || '').trim(),
    };
  }

  isConfigured(): boolean {
    return this.get().token.length > 0;
  }

  save(settings: ConnectionSettings): void {
    this.store.set(KEY, { serverUrl: normalizeUrl(settings.serverUrl), token: settings.token.trim() });
    this.emit();
  }

  onChange(cb: (s: ConnectionSettings) => void): () => void {
    this.listeners.add(cb);
    return () => this.listeners.delete(cb);
  }

  private emit(): void {
    const s = this.get();
    for (const cb of this.listeners) cb(s);
  }
}

export function normalizeUrl(url: string): string {
  return (url.trim() || DEFAULT_SERVER_URL).replace(/\/+$/, '');
}
