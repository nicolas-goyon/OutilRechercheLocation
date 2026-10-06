import assert from 'node:assert/strict';
import { test } from 'node:test';
import { DisplayPrefsStore, hiddenStatuses } from '../src/core/displayPrefs';
import { createStore } from '../src/core/storage';

test('réglages d\'affichage : défauts, persistance, statuts masqués', () => {
  const store = createStore(`d-${Math.random()}`);
  const prefs = new DisplayPrefsStore(store);
  assert.deepEqual(prefs.get(), { hideSeen: false, hideToContact: false });
  assert.deepEqual(hiddenStatuses(['rejected'], prefs.get()), ['rejected']);

  let calls = 0;
  prefs.onChange(() => calls++);
  prefs.set({ hideSeen: true });
  assert.equal(calls, 1);
  assert.equal(prefs.onlyNew, false);
  prefs.set({ hideToContact: true });
  assert.equal(prefs.onlyNew, true);
  assert.deepEqual(hiddenStatuses(['rejected'], prefs.get()).sort(), ['rejected', 'seen', 'toContact']);

  // Rechargement (autre page, autre site) : le réglage est conservé.
  assert.deepEqual(new DisplayPrefsStore(store).get(), { hideSeen: true, hideToContact: true });
});
