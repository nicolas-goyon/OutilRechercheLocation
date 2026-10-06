import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ConnectionStore, DEFAULT_SERVER_URL } from '../src/core/connection';
import { createStore } from '../src/core/storage';

test('défauts, repli sur la config, puis réglage saisi prioritaire', () => {
  const store = createStore(`c-${Math.random()}`);
  assert.deepEqual(new ConnectionStore(store, {}).get(), { serverUrl: DEFAULT_SERVER_URL, token: '' });

  const conn = new ConnectionStore(store, { serverUrl: 'http://localhost:9000/', token: 'ancien' });
  assert.deepEqual(conn.get(), { serverUrl: 'http://localhost:9000', token: 'ancien' });

  let notified = 0;
  conn.onChange(() => notified++);
  conn.save({ serverUrl: ' http://127.0.0.1:5080// ', token: '  nouveau ' });
  assert.deepEqual(conn.get(), { serverUrl: 'http://127.0.0.1:5080', token: 'nouveau' });
  assert.equal(notified, 1);
  assert.ok(conn.isConfigured());

  // Un "nouveau" script (mise à jour auto, sans token dans la config) garde le token saisi.
  assert.equal(new ConnectionStore(store, { serverUrl: 'http://localhost:5080' }).get().token, 'nouveau');
});
