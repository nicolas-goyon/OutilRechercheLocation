/**
 * Lecture des données SeLoger. Fonctions sans effet de bord (les tests utilisent des extraits réels).
 *
 * Structure observée en octobre 2026 (site React, "classified-search") :
 *
 * Liste de résultats :
 *
 *   <div id="classified-card-262UMHKCWPS4" data-testid="serp-core-classified-card-testid">
 *     <a data-testid="card-mfe-covering-link-testid"
 *        href="https://www.seloger.com/annonce/location/<région>/<dpt>/<ville>/262UMHKCWPS4?serp_view=list&search=..."
 *        title="Appartement à louer - Bordeaux - 949,02 €/mois - 3 pièces, 2 chambres, 57,2 m², 7ème étage">
 *     <img src="https://cdnihddipa.cloudimg.io/2/d/e/b/<uuid>.jpg?ci_seal=...&w=626&h=470">
 *     <div data-testid="cardmfe-price-testid">949,02 €/mois</div>
 *     <div>Appartement à louer</div>
 *     <div data-testid="cardmfe-keyfacts-testid"><div>3 pièces</div><div>·</div><div>57,2 m²</div>…</div>
 *     <div data-testid="cardmfe-description-box-address">Bordeaux Sud, Bordeaux (33800)</div>
 *     <div data-testid="cardmfe-description-text-test-id"><div><hr></div><div>…description…</div></div>
 *
 *   Données JSON de la même liste :
 *     - 1re page : window.__UFRN_FETCHER__.data['classified-serp-init-data'].pageProps.classifiedsData[id]
 *     - pages suivantes (SPA) : GET /classifiedList/<id>,<id>,... -> tableau des mêmes objets.
 *
 *   Emplacements de pub dans la liste : [data-testid="serp-inline-ad"], [data-testid="serp-banner-ad"].
 *
 * Page d'une annonce (https://www.seloger.com/annonce/location/.../262UMHKCWPS4) :
 *
 *   <main data-testid="aviv.CDP.main"> … <h1 data-testid="cdp-seo-wrapper">…</h1>
 *   JSON : window.__UFRN_LIFECYCLE_SERVERREQUEST__.app_cldp.data.classified
 *     (GPS, référence de l'annonce, description complète, charges).
 *
 * Les photos SeLoger sont des copies renommées (UUID). Leur nom ne sert donc
 * pas à trouver un doublon sur un autre site : pas de photoKeys.
 */
import type { ListingData, TransactionType } from '../../core/types';
import { normalizeText, parseNumber } from '../../shared/text';

export const CARD_SELECTOR = '[data-testid="serp-core-classified-card-testid"]';
export const DETAIL_SELECTOR = '[data-testid="aviv.CDP.main"]';
export const AD_SELECTORS = [
  '[data-testid="serp-inline-ad"]',
  '[data-testid="serp-banner-ad"]',
  '[data-testid="ad-sense-test"]',
  '[id^="afscontainer"]',
];

/** "classified-card-262UMHKCWPS4" -> "262UMHKCWPS4". */
export function cardSiteId(card: Element): string | undefined {
  const fromId = card.id.match(/^classified-card-(.+)$/)?.[1];
  if (fromId) return fromId;
  const href = card.querySelector('a[data-testid="card-mfe-covering-link-testid"]')?.getAttribute('href');
  return href ? siteIdFromUrl(href) : undefined;
}

/** "/annonce/location/.../262UMHKCWPS4?..." -> "262UMHKCWPS4". */
export function siteIdFromUrl(href: string): string | undefined {
  const path = new URL(href, 'https://www.seloger.com').pathname;
  if (!path.startsWith('/annonce/')) return undefined;
  return path.split('/').filter(Boolean).pop()?.replace(/\.htm$/, '') || undefined;
}

/** URL sans paramètres de recherche ni ancre. */
export function canonicalUrl(href: string): string {
  const u = new URL(href, 'https://www.seloger.com');
  return u.origin + u.pathname;
}

