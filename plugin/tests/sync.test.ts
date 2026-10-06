import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ApiError, type ApiClient } from '../src/core/api';
import { createStore } from '../src/core/storage';
import { SyncEngine } from '../src/core/sync';
import type { ListingView, SyncRequest, SyncResponse } from '../src/core/types';

/** Faux serveur : enregistre les requêtes, peut être "éteint". */
class FakeApi {
  online = true;
  requests: SyncRequest[] = [];
  status: Record<string, ListingView['status']> = {};
  token = 'ok';

  hasToken(): boolean {
    return this.token.length > 0;
  }

  async sync(req: SyncRequest): Promise<SyncResponse> {
    if (!this.online) throw new ApiError('Serveur injoignable', 0);
    this.requests.push(structuredClone(req));
    for (const a of req.actions) if (a.type === 'setStatus') this.status[a.key] = a.status;
    const keys = new Set([...req.observations.map((o) => `${o.site}:${o.siteId}`), ...req.actions.map((a) => a.key), ...req.want]);
    const listings: Record<string, ListingView> = {};
    for (const k of keys) listings[k] = { key: k, propertyId: `p-${k}`, status: this.status[k] ?? 'none', siblings: [], suggestions: [] };
    return { serverTime: Date.now(), appliedActionIds: req.actions.map((a) => a.id), rejectedActionIds: [], listings };
  }
}

const newEngine = (api: FakeApi, store = createStore(`t-${Math.random()}`)) =>
  new SyncEngine(store, api as unknown as ApiClient, { clientVersion: 'test', debounceMs: 0 });

const obs = (id: string) => ({ site: 'bienici', siteId: id, source: 'card' as const, data: { price: 1000 }, seenAt: 1 });

test('envoie observations et actions, met en cache la réponse', async () => {
  const api = new FakeApi();
  const sync = newEngine(api);
  sync.observe(obs('a'));
  sync.act({ type: 'setStatus', key: 'bienici:a', status: 'rejected' });
  await sync.flush();
  assert.equal(sync.state, 'online');
  assert.equal(sync.pendingCount(), 0);
  assert.equal(sync.view('bienici:a')?.status, 'rejected');
  assert.equal(api.requests[0].observations.length, 1);
  assert.equal(api.requests[0].actions.length, 1);
});

test('hors ligne : action appliquée tout de suite, gardée puis envoyée au retour du serveur', async () => {
  const api = new FakeApi();
  api.online = false;
  const store = createStore(`t-${Math.random()}`); // = le stockage Tampermonkey, qui survit au rechargement
  const sync = newEngine(api, store);
  sync.act({ type: 'setStatus', key: 'bienici:b', status: 'toContact' });
  await sync.flush();
  assert.equal(sync.state, 'offline');
  assert.equal(sync.view('bienici:b')?.status, 'toContact'); // masquage / visuel immédiat
  assert.equal(sync.view('bienici:b')?.contactStage, 'pending');
  assert.equal(sync.pendingCount(), 1);

  // Rechargement de la page pendant la coupure : la file est persistée.
  const reloaded = newEngine(api, store);
  assert.equal(reloaded.pendingCount(), 1);
  assert.equal(reloaded.view('bienici:b')?.status, 'toContact');

  api.online = true;
  await reloaded.flush();
  assert.equal(reloaded.state, 'online');
  assert.equal(reloaded.pendingCount(), 0);
  assert.equal(api.status['bienici:b'], 'toContact');
});

test('token refusé : état "unauthorized", rien n\'est perdu', async () => {
  const api = new FakeApi();
  api.sync = async () => {
    throw new ApiError('Token refusé', 401);
  };
  const sync = newEngine(api);
  sync.act({ type: 'setStatus', key: 'bienici:c', status: 'seen' });
  await sync.flush();
  assert.equal(sync.state, 'unauthorized');
  assert.equal(sync.pendingCount(), 1);
});

test('le statut optimiste s\'applique à toutes les annonces du même bien', async () => {
  const api = new FakeApi();
  const sync = newEngine(api);
  sync.observe(obs('x'));
  await sync.flush();
  // Simule deux annonces du même bien dans le cache.
  const vx = sync.view('bienici:x')!;
  (sync as unknown as { cache: Record<string, ListingView> }).cache['seloger:y'] = { ...vx, key: 'seloger:y' };
  api.online = false;
  sync.act({ type: 'setStatus', key: 'bienici:x', status: 'rejected' });
  assert.equal(sync.view('seloger:y')?.status, 'rejected');
});

test('une observation "card" ne remplace pas une observation "api" en attente', () => {
  const api = new FakeApi();
  api.online = false;
  const sync = newEngine(api);
  sync.observe({ site: 'bienici', siteId: 'z', source: 'api', data: { surface: 30.4, agencyRef: 'R1' }, seenAt: 1 });
  sync.observe({ site: 'bienici', siteId: 'z', source: 'card', data: { surface: 30, title: 'T' }, seenAt: 2 });
  const pending = (sync as unknown as { pendingObs: Record<string, { source: string; data: Record<string, unknown> }> }).pendingObs['bienici:z'];
  assert.equal(pending.source, 'api');
  assert.equal(pending.data.surface, 30.4);
  assert.equal(pending.data.title, 'T');
});

test('sans token : rien n\'est envoyé, tout reste en file', async () => {
  const api = new FakeApi();
  api.token = '';
  const sync = newEngine(api);
  sync.act({ type: 'setStatus', key: 'bienici:d', status: 'rejected' });
  await sync.flush();
  assert.equal(sync.state, 'unconfigured');
  assert.equal(api.requests.length, 0);
  assert.equal(sync.view('bienici:d')?.status, 'rejected');
  api.token = 'ok';
  await sync.flush();
  assert.equal(sync.state, 'online');
  assert.equal(api.status['bienici:d'], 'rejected');
});
