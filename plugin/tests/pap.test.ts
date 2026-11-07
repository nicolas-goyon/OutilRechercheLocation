import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { parseHTML } from 'linkedom';
import { CARD_SELECTOR, cardSiteId, frenchDate, parsePapCard, parsePapDetail, siteIdFromUrl, typeOfPath } from '../src/sites/pap/parse';

const fixture = (name: string) => readFileSync(new URL(`./fixtures/${name}`, import.meta.url), 'utf8');
const list = parseHTML(`<html><body>${fixture('pap-liste.html')}</body></html>`).document;
const cards = [...list.querySelectorAll(CARD_SELECTOR)];

test('PAP : cartes, encarts ignorés', () => {
  assert.deepEqual(cards.map((c) => cardSiteId(c)), ['464301065', undefined, '463300632']);
  assert.equal(siteIdFromUrl('https://www.pap.fr/annonces/maison-ibos-r464301065'), '464301065');
});

test('PAP : annonce « à proximité » ignorée', () => {
  const { document } = parseHTML(`<html><body><div class="search-list-item-alt"><a class="item-title" href="/annonces/maison-x-r1" name="1"></a>
    <p class="item-description" data-gaq="{&quot;action&quot;:&quot;Consulter proximite&quot;}"></p></div></body></html>`);
  assert.equal(cardSiteId(document.querySelector(CARD_SELECTOR)!), undefined);
});

test('PAP : parse une carte', () => {
  const d = parsePapCard(cards[0]);
  assert.equal(d.url, 'https://www.pap.fr/annonces/maison-ibos-r464301065');
  assert.equal(d.title?.replace(/\u00a0/g, ' '), 'Maison 6 pièces 155 m²');
  assert.equal(d.propertyType, 'house');
  assert.equal(d.price, 239_000);
  assert.deepEqual([d.rooms, d.bedrooms, d.surface], [6, 4, 155]);
  assert.deepEqual([d.city, d.postalCode], ['Ibos', undefined]);
  assert.equal(d.energyClass, 'D');
  assert.equal(d.photos?.[0], 'https://cdn.pap.fr/photos/pap/5d/e1/5de14bee3f3e2da905c7a9b8e87bec41/5-p1.jpg');
  assert.match(d.descriptionExcerpt ?? '', /^situee dans un quartier calme/);

  const second = parsePapCard(cards[2]);
  assert.deepEqual([second.city, second.postalCode, second.price], ['Poueyferré', '65100', 335_000]);
});

test('PAP : fiche', () => {
  const { document } = parseHTML(`<html><body>${fixture('pap-fiche.html')}</body></html>`);
  const d = parsePapDetail(document, 'https://www.pap.fr/annonces/maison-ibos-r464301065');
  assert.deepEqual([d.postalCode, d.city], ['65420', 'Ibos']);
  assert.equal(d.landSurface, 500);
  assert.deepEqual([d.energyClass, d.ghgClass], ['D', 'D']);
  assert.equal(d.publishedAt, '2026-11-06');
  assert.equal(d.agencyRef, 'G43/1065');
  assert.equal(d.agencyName, undefined);
  assert.equal(d.propertyTax, 1600);
  assert.equal(d.photos?.length, 3);
  assert.match(d.descriptionExcerpt ?? '', /terrasse en beton/);
});

test('PAP : type et date', () => {
  assert.equal(typeOfPath('/annonces/appartement-tarbes-65000-r1'), 'flat');
  assert.equal(typeOfPath('/annonces/grange-x-r3'), 'other');
  assert.equal(frenchDate('6 février 2026'), '2026-02-06');
  assert.equal(frenchDate('hier'), undefined);
});
