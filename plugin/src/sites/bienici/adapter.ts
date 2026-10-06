/**
 * Adaptateur Bien'ici.
 *
 * L'adaptateur fusionne deux sources de données dans la même annonce :
 *  - La carte HTML (titre, adresse, prix, photo, description). Elle est
 *    toujours présente.
 *  - Le JSON `realEstateAds.json`. Le site le charge pour afficher la liste.
 *    Ce JSON contient aussi la référence agence, la position GPS (floue),
 *    l'étage, les photos d'origine... Ces données sont les meilleurs indices
 *    de doublon entre sites.
 *
 * L'adaptateur trouve ces requêtes avec la Performance API (sans modifier
 * XHR). Il lit ensuite de nouveau la même URL.
 *
 * Page d'un bien : la fiche (section.section-detailedSheet) reçoit un bandeau
 * de suivi. Le site charge alors `realEstateAd.json?id=...` (une seule annonce,
 * mêmes champs). L'adaptateur relit aussi cette URL.
 */
import { getRootWindow } from '../../shared/dom/root';
import type { CardRef, SiteAdapter } from '../types';
import {
  AD_SELECTORS,
  CARD_SELECTOR,
  DETAIL_SELECTOR,
  detailSiteId,
  parseBieniciApiAd,
  parseBieniciCard,
  parseBieniciDetail,
  type BieniciApiAd,
} from './parse';

/** Liste (realEstateAds.json), fiche (realEstateAd.json) et variante "une annonce" (realEstateAds-one.json). */
const API_RE = /\/realEstateAds?(-one)?\.json\?/;

export const bieniciAdapter: SiteAdapter = {
  id: 'bienici',
  label: "Bien'ici",

  matches: (loc) => /(^|\.)bienici\.com$/.test(loc.hostname),

  findCards(root) {
    return [...root.querySelectorAll<HTMLElement>(CARD_SELECTOR)]
      .map((element) => ({ element, siteId: element.dataset.id ?? '' }))
      .filter((c) => c.siteId);
  },

  parseCard: (card: CardRef) => parseBieniciCard(card.element),

  observeRoot: () => document.querySelector('#app, main') ?? document.body,

  toolbarAnchor: (card) => card.element,

  hideSelectors: AD_SELECTORS,

  isSearchPage: (loc) => loc.pathname.startsWith('/recherche/'),

  findDetail(root) {
    // Une seule fiche ouverte en pratique. Sinon : la dernière ajoutée.
    const section = [...root.querySelectorAll<HTMLElement>(DETAIL_SELECTOR)].pop();
    const siteId = section && detailSiteId(section);
    if (!section || !siteId) return undefined;
    const anchor = section.querySelector<HTMLElement>('.detailedSheetFirstBlock') ?? section;
    return { siteId, element: section, anchor };
  },

  parseDetail: (detail) => parseBieniciDetail(detail.element),

  thumbnailUrl: (url) => (url.startsWith('https://file.bienici.com/') ? `${url}?width=400&height=240&fit=cover` : url),

  startEnrichment(onData) {
    const win = getRootWindow();
    const done = new Set<string>();

    const handle = async (url: string) => {
      if (!API_RE.test(url) || done.has(url)) return;
      done.add(url);
      try {
        const res = await win.fetch(url, { credentials: 'include' });
        if (!res.ok) return;
        const json = (await res.json()) as { realEstateAds?: BieniciApiAd[]; leadingAds?: BieniciApiAd[] } & Partial<BieniciApiAd>;
        // realEstateAd.json renvoie directement l'annonce (champ id à la racine).
        const single = typeof json.id === 'string' ? [json as BieniciApiAd] : [];
        for (const ad of [...(json.realEstateAds ?? []), ...(json.leadingAds ?? []), ...single]) {
          if (ad?.id) onData(ad.id, parseBieniciApiAd(ad));
        }
      } catch (error) {
        console.warn('[RechercheLogement] Échec de l\'enrichissement Bien\'ici', error);
      }
    };

    // Requêtes faites avant le chargement du script...
    for (const e of win.performance.getEntriesByType('resource')) void handle(e.name);
    // ...et requêtes suivantes (changement de page, de filtre ou de zone de carte).
    if (typeof win.PerformanceObserver === 'function') {
      new win.PerformanceObserver((list) => {
        for (const e of list.getEntries()) void handle(e.name);
      }).observe({ type: 'resource', buffered: false });
    }
  },
};
