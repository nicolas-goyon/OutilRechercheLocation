import assert from 'node:assert/strict';
import { test } from 'node:test';
import { isAllowed, RelayWorker, type RelayApi } from '../src/core/relay';
import type { RelayRequest, RelayResult } from '../src/core/types';

const ORIGIN = 'https://www.seloger.com';
const PATHS = [/^\/serp-bff\/search$/];

function worker(next: RelayRequest | undefined, fetchFn: (url: string, init: RequestInit) => Promise<Response>) {
  const results: RelayResult[] = [];
  const api: RelayApi = {
    hasToken: () => true,
    relayNext: async () => next,
    relayResult: async (_id, r) => void results.push(r),
  };
  return { results, relay: new RelayWorker(api, { site: 'seloger', allowedPaths: PATHS, origin: ORIGIN, fetch: fetchFn }) };
}

test('isAllowed : origine et chemin de l\'adaptateur uniquement', () => {
  assert.equal(isAllowed(`${ORIGIN}/serp-bff/search`, ORIGIN, PATHS), true);
  assert.equal(isAllowed(`${ORIGIN}/compte/profil`, ORIGIN, PATHS), false);
  assert.equal(isAllowed('https://evil.example/serp-bff/search', ORIGIN, PATHS), false);
  assert.equal(isAllowed('pas une url', ORIGIN, PATHS), false);
});

test('la réponse du site repart telle quelle, y compris un refus', async () => {
  let init: RequestInit | undefined;
  const { relay, results } = worker({ id: '1', method: 'POST', url: `${ORIGIN}/serp-bff/search`, body: '{"a":1}' }, async (_u, i) => {
    init = i;
    return new Response('<html>captcha</html>', { status: 403 });
  });

  assert.equal(await relay.step(), true);

  assert.deepEqual(results, [{ status: 403, body: '<html>captcha</html>' }]);
  assert.equal(init?.credentials, 'include');
  assert.equal(init?.body, '{"a":1}');
});

test('une adresse non autorisée n\'est pas appelée', async () => {
  let called = false;
  const { relay, results } = worker({ id: '1', method: 'GET', url: `${ORIGIN}/compte/profil` }, async () => {
    called = true;
    return new Response('');
  });

  await relay.step();

  assert.equal(called, false);
  assert.equal(results[0].status, 0);
});

test('une panne réseau est signalée au serveur', async () => {
  const { relay, results } = worker({ id: '1', method: 'GET', url: `${ORIGIN}/serp-bff/search` }, async () => {
    throw new TypeError('Failed to fetch');
  });

  await relay.step();

  assert.deepEqual(results, [{ status: 0, error: 'Failed to fetch' }]);
});

test('rien à faire : aucune requête au site', async () => {
  const { relay } = worker(undefined, async () => assert.fail('ne doit pas appeler le site'));
  assert.equal(await relay.step(), false);
});
