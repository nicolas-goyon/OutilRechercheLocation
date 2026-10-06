/**
 * Wrapper autour du stockage Tampermonkey (GM_getValue/GM_setValue).
 *
 * - Le stockage GM appartient au SCRIPT, pas au site d'annonces. Un seul
 *   script Tampermonkey avec plusieurs @match partage donc la même base entre
 *   Bien'ici, SeLoger, etc. Cette base commune permet de trouver les doublons
 *   entre sites. (Deux scripts différents ont deux bases séparées.)
 * - Hors Tampermonkey (tests, injection manuelle), le wrapper utilise
 *   localStorage. Si localStorage est absent, il utilise la mémoire.
 */
export interface KeyValueStore {
  get<T>(key: string, fallback: T): T;
  set<T>(key: string, value: T): void;
  /** Appelle cb quand un AUTRE onglet modifie la clé. Renvoie une fonction qui arrête l'écoute. */
  onRemoteChange(key: string, cb: (value: unknown) => void): () => void;
}

export function createStore(namespace: string): KeyValueStore {
  const k = (key: string) => `${namespace}:${key}`;

  if (typeof GM_getValue === 'function' && typeof GM_setValue === 'function') {
    return {
      get: (key, fallback) => GM_getValue(k(key), fallback),
      set: (key, value) => GM_setValue(k(key), value),
      onRemoteChange(key, cb) {
        if (typeof GM_addValueChangeListener !== 'function') return () => {};
        const id = GM_addValueChangeListener(k(key), (_name, _old, value, remote) => {
          if (remote) cb(value);
        });
        return () => {
          if (typeof GM_removeValueChangeListener === 'function') GM_removeValueChangeListener(id);
        };
      },
    };
  }

  const memory = new Map<string, string>();
  const ls = (() => {
    try {
      return typeof localStorage !== 'undefined' ? localStorage : null;
    } catch {
      return null;
    }
  })();
  return {
    get(key, fallback) {
      const raw = ls ? ls.getItem(k(key)) : memory.get(k(key)) ?? null;
      if (raw == null) return fallback;
      try {
        return JSON.parse(raw);
      } catch {
        return fallback;
      }
    },
    set(key, value) {
      const raw = JSON.stringify(value);
      if (ls) ls.setItem(k(key), raw);
      else memory.set(k(key), raw);
    },
    onRemoteChange(key, cb) {
      if (typeof window === 'undefined') return () => {};
      const handler = (e: StorageEvent) => {
        if (e.key === k(key) && e.newValue) cb(JSON.parse(e.newValue));
      };
      window.addEventListener('storage', handler);
      return () => window.removeEventListener('storage', handler);
    },
  };
}
