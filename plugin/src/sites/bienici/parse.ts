/**
 * Lecture des données Bien'ici. Fonctions sans effet de bord (les tests utilisent un HTML extrait).
 *
 * Structure d'une carte de résultats (observée en octobre 2026) :
 *
 *   <article data-id="hektor-sia-immo-2342" class="ad-overview search-results-list__ad-overview ...">
 *     <a href="/annonce/location/paris-19e/appartement/3pieces/hektor-sia-immo-2342?q=..." class="detailedSheetLink">
 *       <img src="https://file.bienici.com/photo/..." alt="Location appartement 3 pièces 88 m²">
 *       <span class="real-estate-main-info__title">Appartement 3 pièces 88 m²</span>
 *       <span class="real-estate-main-info__address">75019 Paris 19e (Bassin de la Villette)</span>
 *       <span class="ad-price__the-price">1 866 €</span>
 *       <span class="ad-price__per-month">par mois charges comprises</span>
 *       <div class="ad-overview-description" style="display:none">…description…</div>
 *
 * Les annonces "mises en avant" (leading ads) utilisent la même carte. Elles
 * sont dans .search-results-list__leading-ads-box.
 */
import type { ListingData } from '../../core/types';
import { normalizeText, parseNumber } from '../../shared/text';

export const CARD_SELECTOR = 'article.ad-overview[data-id]';

/** Fiche d'une annonce. Bien'ici l'affiche par-dessus la liste (SPA) ou seule (lien direct). */
export const DETAIL_SELECTOR = 'section.section-detailedSheet[id^="section-detailedSheet_"]';

/**
 * Emplacements publicitaires. Avec un bloqueur de pub, ils restent vides
 * (300×250 dans la liste carte, 1920×90 en haut) et laissent des trous.
 */
export const AD_SELECTORS = [
  '.search-results-list__commercial-ad',
  '.advertisement-container',
  '.safeFrameContentFullWidth',
];

const PROPERTY_TYPES: [RegExp, string][] = [
  [/^appartement/, 'flat'],
  [/^studio/, 'flat'],
  [/^maison/, 'house'],
  [/^villa/, 'house'],
  [/^loft/, 'loft'],
  [/^chateau/, 'castle'],
  [/^hotel particulier/, 'townhouse'],
  [/^terrain/, 'terrain'],
  [/^parking|^box/, 'parking'],
];

export function parseBieniciCard(card: Element): ListingData {
  const text = (sel: string) => card.querySelector(sel)?.textContent?.replace(/\s+/g, ' ').trim() || undefined;

  const title = text('.real-estate-main-info__title');
  const address = text('.real-estate-main-info__address');
  const priceText = text('.ad-price__the-price');
  const perMonth = text('.ad-price__per-month');
  const href = card.querySelector<HTMLAnchorElement>('a.detailedSheetLink')?.getAttribute('href') ?? undefined;
  const photo = card.querySelector<HTMLImageElement>('img.img__image, .ad-overview-photo img')?.getAttribute('src');
  const description = card.querySelector('.ad-overview-description')?.innerHTML;

  const data: ListingData = {
    ...parseTitle(title),
    ...parseAddress(address),
    title,
    price: parseNumber(priceText),
    url: href ? canonicalUrl(href) : undefined,
    transaction: href?.startsWith('/annonce/location') || perMonth ? 'rent' : href?.startsWith('/annonce/vente') ? 'buy' : undefined,
    photos: photo ? [stripPhotoParams(photo)] : undefined,
    descriptionExcerpt: description ? normalizeText(description).slice(0, 600) : undefined,
  };
  return data;
}

/**
 * Fiche d'une annonce :
 *
 *   <section id="section-detailedSheet_century-21-202_2190_28251" class="section-detailedSheet">
 *     <div class="detailedSheetFirstBlock">
 *       <h1>Location appartement 2 pièces 49 m²<span class="fullAddress">12850 Onet-le-Château</span></h1>
 *       <div class="detailedSheetPrice"> <span class="ad-price__the-price">605 €</span> ...
 *     <div class="ad-detailed-sheet-main-info">Modifiée le 17 sept. 2026 | Réf. de l’annonce : 28251</div>
 *     <span class="see-more-description__content">…description…</span>
 */
