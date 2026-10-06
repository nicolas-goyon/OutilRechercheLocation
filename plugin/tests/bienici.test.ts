import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { parseHTML } from 'linkedom';
import {
  AD_SELECTORS,
  CARD_SELECTOR,
  DETAIL_SELECTOR,
  detailSiteId,
  parseAddress,
  parseBieniciApiAd,
  parseBieniciCard,
  parseBieniciDetail,
  parseTitle,
  photoKey,
} from '../src/sites/bienici/parse';

const html = readFileSync(new URL('./fixtures/bienici-cards.html', import.meta.url), 'utf8');
const { document } = parseHTML(`<html><body>${html}</body></html>`);
const cards = [...document.querySelectorAll(CARD_SELECTOR)];

test('trouve les cartes, mises en avant comprises', () => {
  assert.deepEqual(
    cards.map((c) => c.getAttribute('data-id')),
    ['ag751558-552433229', 'hektor-sia-immo-2342', 'gedeon-33564802'],
  );
});

test('parse une carte Bien\'ici', () => {
  const d = parseBieniciCard(cards[1]);
  assert.equal(d.title, 'Appartement 3 pièces 88 m²');
  assert.equal(d.price, 1866);
  assert.equal(d.surface, 88);
  assert.equal(d.rooms, 3);
  assert.equal(d.propertyType, 'flat');
  assert.equal(d.postalCode, '75019');
  assert.equal(d.city, 'Paris 19e');
  assert.equal(d.district, 'Bassin de la Villette');
  assert.equal(d.transaction, 'rent');
  assert.equal(d.url, 'https://www.bienici.com/annonce/location/paris-19e/appartement/3pieces/hektor-sia-immo-2342');
  assert.ok(d.photos?.[0]?.startsWith('https://file.bienici.com/photo/hektor-sia-immo-2342'));
  assert.ok(!d.photos?.[0]?.includes('?'));
  assert.ok(d.descriptionExcerpt?.startsWith('exclusivite sia immobilier'));
});

test('meublé détecté depuis le titre', () => {
  assert.equal(parseBieniciCard(cards[2]).furnished, true);
  assert.deepEqual(parseTitle('Studio 18 m²'), { propertyType: 'flat', rooms: 1, surface: 18 });
  assert.deepEqual(parseTitle('Maison 5 pièces 120,5 m²'), { propertyType: 'house', rooms: 5, surface: 120.5 });
});

test('adresse sans quartier / non reconnue', () => {
  assert.deepEqual(parseAddress('69003 Lyon 3e'), { postalCode: '69003', city: 'Lyon 3e', district: undefined });
  assert.deepEqual(parseAddress('Quelque part'), { city: 'Quelque part' });
});

test('parse le JSON API (réf. agence, géoloc, photos d\'origine)', () => {
  const ad = JSON.parse(readFileSync(new URL('./fixtures/bienici-api-ad.json', import.meta.url), 'utf8'));
  const d = parseBieniciApiAd(ad);
  assert.equal(d.agencyRef, 'TSLAP320002342');
  assert.equal(d.surface, 88.32);
  assert.equal(d.floor, 1);
  assert.equal(d.furnished, false);
  assert.equal(d.district, 'Bassin de la Villette');
  assert.deepEqual(d.geo, { lat: 48.890093725013195, lon: 2.376918913801298, precisionM: 50 });
  assert.deepEqual(d.photoKeys, ['photo_3c9c36dc663b54820545f52f6aef3a1e', 'photo_0a2fdb5cd3d88ed785ca666f2370f74a']);
});

test('photoKey ignore les noms génériques', () => {
  assert.equal(photoKey('https://photos.ubiflow.net/751558/552433229/photos/1.jpg'), undefined);
  assert.equal(photoKey('https://x/image0001.jpg'), undefined);
  assert.equal(photoKey('https://media.studio-net.fr/biens/33564802/6a5662c07a9db'), '6a5662c07a9db');
});

// ------------------------------------------------------------------ page d'un bien

const detailHtml = readFileSync(new URL('./fixtures/bienici-detail.html', import.meta.url), 'utf8');
const detailDoc = parseHTML(`<html><head><link rel="canonical" href="https://www.bienici.com/annonce/location/onet-le-chateau/appartement/2pieces/century-21-202_2190_28251"></head><body>${detailHtml}</body></html>`).document;

test('page d\'un bien : identifiant et données de la fiche', () => {
  const section = detailDoc.querySelector(DETAIL_SELECTOR)!;
  assert.ok(section);
  assert.equal(detailSiteId(section), 'century-21-202_2190_28251');
  const d = parseBieniciDetail(section);
  assert.equal(d.title, 'Appartement 2 pièces 49 m²');
  assert.equal(d.propertyType, 'flat');
  assert.equal(d.rooms, 2);
  assert.equal(d.surface, 49);
  assert.equal(d.price, 605);
  assert.equal(d.transaction, 'rent');
  assert.equal(d.postalCode, '12850');
  assert.equal(d.city, 'Onet-le-Château');
  assert.equal(d.agencyRef, '28251');
  assert.equal(d.url, 'https://www.bienici.com/annonce/location/onet-le-chateau/appartement/2pieces/century-21-202_2190_28251');
  assert.equal(d.photos?.length, 2);
  assert.ok(!d.photos?.[0]?.includes('?'));
  assert.ok(d.descriptionExcerpt?.startsWith('onet le chateau secteur les balquieres'));
});

test('emplacements de pub : sélecteurs présents dans la liste réelle', () => {
  const { document } = parseHTML(`<html><body><div class="search-results-list">
    <article class="ad-overview" data-id="a"></article>
    <div class="advertisement-container search-results-list__commercial-ad search-results-list__commercial-ad--map"><iframe></iframe></div>
    <article class="ad-overview" data-id="b"></article></div></body></html>`);
  const ads = document.querySelectorAll(AD_SELECTORS.join(','));
  assert.equal(ads.length, 1);
  assert.ok([...document.querySelectorAll('article')].every((a) => !a.matches(AD_SELECTORS.join(','))));
});
