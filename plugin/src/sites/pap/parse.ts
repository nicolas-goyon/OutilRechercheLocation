/**
 * Lecture des pages PAP (www.pap.fr, relevé en novembre 2026). Fonctions sans effet de bord, testées sur des
 * extraits réels. Pas de JSON pour les résultats : tout vient du HTML.
 *
 * Liste de résultats (/annonce/vente-maisons-…-g429) :
 *
 *   <div class="search-list-item-alt">
 *     <img src="https://cdn.pap.fr/photos/pap/…/5-p2.webp">          (petite photo ; grande : -p1.jpg)
 *     <div class="item-thumb-dpe item-thumb-dpe-d"></div>
 *     <a class="item-title" href="/annonces/maison-ibos-r464301065" name="464301065">
 *       <span class="item-price">239.000 €</span> <span class="h1">Ibos</span>   ("Poueyferré (65100)")
 *       <ul class="item-tags"><li>6 pièces</li><li>4 chambres</li><li>155 m²</li></ul>
 *     <p class="item-description" data-gaq="{…Consulter…}">…</p>    ("Consulter proximite" : annonce voisine)
 *
 *   Encarts (prix au m², programmes neufs) : même classe, sans lien /annonces/ : ignorés.
 *
 * Fiche (/annonces/maison-ibos-r464301065) : div.details-item, h2 "Ibos (65420)", li strong "Terrain 500 m²",
 * .energy-indice / .ges-indice li.active, "Réf. : G43/1065 / Publié le 06 novembre 2026", JSON-LD Product.
 *
 * Mêmes règles que le serveur (server/.../Sites/Pap/PapListingParser.cs).
 */
import type { ListingData } from '../../core/types';
import { MAX_PHOTOS } from '../../core/types';
import { energyClass } from '../../shared/details';
import { extractRefs, normalizeText, parseNumber } from '../../shared/text';

export const ORIGIN = 'https://www.pap.fr';
export const CARD_SELECTOR = '.search-list-item-alt';
export const DETAIL_SELECTOR = '.details-item';

/** "/annonces/maison-ibos-r464301065" -> "464301065". */
export function siteIdFromUrl(href: string): string | undefined {
  return new URL(href, ORIGIN).pathname.match(/^\/annonces\/[^/]*-r(\d+)$/)?.[1];
}

/** Lien de l'annonce dans la carte ; encart ou annonce « à proximité » : undefined. */
export function cardLink(card: Element): HTMLAnchorElement | undefined {
  const link = card.querySelector<HTMLAnchorElement>('a.item-title[href^="/annonces/"]');
  if (!link) return undefined;
  const gaq = card.querySelector('p.item-description')?.getAttribute('data-gaq') ?? '';
  return /proximite/i.test(gaq) ? undefined : link;
}

export function cardSiteId(card: Element): string | undefined {
  const link = cardLink(card);
  return link ? (link.getAttribute('name') ?? siteIdFromUrl(link.getAttribute('href') ?? '')) : undefined;
}

/** "/annonces/maison-…" -> type commun. */
export function typeOfPath(path: string): string {
  const kind = path.split('/').filter(Boolean).pop()?.split('-')[0];
  switch (kind) {
    case 'maison': return 'house';
    case 'appartement': case 'studio': return 'flat';
    case 'terrain': return 'terrain';
    case 'parking': case 'garage': case 'box': return 'parking';
    case 'immeuble': return 'building';
    case 'local': case 'bureau': case 'boutique': return 'commercial';
    case 'loft': return 'loft';
    default: return 'other';
  }
}

const TYPE_LABELS: Record<string, string> = {
  house: 'Maison', flat: 'Appartement', terrain: 'Terrain', parking: 'Parking', building: 'Immeuble',
  commercial: 'Local', loft: 'Loft', other: 'Bien',
};

/** "Poueyferré (65100) à 24km de Arreau" -> ["Poueyferré", "65100"]. */
export function splitPlace(text: string | undefined): [string | undefined, string | undefined] {
  const t = clean(text);
  if (!t) return [undefined, undefined];
  const m = t.match(/^(.*?)\s*\((\d{5})\)/);
  return m ? [m[1].trim(), m[2]] : [t, undefined];
}

/** "239.000 €" -> 239000 (le point sépare les milliers). */
export function price(text: string | null | undefined): number | undefined {
  const d = text?.replace(/\D/g, '');
  return d ? Number(d) || undefined : undefined;
}

/** "155 m²", "Terrain 1.200 m²" -> nombre. */
function surface(text: string | undefined): number | undefined {
  return text ? parseNumber(text.replace(/\./g, '')) : undefined;
}

function clean(text: string | null | undefined): string | undefined {
  const t = text?.replace(/\s+/g, ' ').trim();
  return t || undefined;
}

function count(tags: string[], word: string): number | undefined {
  const tag = tags.find((t) => t.includes(word));
  return tag ? parseNumber(tag) : undefined;
}

/** "…/5-p2.webp" -> "…/5-p1.jpg" (grande photo de la fiche). */
export function fullPhoto(url: string): string {
  return url.replace(/-p2\.webp$/, '-p1.jpg');
}