export function detailSiteId(section: Element): string | undefined {
  return section.id.replace(/^section-detailedSheet_/, '') || undefined;
}

export function parseBieniciDetail(section: Element): ListingData {
  const text = (sel: string) => section.querySelector(sel)?.textContent?.replace(/\s+/g, ' ').trim() || undefined;
  const h1 = section.querySelector('h1');
  const address = text('h1 .fullAddress');
  let heading: string | undefined;
  if (h1) {
    const clone = h1.cloneNode(true) as Element;
    clone.querySelector('.fullAddress')?.remove();
    heading = clone.textContent?.replace(/\s+/g, ' ').trim() || undefined;
  }
  // "Location appartement 2 pièces 49 m²" -> "Appartement 2 pièces 49 m²" (même forme que le titre des cartes).
  const transactionWord = heading?.match(/^(location|vente|achat)\s+/i)?.[1]?.toLowerCase();
  const title = heading?.replace(/^(location|vente|achat)\s+/i, '').replace(/^./, (c) => c.toUpperCase());
  const perMonth = text('.detailedSheetPrice .ad-price__per-month');
  const ref = text('.ad-detailed-sheet-main-info')?.match(/R[ée]f\.? de l.annonce\s*:\s*(\S+)/i)?.[1];
  const description = (section.querySelector('.see-more-description__content') ?? section.querySelector('section.description p'))?.textContent;
  const photos = [...section.querySelectorAll<HTMLImageElement>('.slideshow img[u="image"], .slideshow img[src2]')]
    .map((img) => img.getAttribute('src2') ?? img.getAttribute('src') ?? '')
    .filter((u) => u.startsWith('https://file.bienici.com/'))
    .map(stripPhotoParams);
  const id = detailSiteId(section);

  return {
    ...parseTitle(title),
    ...parseAddress(address),
    title,
    price: parseNumber(text('.detailedSheetPrice .ad-price__the-price')),
    transaction: transactionWord === 'location' || perMonth ? 'rent' : transactionWord ? 'buy' : undefined,
    agencyRef: ref,
    photos: photos.length ? [...new Set(photos)].slice(0, 4) : undefined,
    descriptionExcerpt: description ? normalizeText(description).slice(0, 600) : undefined,
    url: id ? detailUrl(id, section) : undefined,
  };
}

/** URL canonique de la fiche : celle de la page si elle porte le même identifiant. */
function detailUrl(id: string, section: Element): string | undefined {
  const loc = section.ownerDocument?.defaultView?.location;
  if (loc && loc.pathname.endsWith(`/${id}`)) return canonicalUrl(loc.pathname);
  const canonical = section.ownerDocument?.querySelector<HTMLLinkElement>('link[rel="canonical"]')?.getAttribute('href');
  return canonical && canonical.endsWith(`/${id}`) ? canonicalUrl(canonical) : undefined;
}

/** "Appartement meublé 2 pièces 40 m²" -> type, meublé, pièces, surface. */
export function parseTitle(title: string | undefined): ListingData {
  if (!title) return {};
  const t = normalizeText(title);
  const out: ListingData = {};
  for (const [re, type] of PROPERTY_TYPES) {
    if (re.test(t)) {
      out.propertyType = type;
      break;
    }
  }
  if (/\bmeuble\b/.test(t)) out.furnished = true;
  const rooms = t.match(/(\d+)\s*pieces?\b/);
  if (rooms) out.rooms = Number(rooms[1]);
  else if (/^studio/.test(t)) out.rooms = 1;
  const surface = title.replace(/[\u00a0\u202f]/g, ' ').match(/(\d+(?:[.,]\d+)?)\s*m\u00b2/);
  if (surface) out.surface = Number(surface[1].replace(',', '.'));
  return out;
}

