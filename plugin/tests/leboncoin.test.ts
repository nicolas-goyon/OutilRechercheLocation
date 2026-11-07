import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { parseHTML } from 'linkedom';
import {
  adsOfNextData,
  CARD_SELECTOR,
  cardSiteId,
  parseLeboncoinAd,
  parseLeboncoinCard,
  siteIdFromUrl,
  type LeboncoinNextData,
} from '../src/sites/leboncoin/parse';

const fixture = (name: string) => readFileSync(new URL(`./fixtures/${name}`, import.meta.url), 'utf8');
const { document } = parseHTML(`<html><body>${fixture('leboncoin-cards.html')}</body></html>`);
const cards = [...document.querySelectorAll(CARD_SELECTOR)];

const nextData = (html: string) =>
  JSON.parse(html.match(/<script id="__NEXT_DATA__"[^>]*>([\s\S]*?)<\/script>/)![1]) as LeboncoinNextData;

test('Leboncoin : identifiant des cartes et des annonces', () => {
  assert.deepEqual(cards.map((c) => cardSiteId(c)), ['3296041144', '3242310156']);
  assert.equal(siteIdFromUrl('https://www.leboncoin.fr/ad/ventes_immobilieres/3267473989?x=1'), '3267473989');
  assert.equal(siteIdFromUrl('/recherche?category=9'), undefined);
});

test('Leboncoin : parse une carte', () => {
  const d = parseLeboncoinCard(cards[0]);
  assert.equal(d.url, 'https://www.leboncoin.fr/ad/ventes_immobilieres/3296041144');
  assert.equal(d.title, 'Maison 12 pièces 321 m²');
  assert.equal(d.propertyType, 'house');
  assert.equal(d.price, 1_195_000);
  assert.equal(d.rooms, 12);
  assert.equal(d.surface, 321);
  assert.equal(d.landSurface, 2146);
  assert.equal(d.energyClass, 'C');
  assert.deepEqual([d.city, d.postalCode], ['Saint-Lary-Soulan', '65170']);
  assert.equal(d.agencyName, 'CAZENEUVE Pierre-Alexandre');
  assert.equal(d.photos?.length, 2);
  assert.match(d.photos?.[0] ?? '', /rule=ad-large$/);

  const second = parseLeboncoinCard(cards[1]);
  assert.equal(second.price, 260_000);
  assert.deepEqual([second.city, second.postalCode], ['Tournay', '65190']);
});

test('Leboncoin : JSON de la page de résultats (mêmes valeurs que le serveur)', () => {
  const ads = adsOfNextData(nextData(fixture('leboncoin-recherche.html')));
  assert.equal(ads.length, 3);
  const d = parseLeboncoinAd(ads[0]);
  assert.equal(d.title, 'Propriété 5 pièces 152 m²');
  assert.equal(d.transaction, 'buy');
  assert.equal(d.propertyType, 'house');
  assert.equal(d.price, 350_000);
  assert.deepEqual([d.surface, d.rooms, d.bedrooms], [152, 5, 4]);
  assert.deepEqual([d.postalCode, d.city], ['65590', 'Bordères-Louron']);
  assert.equal(d.geo?.precisionM, 1500);
  assert.deepEqual([d.agencyRef, d.agencyName, d.agencySiren], ['V30000548', 'AGENCE DU LOURON', '909572356']);
  assert.equal(d.publishedAt, '2026-11-06T01:14:06');
  assert.equal(d.landSurface, 322);
  assert.deepEqual([d.energyClass, d.ghgClass], ['E', 'C']);
  assert.equal(d.yearBuilt, 1900);
  assert.equal(d.bathrooms, 2);
  assert.equal(d.heating, 'Électrique, Individuel');
  assert.equal(d.exposure, 'Sud');
  assert.deepEqual([d.newBuild, d.workNeeded], [false, false]);
  assert.equal(d.propertyTax, 650);
  assert.deepEqual(d.features, { garden: true, balcony: true, cellar: true, parking: true, elevator: false });

  const viager = parseLeboncoinAd(ads[2]);
  assert.equal(viager.saleKind, 'life-annuity');
  assert.equal(viager.workNeeded, true);
  assert.equal(viager.agencyName, undefined);
  assert.equal(viager.features?.pool, true);
});
