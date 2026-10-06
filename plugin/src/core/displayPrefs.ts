/**
 * Réglages d'affichage choisis dans le panneau 🏠 (stockage Tampermonkey, communs à tous les sites).
 *
 * Par défaut, seules les annonces "pas intéressé" sont masquées. Pour ne voir que les nouvelles
 * annonces, on masque aussi les annonces vues et les annonces à contacter.
 */
import type { KeyValueStore } from './storage';
import type { PropertyStatus } from './types';

export interface DisplayPrefs {
  /** Masquer les annonces marquées 👁 vues. */
  hideSeen: boolean;
  /** Masquer les annonces marquées 📞 à contacter. */
  hideToContact: boolean;
}

const KEY = 'display';
const DEFAULTS: DisplayPrefs = { hideSeen: false, hideToContact: false };

export class DisplayPrefsStore {
  private prefs: DisplayPrefs;
  private listeners = new Set<(p: DisplayPrefs) => void>();

  constructor(private readonly store: KeyValueStore) {
    this.prefs = { ...DEFAULTS, ...store.get<Partial<DisplayPrefs>>(KEY, {}) };
    // Changement fait dans un autre onglet : appliqué ici aussi.
    store.onRemoteChange(KEY, (v) => {
      this.prefs = { ...DEFAULTS, ...((v as Partial<DisplayPrefs>) ?? {}) };
      this.emit();
    });
  }

  get(): DisplayPrefs {
    return { ...this.prefs };
  }

  set(patch: Partial<DisplayPrefs>): void {
    this.prefs = { ...this.prefs, ...patch };
    this.store.set(KEY, this.prefs);
    this.emit();
  }

  /** Mode "nouvelles annonces seulement" : vues et à contacter masquées. */
  get onlyNew(): boolean {
    return this.prefs.hideSeen && this.prefs.hideToContact;
  }

  onChange(cb: (p: DisplayPrefs) => void): void {
    this.listeners.add(cb);
  }

  private emit(): void {
    for (const cb of this.listeners) cb(this.get());
  }
}

/** Statuts masqués = statuts de la configuration du script + réglages du panneau. */
export function hiddenStatuses(base: PropertyStatus[], prefs: DisplayPrefs): PropertyStatus[] {
  const out = new Set(base);
  if (prefs.hideSeen) out.add('seen');
  if (prefs.hideToContact) out.add('toContact');
  return [...out];
}
