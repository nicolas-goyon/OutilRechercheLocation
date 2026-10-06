/**
 * Adaptateur SeLoger.
 *
 * Comme pour Bien'ici, deux sources de données :
 *  - La carte HTML (titre, prix, faits clés, adresse, photo). Toujours présente.
 *  - Le JSON du site :
 *     - 1re page de résultats : rendu serveur, dans window.__UFRN_FETCHER__.
 *     - pages suivantes (SPA) : GET /classifiedList/<ids>. L'adaptateur trouve
 *       ces requêtes avec la Performance API, puis relit la même URL.
 *     - page d'une annonce : window.__UFRN_LIFECYCLE_SERVERREQUEST__ (GPS,
 *       référence de l'annonce, charges, description complète).
 */
import { getRootWindow } from '../../shared/dom/root';
import type { CardRef, SiteAdapter } from '../types';
import {
  AD_SELECTORS,
  CARD_SELECTOR,
  cardSiteId,
  DETAIL_SELECTOR,
  parseSelogerCard,
  parseSelogerDetailClassified,
  parseSelogerDetailDom,
  parseSelogerSerpClassified,
  siteIdFromUrl,
  type SelogerDetailClassified,
  type SelogerSerpClassified,
} from './parse';

const LIST_API_RE = /\/classifiedList\/[^/?#]+/;

interface SelogerWindow {
  __UFRN_FETCHER__?: { data?: Record<string, { pageProps?: { classifiedsData?: Record<string, SelogerSerpClassified> } }> };
  __UFRN_LIFECYCLE_SERVERREQUEST__?: { app_cldp?: { data?: { classified?: SelogerDetailClassified } } };
}

export const selogerAdapter: SiteAdapter = {
  id: 'seloger',
  label: 'SeLoger',

  matches: (loc) => /(^|\.)seloger\.com$/.test(loc.hostname),

  isSearchPage: (loc) => loc.pathname.startsWith('/classified-search') || loc.pathname.startsWith('/list.htm'),

  findCards(root) {
    return [...root.querySelectorAll<HTMLElement>(CARD_SELECTOR)]
      .map((element) => ({ element, siteId: cardSiteId(element) ?? '' }))
      .filter((c) => c.siteId);
  },

  parseCard: (card: CardRef) => parseSelogerCard(card.element),

  hideSelectors: AD_SELECTORS,

  findDetail(root) {
    const main = root.querySelector<HTMLElement>(DETAIL_SELECTOR);
    const siteId = main && siteIdFromUrl(location.href);
    if (!main || !siteId) return undefined;
    // Bandeau au-dessus du bloc titre / prix.
    const title = main.querySelector<HTMLElement>('[data-testid="cdp-seo-wrapper"]');
    const anchor = title?.parentElement ?? main;
    return { siteId, element: main, anchor };
  },

  parseDetail: (detail) => parseSelogerDetailDom(detail.element, location.href),

  thumbnailUrl: (url) =>
    /^https:\/\/(cdnihddipa\.cloudimg\.io|mms\.seloger\.com)\//.test(url) ? `${url}${url.includes('?') ? '&' : '?'}w=400&h=300` : url,

  startEnrichment(onData) {
    const win = getRootWindow();
    const sw = win as unknown as SelogerWindow;

    // Page d'une annonce : JSON du rendu serveur. On vérifie l'identifiant (la page a pu changer en SPA).
    const detail = sw.__UFRN_LIFECYCLE_SERVERREQUEST__?.app_cldp?.data?.classified;
    const pageId = siteIdFromUrl(win.location.href);
    if (detail?.id && detail.id === pageId) {
      safely(() => onData(detail.id, parseSelogerDetailClassified(detail, win.location.href)));
    }

    // 1re page de résultats : JSON du rendu serveur.
    for (const entry of Object.values(sw.__UFRN_FETCHER__?.data ?? {})) {
      for (const c of Object.values(entry?.pageProps?.classifiedsData ?? {})) {
        if (c?.id) safely(() => onData(c.id, parseSelogerSerpClassified(c)));
      }
    }

    // Pages suivantes : /classifiedList/<ids> (relu une fois par URL).
    const done = new Set<string>();
    const handle = async (url: string) => {
      if (!LIST_API_RE.test(url) || done.has(url)) return;
      done.add(url);
      try {
        const res = await win.fetch(url, { credentials: 'include' });
        if (!res.ok) return;
        const json = (await res.json()) as SelogerSerpClassified[] | { classifieds?: SelogerSerpClassified[] };
        const list = Array.isArray(json) ? json : (json.classifieds ?? []);
        for (const c of list) if (c?.id) onData(c.id, parseSelogerSerpClassified(c));
      } catch (error) {
        console.warn('[RechercheLogement] Échec de l\'enrichissement SeLoger', error);
      }
    };
    for (const e of win.performance.getEntriesByType('resource')) void handle(e.name);
    if (typeof win.PerformanceObserver === 'function') {
      new win.PerformanceObserver((list) => {
        for (const e of list.getEntries()) void handle(e.name);
      }).observe({ type: 'resource', buffered: false });
    }
  },
};

function safely(fn: () => void): void {
  try {
    fn();
  } catch (error) {
    console.warn('[RechercheLogement] JSON SeLoger illisible', error);
  }
}
