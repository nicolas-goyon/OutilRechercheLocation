/**
 * Adaptateur Bien'ici.
 *
 * Deux sources de données, fusionnées dans la même annonce :
 *  - la carte HTML (titre, adresse, prix, photo, description) : toujours là ;
 *  - le JSON `realEstateAds.json` que le site charge lui-même pour afficher
 *    la liste : il contient en plus la référence agence, la position GPS
 *    (floutée), l'étage, les photos d'origine... Ce sont les meilleurs
 *    signaux de doublon inter-sites. On détecte ces requêtes via la
 *    Performance API (sans patcher XHR) et on relit la même URL.
 */
import { getRootWindow } from '../../shared/dom/root';
import type { CardRef, SiteAdapter } from '../types';
import { CARD_SELECTOR, parseBieniciApiAd, parseBieniciCard, type BieniciApiAd } from './parse';

const API_RE = /\/realEstateAds\.json\?/;

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
        const json = (await res.json()) as { realEstateAds?: BieniciApiAd[]; leadingAds?: BieniciApiAd[] };
        for (const ad of [...(json.realEstateAds ?? []), ...(json.leadingAds ?? [])]) {
          if (ad?.id) onData(ad.id, parseBieniciApiAd(ad));
        }
      } catch (error) {
        console.warn('[RechercheLogement] enrichissement Bien\'ici impossible', error);
      }
    };

    // Requêtes déjà faites avant le chargement du script...
    for (const e of win.performance.getEntriesByType('resource')) void handle(e.name);
    // ...et les suivantes (changement de page, de filtre, de zone de carte).
    if (typeof win.PerformanceObserver === 'function') {
      new win.PerformanceObserver((list) => {
        for (const e of list.getEntries()) void handle(e.name);
      }).observe({ type: 'resource', buffered: false });
    }
  },
};