export function parsePapCard(card: Element): ListingData {
  const link = cardLink(card) ?? card.querySelector('a.item-title');
  const path = link?.getAttribute('href') ?? '';
  const tags = [...(link?.querySelectorAll('ul.item-tags li') ?? [])].map((li) => clean(li.textContent)).filter((t): t is string => !!t);
  const [city, cp] = splitPlace(link?.querySelector('span.h1')?.childNodes[0]?.textContent ?? link?.querySelector('span.h1')?.textContent ?? undefined);
  const photos = [...new Set([...card.querySelectorAll('img[src^="https://cdn.pap.fr/photos/"]')].map((i) => fullPhoto(i.getAttribute('src') ?? '')))].slice(0, MAX_PHOTOS);
  const dpe = [...card.querySelectorAll('[class*="item-thumb-dpe-"]')].map((e) => e.className.match(/item-thumb-dpe-([a-g])\b/)?.[1]).find(Boolean);
  const description = clean(card.querySelector('p.item-description')?.textContent);
  const type = typeOfPath(path);
  return {
    url: path ? ORIGIN + path : undefined,
    title: [TYPE_LABELS[type], ...tags.filter((t) => !t.includes('chambre'))].join(' '),
    transaction: 'buy',
    propertyType: type,
    price: price(link?.querySelector('span.item-price')?.textContent),
    rooms: count(tags, 'pièce'),
    bedrooms: count(tags, 'chambre'),
    surface: surface(tags.find((t) => t.endsWith('m²') && !t.startsWith('Terrain'))),
    city,
    postalCode: cp ?? path.match(/-(\d{5})-r\d+$/)?.[1],
    energyClass: energyClass(dpe),
    photos: photos.length ? photos : undefined,
    descriptionExcerpt: description ? normalizeText(description).slice(0, 600) : undefined,
  };
}

interface PapJsonLd {
  '@type'?: string;
  description?: string;
  image?: string[] | string;
  offers?: { price?: number | string; seller?: { name?: string } };
  address?: { postalCode?: string; addressLocality?: string };
}

const MONTHS = ['janvier', 'fevrier', 'mars', 'avril', 'mai', 'juin', 'juillet', 'aout', 'septembre', 'octobre', 'novembre', 'decembre'];

/** "06 novembre 2026" -> "2026-11-06". */
export function frenchDate(text: string | undefined): string | undefined {
  const m = text ? normalizeText(text).match(/^(\d{1,2}) ([a-z]+) (\d{4})$/) : null;
  const month = m ? MONTHS.indexOf(m[2]) + 1 : 0;
  return m && month ? `${m[3]}-${String(month).padStart(2, '0')}-${m[1].padStart(2, '0')}` : undefined;
}

/** Fiche d'une annonce. root : document (ou fragment) de la page. */
export function parsePapDetail(root: ParentNode, href: string): ListingData {
  let ld: PapJsonLd | undefined;
  for (const s of root.querySelectorAll('script[type="application/ld+json"]')) {
    try {
      const node = JSON.parse(s.textContent ?? '') as PapJsonLd;
      if (node['@type'] === 'Product') ld = node;
    } catch {
      // Bloc mal formé : on essaie le suivant.
    }
  }
  const path = new URL(href, ORIGIN).pathname;
  const tags = [...root.querySelectorAll('.item-tags li strong')].map((e) => clean(e.textContent)).filter((t): t is string => !!t);
  const dateLine = clean(root.querySelector('p.item-date')?.textContent) ?? '';
  const [city, cp] = splitPlace(root.querySelector('.item-description h2')?.textContent ?? undefined);
  const images = typeof ld?.image === 'string' ? [ld.image] : (ld?.image ?? []);
  const seller = ld?.offers?.seller?.name;
  const description = ld?.description ?? clean(root.querySelector('.item-description .margin-bottom-30, .item-description p')?.textContent);
  const tax = description?.match(/[Tt]axe foncière\s*:?\s*([\d\s .]+)\s*€/)?.[1];
  const refs = extractRefs(description);
  const type = typeOfPath(path);
  return {
    url: ORIGIN + path,
    title: clean(root.querySelector('h1.item-title')?.childNodes[0]?.textContent),
    transaction: 'buy',
    propertyType: type,
    price: ld?.offers?.price !== undefined ? Number(ld.offers.price) || undefined : price(root.querySelector('h1 .item-price')?.textContent),
    rooms: count(tags, 'pièce'),
    bedrooms: count(tags, 'chambre'),
    surface: surface(tags.find((t) => t.endsWith('m²') && !t.startsWith('Terrain'))),
    landSurface: surface(tags.find((t) => t.startsWith('Terrain'))),
    postalCode: ld?.address?.postalCode ?? cp,
    city: ld?.address?.addressLocality ?? city,
    energyClass: energyClass(root.querySelector('.energy-indice li.active')?.textContent),
    ghgClass: energyClass(root.querySelector('.ges-indice li.active')?.textContent),
    publishedAt: frenchDate(dateLine.match(/Publié le\s+(\d{1,2}\s+\S+\s+\d{4})/)?.[1]),
    agencyRef: dateLine.match(/Réf\.\s*:\s*(\S+)/)?.[1],
    agencyName: seller && seller !== 'Particulier' ? seller : undefined,
    photos: images.length ? images.slice(0, MAX_PHOTOS) : undefined,
    descriptionExcerpt: description ? normalizeText(description).slice(0, 600) : undefined,
    otherRefs: refs.length ? refs : undefined,
    propertyTax: price(tax),
  };
}
