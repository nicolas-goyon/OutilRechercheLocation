/**
 * Lecture des données Leboncoin. Fonctions sans effet de bord (les tests utilisent des extraits réels).
 *
 * Structure observée en novembre 2026 (site Next.js) :
 *
 * Liste de résultats (/recherche?category=9&locations=…) :
 *
 *   <div data-qa-id="aditem_container">
 *     <a aria-label="Voir l’annonce" href="/ad/ventes_immobilieres/3296041144">…</a>
 *     <img src="https://img.leboncoin.fr/api/v1/lbcpb1/images/…jpg?rule=ad-image">
 *     <span>1 195 000 €</span>
 *     <p aria-label="Maison, 12 pièces, 321 mètres carrés.">Maison · 12 pièces · 321m²</p>
 *     <img alt="Classe énergie C"> <span>Terrain : 2146 m²</span>
 *     <p>Saint-Lary-Soulan 65170</p>
 *     <p data-qa-id="pro-store-name"><strong>AGENCE</strong></p>   (vendeur professionnel)
 *
 *   JSON de la 1re page : <script id="__NEXT_DATA__"> props.pageProps.searchData.ads[] (mêmes champs que
 *   le serveur, sans description). Page d'une annonce : props.pageProps.ad (avec la description).
 *   Les pages suivantes passent par un POST (api.leboncoin.fr/finder/search) : seules les cartes sont lues.
 *
 * Mêmes règles que le serveur (server/.../Sites/Leboncoin/LeboncoinListingParser.cs).
 */
import type { ListingData } from '../../core/types';
import { MAX_PHOTOS } from '../../core/types';
import { energyClass, featuresOf } from '../../shared/details';
import { extractRefs, normalizeSiren, normalizeText, parseNumber } from '../../shared/text';

export const ORIGIN = 'https://www.leboncoin.fr';
export const CARD_SELECTOR = '[data-qa-id="aditem_container"]';
export const DETAIL_TITLE_SELECTOR = '[data-qa-id="adview_title"]';

/** "/ad/ventes_immobilieres/3296041144" -> "3296041144". */
export function siteIdFromUrl(href: string): string | undefined {
  return new URL(href, ORIGIN).pathname.match(/^\/ad\/[a-z_]+\/(\d+)/)?.[1];
}

export function cardSiteId(card: Element): string | undefined {
  const href = card.querySelector('a[href*="/ad/"]')?.getAttribute('href');
  return href ? siteIdFromUrl(href) : undefined;
}

const TYPE_WORDS: [RegExp, string][] = [
  [/^maison/, 'house'],
  [/^appartement/, 'flat'],
  [/^terrain/, 'terrain'],
  [/^parking/, 'parking'],
];

/** Carte de la liste (le JSON de la page, quand il existe, complète ces données). */
export function parseLeboncoinCard(card: Element): ListingData {
  const href = card.querySelector('a[href*="/ad/"]')?.getAttribute('href');
  const texts = [...card.querySelectorAll('p, span')].map((e) => (e.textContent ?? '').replace(/\s+/g, ' ').trim()).filter(Boolean);
  // "Maison · 12 pièces · 321m²"
  const facts = texts.find((t) => t.includes('·'))?.split('·').map((s) => s.trim()) ?? [];
  const price = texts.find((t) => /^[\d\s]+€$/.test(t));
  const land = texts.find((t) => /^Terrain\s*:/.test(t));
  const place = texts.map((t) => t.match(/^(.+?)\s(\d{5})$/)).find((m) => m && !/^Située/.test(m[1]));
  const dpe = [...card.querySelectorAll('img[alt^="Classe énergie"]')].map((i) => i.getAttribute('alt'))[0]
    ?? card.innerHTML.match(/alt="(Classe énergie [A-G])"/)?.[1];
  const photos = [...new Set([...card.querySelectorAll('img[src^="https://img.leboncoin.fr/"]')]
    .map((i) => i.getAttribute('src') ?? '')
    .filter((u) => /rule=ad-/.test(u))
    .map((u) => u.replace(/rule=ad-image$/, 'rule=ad-large')))].slice(0, MAX_PHOTOS);
  const type = facts[0] ? TYPE_WORDS.find(([re]) => re.test(normalizeText(facts[0])))?.[1] : undefined;
  const store = card.querySelector('[data-qa-id="pro-store-name"]')?.textContent?.trim();
  return {
    url: href ? ORIGIN + new URL(href, ORIGIN).pathname : undefined,
    title: facts.length ? facts.join(' ').replace(/(\d)m²/, '$1 m²') : undefined,
    propertyType: type,
    price: price ? parseNumber(price) : undefined,
    rooms: number(facts.find((f) => /pièce/.test(f))),
    surface: number(facts.find((f) => /m²$/.test(f))),
    landSurface: land ? parseNumber(land.replace(/^Terrain\s*:/, '')) : undefined,
    energyClass: energyClass(dpe?.replace('Classe énergie', '')),
    city: place?.[1],
    postalCode: place?.[2],
    photos: photos.length ? photos : undefined,
    agencyName: store || undefined,
  };
}