/** "75019 Paris 19e (Bassin de la Villette)" -> CP, ville, quartier. */
export function parseAddress(address: string | undefined): ListingData {
  if (!address) return {};
  const a = address.replace(/[\u00a0\u202f]/g, ' ').trim();
  const m = a.match(/^(\d{5})\s+(.+?)(?:\s*\((.+)\))?$/);
  if (!m) return { city: a };
  return { postalCode: m[1], city: m[2].trim(), district: m[3]?.trim() };
}

/**
 * Empreinte d'une photo d'origine : nom de fichier en minuscules, sans
 * extension ni paramètres ("photo_3c9c36dc663b54820545f52f6aef3a1e").
 * Renvoie undefined si le nom est trop générique (par exemple "1", "photo").
 */
export function photoKey(url: string | undefined): string | undefined {
  if (!url) return undefined;
  const name = url.split('?')[0].split('/').pop()?.toLowerCase().replace(/\.(jpe?g|png|webp|gif)$/, '');
  // "image0001", "photo_2" : noms trop génériques. Deux agences peuvent utiliser le même nom.
  const significant = name?.replace(/image|photo|img|pic|[_\-.\s]/g, '') ?? '';
  return name && significant.length >= 8 ? name : undefined;
}

/** URL absolue de l'annonce, sans les paramètres de recherche (?q=...). */
export function canonicalUrl(href: string): string {
  const u = new URL(href, 'https://www.bienici.com');
  return u.origin + u.pathname;
}

/** Retire les paramètres de taille (?width=300&...). L'URL ne change donc pas. */
export function stripPhotoParams(url: string): string {
  return url.split('?')[0];
}

// ------------------------------------------------------------------ API JSON

/** Champs utiles d'une annonce de realEstateAds.json. */
export interface BieniciApiAd {
  id: string;
  adType?: string;
  propertyType?: string;
  reference?: string;
  title?: string;
  description?: string;
  price?: number | number[];
  charges?: number;
  surfaceArea?: number | number[];
  roomsQuantity?: number | number[];
  bedroomsQuantity?: number;
  floor?: number | null;
  isFurnished?: boolean;
  postalCode?: string;
  city?: string;
  district?: { name?: string; libelle?: string } | null;
  blurInfo?: { radius?: number; position?: { lat: number; lon: number } };
  photos?: { url?: string; url_photo?: string }[];
  publicationDate?: string;
  accountDisplayName?: string;
}

const first = (v: number | number[] | undefined) => (Array.isArray(v) ? v[0] : v);

export function parseBieniciApiAd(ad: BieniciApiAd): ListingData {
  const pos = ad.blurInfo?.position;
  return {
    transaction: ad.adType === 'rent' ? 'rent' : ad.adType === 'buy' ? 'buy' : undefined,
    propertyType: ad.propertyType,
    agencyRef: ad.reference || undefined,
    agencyName: ad.accountDisplayName || undefined,
    price: first(ad.price),
    charges: ad.charges,
    surface: first(ad.surfaceArea),
    rooms: first(ad.roomsQuantity),
    bedrooms: ad.bedroomsQuantity,
    floor: ad.floor ?? undefined,
    furnished: ad.isFurnished,
    postalCode: ad.postalCode,
    city: ad.city,
    district: ad.district?.libelle || ad.district?.name || undefined,
    geo: pos ? { lat: pos.lat, lon: pos.lon, precisionM: ad.blurInfo?.radius } : undefined,
    photos: ad.photos?.map((p) => p.url || p.url_photo).filter((u): u is string => !!u).slice(0, 4),
    photoKeys: ad.photos?.map((p) => photoKey(p.url_photo)).filter((u): u is string => !!u).slice(0, 6),
    descriptionExcerpt: ad.description ? normalizeText(ad.description).slice(0, 600) : undefined,
    publishedAt: ad.publicationDate,
  };
}
