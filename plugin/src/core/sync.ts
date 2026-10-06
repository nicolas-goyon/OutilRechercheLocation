/**
 * Synchronisation plugin <-> serveur. Fonctionne aussi hors ligne.
 *
 *  - observe() : une annonce affichée à l'écran (données de la carte ou de l'API du site).
 *  - act()     : une action de l'utilisateur (statut, note, doublon...).
 *
 * Le moteur met les observations et les actions dans une file. Il garde la
 * file dans le stockage Tampermonkey. Il envoie la file par lots à
 * POST /api/sync. Le serveur répond avec l'état de chaque annonce concernée
 * (ListingView). Le moteur garde cet état dans un cache local.
 *
 * Si le serveur est arrêté :
 *  1. Le moteur applique tout de suite les actions au cache. La carte est donc
 *     masquée immédiatement.
 *  2. Les actions restent dans la file jusqu'au redémarrage du serveur.
 *  3. Le moteur essaie de nouveau avec un délai croissant (5 s -> 2 min).
 */
import { ApiError, type ApiClient } from './api';
import type { KeyValueStore } from './storage';
import type { Action, ListingKey, ListingView, NewAction, Observation, SyncRequest } from './types';

export type ConnectionState = 'unknown' | 'online' | 'offline' | 'unauthorized' | 'unconfigured';

interface CachedView extends ListingView {
  cachedAt: number;
}

const CACHE_KEY = 'cache';
const QUEUE_KEY = 'queue';
const OBS_KEY = 'observations';
const MAX_CACHE = 5000;
const MAX_BATCH_OBS = 200;
const MAX_BACKOFF_MS = 120_000;

export interface SyncOptions {
  clientVersion: string;
  /** Délai pendant lequel le moteur regroupe les envois. Default 300 ms. */
  debounceMs?: number;
  now?: () => number;
}

export class SyncEngine {
  state: ConnectionState = 'unknown';
  lastError?: string;
  lastSyncAt?: number;

  private cache: Record<ListingKey, CachedView>;
  private queue: Action[];
  private pendingObs: Record<ListingKey, Observation>;
  private want = new Set<ListingKey>();
  private timer: ReturnType<typeof setTimeout> | null = null;
  private inflight = false;
  private backoffMs = 0;
  private listeners = new Set<() => void>();
  private readonly now: () => number;

  constructor(
    private readonly store: KeyValueStore,
    private readonly api: ApiClient,
    private readonly options: SyncOptions,
  ) {
    this.now = options.now ?? Date.now;
    this.cache = store.get<Record<ListingKey, CachedView>>(CACHE_KEY, {});
    this.queue = store.get<Action[]>(QUEUE_KEY, []);
    this.pendingObs = store.get<Record<ListingKey, Observation>>(OBS_KEY, {});
    // Si un autre onglet modifie la file, le moteur fusionne les deux files.
    store.onRemoteChange(QUEUE_KEY, (v) => {
      this.queue = mergeQueues(this.queue, (v as Action[]) ?? []);
    });
  }

  // ---------------------------------------------------------------- lecture

  view(key: ListingKey): ListingView | undefined {
    return this.cache[key];
  }

  pendingCount(): number {
    return this.queue.length + Object.keys(this.pendingObs).length;
  }

  onChange(cb: () => void): () => void {
    this.listeners.add(cb);
    return () => this.listeners.delete(cb);
  }

  // ---------------------------------------------------------------- écriture

  observe(obs: Observation): void {
    const key = `${obs.site}:${obs.siteId}`;
    const prev = this.pendingObs[key];
    // Fusion : une observation 'card' plus récente ne remplace pas une observation 'api' (plus complète).
    this.pendingObs[key] =
      prev && prev.source === 'api' && obs.source === 'card'
        ? { ...prev, data: { ...obs.data, ...prev.data }, seenAt: obs.seenAt }
        : prev
          ? { ...obs, data: { ...prev.data, ...obs.data } }
          : obs;
    this.store.set(OBS_KEY, this.pendingObs);
    this.schedule();
  }

  /** Demande l'état actuel de ces annonces au prochain envoi. */
  refresh(keys: ListingKey[]): void {
    for (const k of keys) this.want.add(k);
    this.schedule();
  }

  act(action: NewAction): void {
    const full = { ...action, id: newId(), at: this.now() } as Action;
    this.queue.push(full);
    this.persistQueue();
    this.applyOptimistic(full);
    this.emit();
    this.schedule(0);
  }

  /** Envoie immédiatement (bouton "Réessayer"). */
  retryNow(): void {
    this.backoffMs = 0;
    this.schedule(0);
  }

  // ---------------------------------------------------------------- envoi

