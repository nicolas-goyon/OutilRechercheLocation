import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { extractRefs, extractSiren, normalizeSiren } from '../src/shared/text';
import { parseBieniciApiAd } from '../src/sites/bienici/parse';
import { parseSelogerDetailClassified, parseSelogerSerpClassified } from '../src/sites/seloger/parse';

const fixture = (name: string) => JSON.parse(readFileSync(new URL(`./fixtures/${name}`, import.meta.url), 'utf8'));

test('références écrites dans un texte', () => {
  assert.deepEqual(extractRefs('Bel appartement. Réf. : LA2100-REGOURD12'), ['LA2100-REGOURD12']);
  assert.deepEqual(extractRefs('Référence annonce : 1653L1653 — Mandat n° 2024-118.'), ['1653L1653', '2024-118']);
  assert.deepEqual(extractRefs('Réf. de l’annonce : 28251'), ['28251']);
  assert.deepEqual(extractRefs('<b>Ref du bien</b> abc123<br>'), ['ABC123']);
});

test('références : pas de faux positifs', () => {
  assert.deepEqual(extractRefs('Cuisine avec réfrigérateur, entièrement refaite en 2024. Référence du DPE : C.'), []);
  assert.deepEqual(extractRefs('Garage n°5, cave n°8, référence 2024'), []);
  assert.deepEqual(extractRefs(undefined), []);
});

test('SIREN de l\'agence (mentions légales)', () => {
  assert.equal(extractSiren('<b>RCS:</b> 325539286'), '325539286');
  assert.equal(extractSiren('<b>SIRET:</b> 32553928600105'), '325539286');
  assert.equal(extractSiren('SARL au capital de 50 000 € - RCS Rodez 407 797 521'), '407797521');
  assert.equal(extractSiren('Carte professionnelle CPI12022019000042567'), undefined);
  assert.equal(extractSiren('RCS 123456789'), undefined); // clé de Luhn invalide
  assert.equal(normalizeSiren('407 797 521'), '407797521');
});

test('Bien\'ici : agence et SIREN dans realEstateAd.json (page d\'une annonce)', () => {
  const d = parseBieniciApiAd(fixture('bienici-api-ad-detail.json'));
  assert.equal(d.agencyRef, '28251');
  assert.equal(d.agencyName, 'CENTURY 21 Foch Immobilier');
  assert.equal(d.agencySiren, '407797521');
  assert.deepEqual(d.otherRefs, ['28251-C21']);
});

test('SeLoger : SIREN de l\'agence (liste et page d\'une annonce), références de la description', () => {
  const serp = parseSelogerSerpClassified(fixture('seloger-serp-classified.json'));
  assert.equal(serp.agencySiren, '328921432');
  const detail = parseSelogerDetailClassified(fixture('seloger-detail-classified.json'));
  assert.equal(detail.agencySiren, '328921432');
  assert.equal(detail.agencyRef, 'P181036-1642-24058');
  assert.deepEqual(detail.otherRefs, ['YFR-BDX-0457', '2024-118']);
});