/** Retire les paramètres de taille (&w=626&h=470). Garde ci_seal : le CDN refuse une URL sans signature. */
export function stripPhotoSize(url: string): string {
  const u = new URL(url);
  for (const k of ['w', 'h', 'width', 'height', 'func']) u.searchParams.delete(k);
  return u.toString();
}

export function transactionFromUrl(href: string | undefined): TransactionType | undefined {
  if (!href) return undefined;
  const path = new URL(href, 'https://www.seloger.com').pathname;
  if (/^\/annonce\/location\//.test(path)) return 'rent';
  if (/^\/annonce\/(achat|vente)\//.test(path)) return 'buy';
  return undefined;
}

const TYPE_WORDS: [RegExp, string][] = [
  [/\b(maison|villa|pavillon|propriete|ferme|mas)\b/, 'house'],
  [/\b(appartement|studio|duplex|triplex|chambre)\b/, 'flat'],
  [/\bloft\b/, 'loft'],
  [/\b(chateau|manoir)\b/, 'castle'],
  [/\bhotel particulier\b/, 'townhouse'],
  [/\bterrain\b/, 'terrain'],
  [/\b(parking|box|garage)\b/, 'parking'],
];

/** Type de bien depuis un libellé ("Appartement à louer", "Duplex Maison à plusieurs étages à louer"). */
export function propertyTypeFromText(text: string | undefined): string | undefined {
  if (!text) return undefined;
  const t = normalizeText(text);
  // Ordre de TYPE_WORDS : "Duplex Maison à plusieurs étages" est une maison.
  for (const [re, type] of TYPE_WORDS) if (re.test(t)) return type;
  return undefined;
}

/** Valeurs de rawData.propertyType (JSON SeLoger) vers les types communs du plugin. */
const RAW_TYPES: Record<string, string> = {
  APARTMENT: 'flat',
  FLAT: 'flat',
  STUDIO: 'flat',
  HOUSE: 'house',
  VILLA: 'house',
  LOFT: 'loft',
  CASTLE: 'castle',
  TOWNHOUSE: 'townhouse',
  LAND: 'terrain',
  PLOT: 'terrain',
  PARKING: 'parking',
  GARAGE: 'parking',
};

/** "7ème étage" -> 7. "Étage 1/1" -> 1. "1er étage" -> 1. "Rez-de-chaussée" -> 0. */
export function parseFloor(text: string | undefined): number | undefined {
  if (!text) return undefined;
  const t = normalizeText(text);
  if (/rez de chaussee|\brdc\b/.test(t)) return 0;
  const m = t.match(/(\d+)\s*(?:e|er|eme|ere)?\s*etage/) ?? t.match(/etage\s*(\d+)/);
  return m ? Number(m[1]) : undefined;
}

/** Faits clés ("3 pièces", "2 chambres", "57,2 m²", "7ème étage", "Meublé") -> champs. */
export function parseKeyfacts(facts: string[]): ListingData {
  const out: ListingData = {};
  for (const raw of facts) {
    const f = raw.replace(/[  ]/g, ' ').trim();
    const t = normalizeText(f);
    if (/^\d+\s*pieces?$/.test(t) || /^studio$/.test(t)) out.rooms = t === 'studio' ? 1 : parseNumber(f);
    else if (/^\d+\s*chambres?$/.test(t)) out.bedrooms = parseNumber(f);
    else if (/m²|m2$/.test(f)) out.surface = parseNumber(f);
    else if (/etage|rez de chaussee/.test(t)) out.floor = parseFloor(f);
    else if (/^meuble$/.test(t)) out.furnished = true;
    else if (/^non meuble$/.test(t)) out.furnished = false;
  }
  return out;
}

/** "Bordeaux Sud, Bordeaux (33800)" -> quartier, ville, CP. "Bordeaux (33000)" -> ville, CP. */
export function parseAddress(address: string | undefined): ListingData {
  if (!address) return {};
  const a = address.replace(/[  ]/g, ' ').replace(/\s+/g, ' ').trim();
  const m = a.match(/^(?:(.+),\s*)?([^,]+?)\s*\((\d{5})\)$/);
  if (!m) return { city: a };
  return { postalCode: m[3], city: m[2].trim(), district: m[1]?.trim() || undefined };
}

// ------------------------------------------------------------------ carte HTML

export function parseSelogerCard(card: Element): ListingData {
  const text = (sel: string) => card.querySelector(sel)?.textContent?.replace(/\s+/g, ' ').trim() || undefined;
  const link = card.querySelector<HTMLAnchorElement>('a[data-testid="card-mfe-covering-link-testid"]');
  const href = link?.getAttribute('href') ?? undefined;
  const keyfactsEl = card.querySelector('[data-testid="cardmfe-keyfacts-testid"]');
  const facts = keyfactsEl
    ? [...keyfactsEl.children].map((c) => c.textContent?.trim() ?? '').filter((f) => f && f !== '·')
    : [];
  // Le libellé ("Appartement à louer") est juste avant les faits clés.
  const heading = keyfactsEl?.previousElementSibling?.textContent?.trim() || link?.getAttribute('title')?.split(' - ')[0]?.trim();
  const price = text('[data-testid="cardmfe-price-testid"]');
  const photo = card.querySelector<HTMLImageElement>('[data-testid="card-mfe-picture-box-gallery-test-id"] img')?.getAttribute('src');
  const descBox = card.querySelector('[data-testid="cardmfe-description-text-test-id"]');
  const description = descBox?.lastElementChild?.textContent ?? undefined;
  const city = parseAddress(text('[data-testid="cardmfe-description-box-address"]'));
  const kf = parseKeyfacts(facts);

  return {
    ...kf,
    ...city,
    title: buildTitle(heading, kf),
    propertyType: propertyTypeFromText(heading),
    price: parseNumber(price),
    url: href ? canonicalUrl(href) : undefined,
    transaction: transactionFromUrl(href) ?? (/\blouer\b/i.test(heading ?? '') ? 'rent' : /\bvendre\b/i.test(heading ?? '') ? 'buy' : undefined),
    furnished: kf.furnished ?? (/\bmeubl[ée]/i.test(heading ?? '') ? true : undefined),
    photos: photo?.startsWith('http') ? [stripPhotoSize(photo)] : undefined,
    descriptionExcerpt: description ? normalizeText(description).slice(0, 600) : undefined,
  };
}

/** "Appartement à louer" + 3 pièces 57,2 m² -> "Appartement 3 pièces 57,2 m²" (même forme que Bien'ici). */
function buildTitle(heading: string | undefined, kf: ListingData): string | undefined {
  if (!heading) return undefined;
  const base = heading.replace(/\s+à\s+(louer|vendre)\b.*$/i, '').trim();
  const parts = [base];
  if (kf.rooms) parts.push(`${kf.rooms} pièce${kf.rooms > 1 ? 's' : ''}`);
  if (kf.surface) parts.push(`${String(kf.surface).replace('.', ',')} m²`);
  return parts.join(' ');
}

// ------------------------------------------------------------------ JSON de la liste

/** Champs utiles d'une annonce de la liste (classifiedsData / classifiedList). */
export interface SelogerSerpClassified {
  id: string;
  url?: string;
  metadata?: { creationDate?: string };
  location?: { address?: { city?: string; zipCode?: string; district?: string } };
  hardFacts?: {
    title?: string;
    keyfacts?: string[];
    facts?: { type?: string; value?: string; splitValue?: string }[];
  };
  gallery?: { images?: { url?: string }[] };
  mainDescription?: { description?: string; headline?: string };
  provider?: { intermediaryCard?: { title?: string | null } };
  rawData?: {
    distributionType?: string;
    propertyType?: string;
    price?: number;
    nbroom?: number;
    nbbedroom?: number;
    surface?: { main?: number | null };
    offererMarketingKey?: string;
  };
}

export function parseSelogerSerpClassified(c: SelogerSerpClassified): ListingData {
  const raw = c.rawData ?? {};
  const kf = parseKeyfacts(c.hardFacts?.keyfacts ?? []);
  const addr = c.location?.address;
  const photos = (c.gallery?.images ?? []).map((i) => i.url).filter((u): u is string => !!u).slice(0, 4);
  return {
    ...kf,
    url: c.url ? canonicalUrl(c.url) : undefined,
    title: buildTitle(c.hardFacts?.title, { rooms: raw.nbroom ?? kf.rooms, surface: raw.surface?.main ?? kf.surface }),
    transaction: distribution(raw.distributionType) ?? transactionFromUrl(c.url),
    propertyType: (raw.propertyType && RAW_TYPES[raw.propertyType]) || propertyTypeFromText(c.hardFacts?.title),
    price: raw.price ?? undefined,
    surface: raw.surface?.main ?? kf.surface,
    rooms: raw.nbroom ?? kf.rooms,
    bedrooms: raw.nbbedroom ?? kf.bedrooms,
    postalCode: addr?.zipCode,
    city: addr?.city,
    district: addr?.district,
    agencyRef: raw.offererMarketingKey || undefined,
    agencyName: c.provider?.intermediaryCard?.title || undefined,
    photos: photos.length ? photos : undefined,
    descriptionExcerpt: c.mainDescription?.description ? normalizeText(c.mainDescription.description).slice(0, 600) : undefined,
    publishedAt: c.metadata?.creationDate,
  };
}

function distribution(d: string | undefined): TransactionType | undefined {
  if (!d) return undefined;
  if (/^RENT/i.test(d)) return 'rent';
  if (/^(BUY|SALE|SELL)/i.test(d)) return 'buy';
  return undefined;
}

// ------------------------------------------------------------------ JSON de la page d'une annonce

/** Champs utiles de __UFRN_LIFECYCLE_SERVERREQUEST__.app_cldp.data.classified. */
export interface SelogerDetailClassified {
  id: string;
  metadata?: { creationDate?: string };
  domains?: { medias?: { images?: { url?: string }[] } };
  sections?: {
    location?: {
      address?: { city?: string; zipCode?: string; district?: string };
      geometry?: { type?: string; coordinates?: [number, number] };
    };
    description?: { description?: string };
    hardFacts?: { title?: string; keyfacts?: string[]; price?: { ariaLabel?: string; value?: string } };
    key?: { keys?: { label?: string; value?: string }[] };
    price?: { components?: PriceComponent[] };
    features?: { details?: { categories?: { elements?: { icon?: string; value?: string }[] }[] } };
  };
  contactSections?: { contactCard?: { title?: string | null } };
  rawData?: { distributionType?: string; propertyType?: string };
  tracking?: { av_items?: { price?: number }[] };
}

interface PriceEntry {
  label?: { main?: string } | string;
  value?: { main?: { value?: string; ariaLabel?: string } };
}
interface PriceComponent {
  units?: { main?: { price?: PriceEntry }; details?: { prices?: PriceEntry[] }[] }[];
}

/** Précision supposée du point GPS de la fiche (adresse publiée ou centre du quartier). */
const DETAIL_GEO_PRECISION_M = 150;

export function parseSelogerDetailClassified(c: SelogerDetailClassified, pageUrl?: string): ListingData {
  const s = c.sections ?? {};
  const kf = parseKeyfacts(s.hardFacts?.keyfacts ?? []);
  const addr = s.location?.address;
  const coords = s.location?.geometry?.type === 'Point' ? s.location.geometry.coordinates : undefined;
  const ref = s.key?.keys?.find((k) => /r[ée]f[ée]rence/i.test(k.label ?? ''))?.value;
  const prices = (s.price?.components ?? []).flatMap((comp) =>
    (comp.units ?? []).flatMap((u) => [u.main?.price, ...(u.details ?? []).flatMap((d) => d.prices ?? [])]),
  ).filter((p): p is PriceEntry => !!p);
  const labelOf = (p: PriceEntry) => (typeof p.label === 'string' ? p.label : p.label?.main) ?? '';
  const amount = (p: PriceEntry | undefined) => parseNumber(p?.value?.main?.ariaLabel ?? p?.value?.main?.value);
  const rent = prices.find((p) => /^loyer/i.test(labelOf(p)));
  const charges = prices.find((p) => /charges/i.test(labelOf(p)) && !/^loyer/i.test(labelOf(p)));
  const furnishedValue = (s.features?.details?.categories ?? [])
    .flatMap((cat) => cat.elements ?? [])
    .find((e) => e.icon === 'furnished')?.value;
  const photos = (c.domains?.medias?.images ?? []).map((i) => i.url).filter((u): u is string => !!u).slice(0, 4);

  return {
    ...kf,
    url: pageUrl ? canonicalUrl(pageUrl) : undefined,
    title: buildTitle(s.hardFacts?.title, kf),
    transaction: distribution(c.rawData?.distributionType) ?? transactionFromUrl(pageUrl),
    propertyType: (c.rawData?.propertyType && RAW_TYPES[c.rawData.propertyType]) || propertyTypeFromText(s.hardFacts?.title),
    price: amount(rent) ?? parseNumber(s.hardFacts?.price?.ariaLabel) ?? c.tracking?.av_items?.[0]?.price,
    charges: amount(charges),
    furnished: furnishedValue ? !/^non\b/i.test(normalizeText(furnishedValue)) : kf.furnished,
    postalCode: addr?.zipCode,
    city: addr?.city,
    district: addr?.district,
    geo: coords ? { lat: coords[1], lon: coords[0], precisionM: DETAIL_GEO_PRECISION_M } : undefined,
    agencyRef: ref || undefined,
    agencyName: c.contactSections?.contactCard?.title || undefined,
    photos: photos.length ? photos : undefined,
    descriptionExcerpt: s.description?.description ? normalizeText(s.description.description).slice(0, 600) : undefined,
    publishedAt: c.metadata?.creationDate,
  };
}

// ------------------------------------------------------------------ page d'une annonce (DOM, secours)

/** Données lisibles dans la page d'une annonce, si le JSON de la page est absent (navigation SPA). */
export function parseSelogerDetailDom(main: Element, pageUrl?: string): ListingData {
  const doc = main.ownerDocument ?? main;
  const text = (sel: string) => doc.querySelector(sel)?.textContent?.replace(/\s+/g, ' ').trim() || undefined;
  const heading = text('[data-testid="cdp-hardfacts-title"]');
  const facts = (text('[data-testid="cdp-hardfacts-keyfacts"]') ?? '').split(/\s*[•·]\s*/).filter(Boolean);
  const kf = parseKeyfacts(facts);
  const keys = text('[data-testid="cdp-classified-keys"]');
  const ref = keys?.match(/R[ée]f[ée]rence annonce\s*:\s*(\S+)/i)?.[1];
  const description = text('[data-testid="cdp-main-description-expandable-text"]');
  // Le titre contient le prix pour les lecteurs d'écran ("949.02 €"), avant le prix affiché.
  const srPrice = doc.querySelector('[data-testid="cdp-seo-wrapper"] span[style*="clip"]')?.textContent;
  return {
    ...kf,
    ...parseAddress(text('[data-testid="cdp-location-address"]')),
    url: pageUrl ? canonicalUrl(pageUrl) : undefined,
    title: buildTitle(heading, kf),
    transaction: transactionFromUrl(pageUrl),
    propertyType: propertyTypeFromText(heading),
    price: parseNumber(srPrice),
    agencyRef: ref,
    descriptionExcerpt: description ? normalizeText(description).slice(0, 600) : undefined,
  };
}
