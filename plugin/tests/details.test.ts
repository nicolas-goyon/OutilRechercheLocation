import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { bathroomsFromText, energyClass, featuresFromText, featuresOf } from '../src/shared/details';
import { parseBieniciApiAd } from '../src/sites/bienici/parse';
import {
  parseSelogerDetailClassified,
  parseSelogerSerpClassified,
  selogerNewBuild,
  selogerSubtype,
  type SelogerDetailClassified,
} from '../src/sites/seloger/parse';

const fixture = (name: string) => readFileSync(new URL(`./fixtures/${name}`, import.meta.url), 'utf8');

test('classes énergie : lettre A à G, sinon rien', () => {
  assert.equal(energyClass('d'), 'D');
  assert.equal(energyClass('Performance énergétique : G'), 'G');
  assert.equal(energyClass('NS'), undefined);
  assert.equal(energyClass(undefined), undefined);
});

test('équipements écrits en toutes lettres, "Pas de …" = absent', () => {
  assert.deepEqual(featuresFromText(['Jardin', 'Pas de cave', 'Garage, Parking', 'Cuisine ouverte', '3 Chambres']), {
    garden: true,
    cellar: false,
    garage: true,
    parking: true,
  });
  assert.equal(featuresFromText(['Cuisine ouverte']), undefined);
  assert.deepEqual(featuresOf([['pool', undefined], ['garden', false], ['garden', true]]), { garden: true });
});

test('salles de bain et salles d\'eau additionnées', () => {
  assert.equal(bathroomsFromText('1 Salle de bain; 1 Salle de douche'), 2);
  assert.equal(bathroomsFromText("2 salles d'eau"), 2);
  assert.equal(bathroomsFromText('Cuisine'), undefined);
});

test("Bien'ici : terrain, DPE, équipements et viager (mêmes règles que le serveur)", () => {
  const d = parseBieniciApiAd({
    id: 'ag1-42',
    adType: 'lifeAnnuitySale',
    propertySubtype: 'barn',
    landSurfaceArea: 425,
    energyClassification: 'E',
    greenhouseGazClassification: 'd',
    yearOfConstruction: 1800,
    bathroomsQuantity: 1,
    showerRoomsQuantity: 1,
    heating: '7',
    workToDo: true,
    hasGarden: true,
    hasPool: false,
    garagesQuantity: 1,
    parkingPlacesQuantity: 0,
  });
  assert.equal(d.transaction, 'buy');
  assert.equal(d.saleKind, 'life-annuity');
  assert.equal(d.subtype, 'Grange');
  assert.equal(d.landSurface, 425);
  assert.deepEqual([d.energyClass, d.ghgClass], ['E', 'D']);
  assert.equal(d.yearBuilt, 1800);
  assert.equal(d.bathrooms, 2);
  assert.equal(d.heating, undefined); // code numérique, pas un libellé
  assert.equal(d.workNeeded, true);
  assert.deepEqual(d.features, { garden: true, pool: false, garage: true, parking: false });
});

test('SeLoger, liste : terrain, DPE, année, sous-type, neuf, viager', () => {
  const d = parseSelogerSerpClassified({
    id: 'x',
    hardFacts: { title: 'Maison à vendre • neuf', facts: [{ type: 'plotSpace', splitValue: '750' }] },
    energyClass: 'D',
    legacyTracking: { year_of_construction: 2027 },
    rawData: { distributionType: 'BUY', propertyType: 'HOUSE', propertySubType: 'CHALET' },
  });
  assert.equal(d.landSurface, 750);
  assert.equal(d.energyClass, 'D');
  assert.equal(d.yearBuilt, 2027);
  assert.equal(d.subtype, 'Chalet');
  assert.equal(d.newBuild, true);
  const viager = parseSelogerSerpClassified({ id: 'v', hardFacts: { title: 'Maison en viager • occupé' } });
  assert.equal(viager.saleKind, 'life-annuity');
  assert.equal(viager.newBuild, false);
});

test('SeLoger, fiche : DPE, GES, chauffage, exposition, salles de bain et équipements', () => {
  const c = JSON.parse(fixture('seloger-detail-house.json')) as SelogerDetailClassified;
  const d = parseSelogerDetailClassified(c, 'https://www.seloger.com/annonce/achat/x/hendaye-64700/262XRHGFRHPC');
  assert.deepEqual([d.energyClass, d.ghgClass], ['D', 'B']);
  assert.equal(d.heating, 'Chauffage individuel, Électrique');
  assert.equal(d.exposure, 'sud, est');
  assert.equal(d.landSurface, 750);
  assert.equal(d.bathrooms, 2);
  assert.equal(d.newBuild, false);
  assert.deepEqual(d.features, { balcony: false, garden: true, garage: false, pool: true });
});

test('SeLoger : sous-types et neuf', () => {
  assert.equal(selogerSubtype('MANOR_HOUSE'), 'Manoir');
  assert.equal(selogerSubtype('HOUSEBOAT'), 'Houseboat');
  assert.equal(selogerSubtype(null), undefined);
  assert.equal(selogerNewBuild('Maison', 'NEW_BUILD_UNIT'), true);
  assert.equal(selogerNewBuild('Maison à vendre'), false);
  assert.equal(selogerNewBuild(undefined), undefined);
});
