/**
 * Réglages de connexion au site local (URL + token).
 *
 * Le script installé est auto-mis à jour depuis une GitHub Release publique :
 * le token ne peut donc pas y figurer. Il est saisi une fois dans le plugin
 * (panneau 🏠 > Connexion) et gardé dans le stockage Tampermonkey, que les
 * mises à jour du script ne touchent pas.
 *
 * Priorité : valeur saisie dans le plugin > config de init() (anciens scripts
 * collés à la main avec le token) > défauts.
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
    // Réglage modifié dans un autre onglet : on suit.
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