  private schedule(delay = this.options.debounceMs ?? 300): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => void this.flush(), Math.max(delay, this.backoffMs));
    // Sous Node (tests), le timer ne bloque pas la fin du processus. Sans effet dans le navigateur.
    (this.timer as unknown as { unref?: () => void }).unref?.();
  }

  async flush(): Promise<void> {
    if (this.inflight) return;
    // Token absent : le moteur garde tout dans la file et n'appelle pas le serveur.
    if (!this.api.hasToken()) {
      if (this.state !== 'unconfigured') {
        this.state = 'unconfigured';
        this.emit();
      }
      return;
    }
    const obsEntries = Object.entries(this.pendingObs).slice(0, MAX_BATCH_OBS);
    const actions = [...this.queue];
    const want = [...this.want];
    if (obsEntries.length === 0 && actions.length === 0 && want.length === 0) return;

    this.inflight = true;
    const request: SyncRequest = {
      clientVersion: this.options.clientVersion,
      observations: obsEntries.map(([, o]) => o),
      actions,
      want,
    };
    try {
      const res = await this.api.sync(request);
      // Retire de la file les éléments envoyés. Garde une observation si une version plus récente est arrivée pendant l'envoi.
      for (const [k, o] of obsEntries) if (this.pendingObs[k] === o) delete this.pendingObs[k];
      const done = new Set([...res.appliedActionIds, ...res.rejectedActionIds]);
      this.queue = this.queue.filter((a) => !done.has(a.id));
      for (const k of want) this.want.delete(k);
      const now = this.now();
      for (const [k, v] of Object.entries(res.listings)) this.cache[k] = { ...v, cachedAt: now };
      // Les actions encore dans la file (arrivées pendant l'envoi) ont priorité pour l'affichage.
      for (const a of this.queue) this.applyOptimistic(a);
      this.store.set(OBS_KEY, this.pendingObs);
      this.persistQueue(done);
      this.persistCache();
      this.state = 'online';
      this.lastError = undefined;
      this.lastSyncAt = now;
      this.backoffMs = 0;
    } catch (e) {
      const err = e as ApiError;
      this.state = err.status === 401 ? 'unauthorized' : 'offline';
      this.lastError = err.message;
      this.backoffMs = Math.min(MAX_BACKOFF_MS, this.backoffMs ? this.backoffMs * 2 : 5_000);
    } finally {
      this.inflight = false;
      this.emit();
      if (this.pendingCount() > 0 || this.want.size > 0) this.schedule();
    }
  }

  // ---------------------------------------------------------------- interne

  /** Applique au cache l'effet visible d'une action, avant la réponse du serveur. */
  private applyOptimistic(a: Action): void {
    const v = this.cache[a.key] ?? (this.cache[a.key] = emptyView(a.key, this.now()));
    const sameProperty = Object.values(this.cache).filter((c) => c.propertyId === v.propertyId);
    switch (a.type) {
      case 'setStatus':
        for (const c of sameProperty) {
          c.status = a.status;
          if (a.status === 'toContact' && !c.contactStage) c.contactStage = 'pending';
        }
        break;
      case 'setNote':
        for (const c of sameProperty) c.note = a.note || undefined;
        break;
      case 'confirmDuplicate': {
        const s = v.suggestions.find((x) => x.other.key === a.otherKey);
        v.suggestions = v.suggestions.filter((x) => x.other.key !== a.otherKey);
        if (s) {
          v.siblings = [...v.siblings, s.other];
          if (v.status === 'none') v.status = s.otherStatus;
        }
        break;
      }
      case 'dismissDuplicate':
        v.suggestions = v.suggestions.filter((x) => x.other.key !== a.otherKey);
        break;
      case 'detach':
        v.siblings = [];
        v.propertyId = `local:${a.key}`;
        v.status = 'none';
        break;
    }
    this.persistCache();
  }

  private persistQueue(done?: Set<string>): void {
    const stored = this.store.get<Action[]>(QUEUE_KEY, []);
    this.queue = mergeQueues(this.queue, stored).filter((a) => !done?.has(a.id));
    this.store.set(QUEUE_KEY, this.queue);
  }

  private persistCache(): void {
    const keys = Object.keys(this.cache);
    if (keys.length > MAX_CACHE) {
      keys
        .sort((a, b) => this.cache[a].cachedAt - this.cache[b].cachedAt)
        .slice(0, keys.length - MAX_CACHE)
        .forEach((k) => delete this.cache[k]);
    }
    this.store.set(CACHE_KEY, this.cache);
  }

  private emit(): void {
    for (const cb of this.listeners) cb();
  }
}

function emptyView(key: ListingKey, now: number): CachedView {
  return { key, propertyId: `local:${key}`, status: 'none', siblings: [], suggestions: [], cachedAt: now };
}

function mergeQueues(a: Action[], b: Action[]): Action[] {
  const byId = new Map<string, Action>();
  for (const x of [...a, ...b]) byId.set(x.id, x);
  return [...byId.values()].sort((x, y) => x.at - y.at);
}

function newId(): string {
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}
