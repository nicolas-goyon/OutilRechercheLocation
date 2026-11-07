/**
 * Adaptateur Leboncoin (immobilier).
 *
 *  - Cartes de la liste : [data-qa-id="aditem_container"] (prix, type, pièces, surface, terrain, DPE, commune).
 *  - JSON du rendu serveur (<script id="__NEXT_DATA__">) : toutes les caractéristiques de la 1re page de
 *    résultats, ou de l'annonce ouverte (avec sa description).
 *  - Recherches de « Mes projets » : le site refuse les appels hors navigateur, le serveur passe par cet onglet
 *    (pages /recherche et /ad/…, lues en HTML).
 */
import { getRootWindow } from '../../shared/dom/root';
import type { CardRef, SiteAdapter } from '../types';
import {
  adId,
  adsOfNextData,
  CARD_SELECTOR,
  cardSiteId,
  DETAIL_TITLE_SELECTOR,
  parseLeboncoinAd,
  parseLeboncoinCard,
  parseLeboncoinDetailDom,
  siteIdFromUrl,
  type LeboncoinNextData,
} from './parse';

export const leboncoinAdapter: SiteAdapter = {
  id: 'leboncoin',
  label: 'Leboncoin',

  relayPaths: [/^\/recherche$/, /^\/ad\/[a-z_]+\/\d+$/],

  matches: (loc) => /(^|\.)leboncoin\.fr$/.test(loc.hostname),

  isSearchPage: (loc) => loc.pathname === '/recherche' && /category=(9|10)\b/.test(loc.search),

  findCards(root) {
    return [...root.querySelectorAll<HTMLElement>(CARD_SELECTOR)]
      .map((element) => ({ element, siteId: cardSiteId(element) ?? '' }))
      .filter((c) => c.siteId);
  },

  parseCard: (card: CardRef) => parseLeboncoinCard(card.element),

  findDetail(root) {
    const title = root.querySelector<HTMLElement>(DETAIL_TITLE_SELECTOR);
    const siteId = siteIdFromUrl(location.href);
    if (!title || !siteId) return undefined;
    const element = title.closest<HTMLElement>('article') ?? title.parentElement ?? root.body;
    return { siteId, element, anchor: element };
  },

  parseDetail: (detail) => parseLeboncoinDetailDom(detail.element, location.href),

  thumbnailUrl: (url) => (url.startsWith('https://img.leboncoin.fr/') ? url.replace(/rule=ad-large$/, 'rule=ad-image') : url),

  startEnrichment(onData) {
    const win = getRootWindow();
    const script = win.document.getElementById('__NEXT_DATA__');
    if (!script?.textContent) return;
    try {
      const data = JSON.parse(script.textContent) as LeboncoinNextData;
      for (const ad of adsOfNextData(data)) {
        const id = adId(ad);
        if (id) onData(id, parseLeboncoinAd(ad));
      }
    } catch (error) {
      console.warn('[RechercheLogement] JSON Leboncoin illisible', error);
    }
  },
};
