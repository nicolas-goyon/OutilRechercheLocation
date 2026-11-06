/**
 * Test sur le vrai site (réseau) : le JSON de l'API Bien'ici est toujours lu par le
 * parser du plugin. Lancé par la CI quotidienne (npm run test:live), pas par npm test.
 */
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { parseBieniciApiAd, type BieniciApiAd } from '../src/sites/bienici/parse';

test('Bien\'ici : les annonces de l\'API sont lues', async () => {
  const filters = {
    size: 20,
    from: 0,
    filterType: 'buy',
    propertyType: ['house'],
    onTheMarket: [true],
    sortBy: 'publicationDate',
    sortOrder: 'desc',
  };
  const res = await fetch(`https://www.bienici.com/realEstateAds.json?filters=${encodeURIComponent(JSON.stringify(filters))}`, {
    headers: { Accept: 'application/json', 'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/140.0 Safari/537.36' },
  });
  assert.equal(res.status, 200);
  const json = (await res.json()) as { realEstateAds?: BieniciApiAd[] };
  const ads = json.realEstateAds ?? [];
  assert.ok(ads.length >= 10, `${ads.length} annonces reçues`);

  const parsed = ads.map(parseBieniciApiAd);
  const share = (what: string, has: (d: (typeof parsed)[number]) => boolean) => {
    const n = parsed.filter(has).length;
    assert.ok(n >= parsed.length * 0.8, `${n}/${parsed.length} annonces ont ${what} : le format du site a peut-être changé.`);
  };
  share('un prix', (d) => (d.price ?? 0) > 0);
  share('un code postal', (d) => /^\d{5}$/.test(d.postalCode ?? ''));
  share('des photos', (d) => (d.photos?.length ?? 0) > 0);
  share('le type maison', (d) => d.propertyType === 'house');
});