function number(text: string | undefined): number | undefined {
  return text ? parseNumber(text.replace(/m²$/, '')) : undefined;
}

// ------------------------------------------------------------------ JSON (__NEXT_DATA__)

export interface LeboncoinAttribute {
  key?: string;
  value?: string;
  values?: string[];
  value_label?: string;
}

export interface LeboncoinAd {
  list_id?: number | string;
  url?: string;
  subject?: string;
  body?: string;
  category_id?: string;
  price?: number[] | number;
  first_publication_date?: string;
  images?: { urls?: string[]; urls_large?: string[] };
  location?: { city?: string; zipcode?: string; lat?: number; lng?: number; source?: string };
  owner?: { type?: string; name?: string; siren?: string };
  attributes?: LeboncoinAttribute[];
}

export interface LeboncoinNextData {
  props?: { pageProps?: { searchData?: { ads?: LeboncoinAd[] }; ad?: LeboncoinAd } };
}

/** Annonces de la page : liste (searchData.ads) ou fiche (ad). */
export function adsOfNextData(data: LeboncoinNextData | undefined): LeboncoinAd[] {
  const page = data?.props?.pageProps;
  return [...(page?.searchData?.ads ?? []), ...(page?.ad ? [page.ad] : [])];
}

export function adId(ad: LeboncoinAd): string | undefined {
  return ad.list_id === undefined || ad.list_id === null ? undefined : String(ad.list_id);
}

/** "1" maison, "2" appartement, "3" terrain, "4" parking, "5" autre. */
export function propertyTypeOf(value: string | undefined): string | undefined {
  return ({ '1': 'house', '2': 'flat', '3': 'terrain', '4': 'parking', '5': 'other' } as Record<string, string>)[value ?? ''];
}

