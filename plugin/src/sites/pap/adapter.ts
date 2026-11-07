/**
 * Adaptateur PAP (de particulier à particulier).
 *
 * Tout vient du HTML (pas de JSON pour les résultats) : cartes de la liste (div.search-list-item-alt) et fiche
 * (div.details-item, JSON-LD). Les annonces « à proximité » que PAP ajoute quand la zone en a peu sont ignorées.
 * Recherches de « Mes projets » : le site refuse les appels hors navigateur, le serveur passe par cet onglet.
 */
import type { CardRef, SiteAdapter } from '../types';
import { CARD_SELECTOR, cardSiteId, DETAIL_SELECTOR, parsePapCard, parsePapDetail, siteIdFromUrl } from './parse';

export const papAdapter: SiteAdapter = {
  id: 'pap',
  label: 'PAP',

  relayPaths: [/^\/annonce\/[^/]+$/, /^\/annonces\/[^/]+$/, /^\/json\/ac-geo$/],

  matches: (loc) => /(^|\.)pap\.fr$/.test(loc.hostname),

  isSearchPage: (loc) => /^\/annonce\/(vente|location)s?-/.test(loc.pathname),

  findCards(root) {
    return [...root.querySelectorAll<HTMLElement>(CARD_SELECTOR)]
      .map((element) => ({ element, siteId: cardSiteId(element) ?? '' }))
      .filter((c) => c.siteId);
  },

  parseCard: (card: CardRef) => parsePapCard(card.element),

  findDetail(root) {
    const element = root.querySelector<HTMLElement>(DETAIL_SELECTOR);
    const siteId = siteIdFromUrl(location.href);
    if (!element || !siteId) return undefined;
    return { siteId, element, anchor: element };
  },

  parseDetail: () => parsePapDetail(document, location.href),

  thumbnailUrl: (url) => (url.startsWith('https://cdn.pap.fr/photos/') ? url.replace(/-p1\.jpg$/, '-p2.webp') : url),
};
