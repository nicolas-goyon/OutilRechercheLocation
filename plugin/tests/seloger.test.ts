import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { parseHTML } from 'linkedom';
import {
  AD_SELECTORS,
  CARD_SELECTOR,
  cardSiteId,
  parseAddress,
  parseFloor,
  parseKeyfacts,
  parseSelogerCard,
  parseSelogerDetailClassified,
  parseSelogerSerpClassified,
  propertyTypeFromText,
  siteIdFromUrl,
  stripPhotoSize,
} from '../src/sites/seloger/parse';

const fixture = (name: string) => readFileSync(new URL(`./fixtures/${name}`, import.meta.url), 'utf8');
const { document } = parseHTML(`<html><body>${fixture('seloger-cards.html')}</body></html>`);
const cards = [...document.querySelectorAll(CARD_SELECTOR)];

test('SeLoger : trouve les cartes et leur identifiant', () => {
  assert.deepEqual(cards.map((c) => cardSiteId(c)), ['262UMHKCWPS4', '26B3BCIM1CFF']);
});

test('SeLoger : parse une carte', () => {
  const d = parseSelogerCard(cards[0]);
  assert.equal(d.title, 'Appartement 3 pièces 57,2 m²');
  assert.equal(d.propertyType, 'flat');
  assert.equal(d.price, 949.02);
  assert.equal(d.rooms, 3);
  assert.equal(d.bedrooms, 2);
  assert.equal(d.surface, 57.2);
  assert.equal(d.floor, 7);
  assert.equal(d.postalCode, '33800');
  assert.equal(d.city, 'Bordeaux');
  assert.equal(d.district, 'Bordeaux Sud');
  assert.equal(d.transaction, 'rent');
  assert.equal(d.url, 'https://www.seloger.com/annonce/location/nouvelle-aquitaine/gironde-33/bordeaux-33000/262UMHKCWPS4');
  assert.equal(d.photos?.[0], 'https://cdnihddipa.cloudimg.io/2/d/e/b/2debcce0-1a96-4683-838b-bd21f42c096d.jpg?ci_seal=0333de191bf5f4dcf3db1dc22a2bf1557b5850d9');
  assert.ok(d.descriptionExcerpt?.startsWith('a titre exceptionnel 50 sur les honoraires'));
});

test('SeLoger : maison en duplex, étage "1/1"', () => {
  const d = parseSelogerCard(cards[1]);
  assert.equal(d.propertyType, 'house');
  assert.equal(d.floor, 1);
  assert.equal(d.price, 2540);
  assert.equal(d.surface, 137.9);
  assert.equal(d.district, 'Chartrons');
  assert.equal(d.postalCode, '33000');
});

test('SeLoger : JSON de la liste', () => {
  const d = parseSelogerSerpClassified(JSON.parse(fixture('seloger-serp-classified.json')));
  assert.equal(d.price, 949.02);
  assert.equal(d.surface, 57.17);
  assert.equal(d.rooms, 3);
  assert.equal(d.bedrooms, 2);
  assert.equal(d.floor, 7);
  assert.equal(d.propertyType, 'flat');
  assert.equal(d.transaction, 'rent');
  assert.equal(d.agencyRef, 'P181036-1642-24058');
  assert.equal(d.agencyName, 'YFR');
  assert.equal(d.postalCode, '33800');
  assert.equal(d.publishedAt, '2026-09-29T08:22:00Z');
  assert.equal(d.photos?.length, 2);
  assert.equal(d.photoKeys, undefined);
});

test('SeLoger : JSON de la page d\'une annonce (GPS, charges, meublé)', () => {
  const url = 'https://www.seloger.com/annonce/location/nouvelle-aquitaine/gironde-33/bordeaux-33000/262UMHKCWPS4?serp_view=list';
  const d = parseSelogerDetailClassified(JSON.parse(fixture('seloger-detail-classified.json')), url);
  assert.equal(d.url, 'https://www.seloger.com/annonce/location/nouvelle-aquitaine/gironde-33/bordeaux-33000/262UMHKCWPS4');
  assert.equal(d.price, 949.02);
  assert.equal(d.charges, 171.51);
  assert.equal(d.surface, 57.17);
  assert.equal(d.furnished, false);
  assert.deepEqual(d.geo, { lat: 44.8202, lon: -0.542778, precisionM: 150 });
  assert.equal(d.agencyRef, 'P181036-1642-24058');
  assert.equal(d.title, 'Appartement 3 pièces 57,17 m²');
  assert.ok(d.descriptionExcerpt?.includes('quai de brienne'));
});

test('SeLoger : petites fonctions', () => {
  assert.equal(siteIdFromUrl('https://www.seloger.com/annonce/achat/x/y/z/26ABC?x=1#a'), '26ABC');
  assert.equal(siteIdFromUrl('https://www.seloger.com/classified-search?x=1'), undefined);
  assert.equal(parseFloor('Rez-de-chaussée'), 0);
  assert.equal(parseFloor('1er étage'), 1);
  assert.deepEqual(parseAddress('Bordeaux (33000)'), { postalCode: '33000', city: 'Bordeaux', district: undefined });
  assert.deepEqual(parseKeyfacts(['Studio', '18 m²', 'Meublé']), { rooms: 1, surface: 18, furnished: true });
  assert.equal(propertyTypeFromText('Studio à louer'), 'flat');
  assert.equal(stripPhotoSize('https://x.io/a.jpg?ci_seal=abc&w=626&h=470'), 'https://x.io/a.jpg?ci_seal=abc');
});

test('SeLoger : emplacements de pub masqués, pas les annonces', () => {
  const ads = [...document.querySelectorAll(AD_SELECTORS.join(','))];
  assert.equal(ads.length, 2);
  assert.ok(ads.every((a) => !a.matches(CARD_SELECTOR)));
});