export function parseLeboncoinAd(ad: LeboncoinAd): ListingData {
  const id = adId(ad);
  const attrs = new Map<string, LeboncoinAttribute>();
  for (const a of ad.attributes ?? []) if (a.key && !attrs.has(a.key)) attrs.set(a.key, a);
  const value = (k: string) => attrs.get(k)?.value;
  const label = (k: string) => attrs.get(k)?.value_label;
  const values = (k: string) => attrs.get(k)?.values ?? [];
  const int = (k: string) => {
    const v = value(k);
    return v !== undefined && /^-?\d+$/.test(v) ? Number(v) : undefined;
  };
  const positive = (k: string) => {
    const v = Number(value(k));
    return Number.isFinite(v) && v > 0 ? v : undefined;
  };

  const loc = ad.location;
  const reference = value('custom_ref');
  const description = ad.body || undefined;
  const otherRefs = extractRefs(description).filter((r) => r.toLowerCase() !== reference?.toLowerCase());
  const photos = (ad.images?.urls_large?.length ? ad.images.urls_large : (ad.images?.urls ?? [])).slice(0, MAX_PHOTOS);
  const outside = values('outside_access');
  const specific = values('specificities');
  const sellType = value('immo_sell_type');
  const bathrooms = int('nb_bathrooms');
  const showers = int('nb_shower_room');
  const year = int('building_year');
  const tax = int('property_tax');
  const heating = [label('heating_mode'), label('heating_type')].filter(Boolean).join(', ');
  const subtype = label('real_estate_type_specificities');
  const condition = value('global_condition');
  const price = Array.isArray(ad.price) ? ad.price[0] : ad.price;

  return {
    url: ad.url ?? (id ? `${ORIGIN}/ad/ventes_immobilieres/${id}` : undefined),
    title: ad.subject,
    transaction: ad.category_id === '9' ? 'buy' : ad.category_id === '10' ? 'rent' : undefined,
    propertyType: propertyTypeOf(value('real_estate_type')),
    price: typeof price === 'number' ? price : undefined,
    surface: positive('square'),
    rooms: int('rooms'),
    bedrooms: int('bedrooms'),
    floor: int('floor_number'),
    furnished: value('furnished') === '1' ? true : value('furnished') === '2' ? false : undefined,
    postalCode: loc?.zipcode,
    city: loc?.city,
    geo: typeof loc?.lat === 'number' && typeof loc.lng === 'number'
      ? { lat: loc.lat, lon: loc.lng, precisionM: loc.source === 'city' ? 1500 : 150 }
      : undefined,
    agencyRef: reference,
    otherRefs: otherRefs.length ? otherRefs : undefined,
    agencyName: ad.owner?.type === 'pro' ? (ad.owner.name ?? value('store_name')) : undefined,
    agencySiren: normalizeSiren(ad.owner?.siren),
    photos: photos.length ? photos : undefined,
    descriptionExcerpt: description ? normalizeText(description).slice(0, 600) : undefined,
    publishedAt: ad.first_publication_date?.replace(' ', 'T'),
    landSurface: positive('land_plot_surface'),
    energyClass: energyClass(value('energy_rate')),
    ghgClass: energyClass(value('ges')),
    yearBuilt: year !== undefined && year > 1000 ? year : undefined,
    bathrooms: bathrooms === undefined && showers === undefined ? undefined : (bathrooms ?? 0) + (showers ?? 0),
    heating: heating || undefined,
    exposure: label('orientation'),
    subtype: subtype && subtype !== 'Autre' ? subtype : undefined,
    newBuild: sellType === 'new' ? true : sellType === 'old' ? false : undefined,
    workNeeded: condition === '4' || condition === '5' ? true : ['1', '2', '3'].includes(condition ?? '') ? false : undefined,
    saleKind: sellType === 'viager' ? 'life-annuity' : undefined,
    propertyTax: tax !== undefined && tax > 0 ? tax : undefined,
    features: featuresOf([
      ['garden', outside.includes('garden') || undefined],
      ['pool', outside.includes('pool') || undefined],
      ['terrace', outside.includes('terrace') || undefined],
      ['balcony', outside.includes('balcony') || undefined],
      ['cellar', specific.includes('cellar') || undefined],
      ['airConditioning', specific.includes('air_conditioned') || undefined],
      ['parking', specific.includes('with_garage_or_parking_spot') || (int('nb_parkings') ?? 0) > 0 || undefined],
      ['elevator', value('elevator') === '1' ? true : value('elevator') === '2' ? false : undefined],
    ]),
  };
}

/** Fiche (DOM) : titre, prix et description ; le JSON de la page donne le reste. */
export function parseLeboncoinDetailDom(root: ParentNode, href: string): ListingData {
  const title = root.querySelector(DETAIL_TITLE_SELECTOR)?.textContent?.trim();
  const price = root.querySelector('[data-qa-id="adview_price"]')?.textContent;
  const description = root.querySelector('[data-qa-id="adview_description_container"]')?.textContent?.trim();
  const id = siteIdFromUrl(href);
  return {
    url: id ? `${ORIGIN}${new URL(href, ORIGIN).pathname}` : undefined,
    title: title || undefined,
    price: price ? parseNumber(price) : undefined,
    descriptionExcerpt: description ? normalizeText(description).slice(0, 600) : undefined,
  };
}
