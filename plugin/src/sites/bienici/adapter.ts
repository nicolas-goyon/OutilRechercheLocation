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
