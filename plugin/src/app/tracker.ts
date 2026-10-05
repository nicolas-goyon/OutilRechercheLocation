/**
 * Orchestrateur côté page : relie l'adaptateur du site, la synchro serveur et l'UI.
 *
 *  - repère les cartes d'annonces (et les re-repère à chaque re-rendu : les
 *    sites sont des SPA), les envoie au serveur comme observations ;
 *  - transmet aussi les données enrichies (JSON du site) ;
 *  - applique l'état reçu (ou en cache) : attributs data-tmrl-* qui masquent /
 *    atténuent / colorent les cartes, et barre d'actions par carte.
 */
import type { SyncEngine } from '../core/sync';
import { listingKey, type ListingView, type PropertyStatus } from '../core/types';
import type { CardRef, SiteAdapter } from '../sites/types';
import { siteLabel } from '../sites';
import { renderToolbar, TOOLBAR_HOST_ATTR } from '../ui/cardToolbar';
import { installPageStyles, setShowHidden } from '../ui/pageStyles';
import { showDetails, showSuggestion } from '../ui/views';

export interface TrackerOptions {
  /** Statuts dont les cartes sont masquées. Default: ['rejected']. */
  hideStatuses: PropertyStatus[];
  /** Masquer aussi une annonce non qualifiée dont un doublon PROBABLE a l'un de ces statuts. Default: []. */
  hideSuggestedDuplicatesOf: PropertyStatus[];
  debug: boolean;
}

export interface PageCounts {
  total: number;
  hidden: number;
  seen: number;
  toContact: number;
  suggested: number;
}

export class Tracker {
  private processed = new WeakMap<HTMLElement, string>();
  private renderScheduled = false;
  private counts: PageCounts = { total: 0, hidden: 0, seen: 0, toContact: 0, suggested: 0 };
  private countListeners = new Set<(c: PageCounts) => void>();
  showHidden = false;

  constructor(
    private readonly adapter: SiteAdapter,
    private readonly sync: SyncEngine,
    private readonly options: TrackerOptions,
  ) {}

  start(): void {
    installPageStyles();

    const observer = new MutationObserver((records) => {
      if (records.some((r) => !isOwnMutation(r))) this.schedule();
    });
    observer.observe(this.adapter.observeRoot?.() ?? document.body, {
      childList: true,
      subtree: true,
      attributes: true,
      attributeFilter: ['data-id', 'href'],
    });

    this.adapter.startEnrichment?.((siteId, data) => {
      // On n'enregistre que les annonces réellement affichées (l'API en renvoie parfois d'autres).
      if (!document.querySelector(`[data-tmrl-card="${CSS.escape(siteId)}"]`)) {
        this.pendingApi.set(siteId, data);
        return;
      }
      this.sync.observe({ site: this.adapter.id, siteId, source: 'api', data, seenAt: Date.now() });
    });

    this.sync.onChange(() => this.schedule());
    // Retour sur l'onglet : on redemande l'état (un statut a pu changer sur le site local).
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') {
        this.sync.refresh(this.adapter.findCards(document).map((c) => listingKey(this.adapter.id, c.siteId)));
      }
    });
    this.schedule();
  }

  private pendingApi = new Map<string, Parameters<SyncEngine['observe']>[0]['data']>();

  onCounts(cb: (c: PageCounts) => void): void {
    this.countListeners.add(cb);
    cb(this.counts);
  }

  pageCounts(): PageCounts {
    return this.counts;
  }

  setShowHidden(show: boolean): void {
    this.showHidden = show;
    setShowHidden(show);
  }

  private schedule(): void {
    if (this.renderScheduled) return;
    this.renderScheduled = true;
    setTimeout(() => {
      this.renderScheduled = false;
      this.scan();
    }, 120);
  }

  private scan(): void {
    const cards = this.adapter.findCards(document);
    const counts: PageCounts = { total: cards.length, hidden: 0, seen: 0, toContact: 0, suggested: 0 };
    for (const card of cards) {
      this.ingest(card);
      const s = this.apply(card);
      if (s.hidden) counts.hidden++;
      if (s.status === 'seen') counts.seen++;
      if (s.status === 'toContact') counts.toContact++;
      if (s.suggested) counts.suggested++;
    }
    this.counts = counts;
    for (const cb of this.countListeners) cb(counts);
    if (this.options.debug) console.debug('[RechercheLogement] scan', counts);
  }

  /** Envoie la carte au serveur une fois par élément/annonce (les SPA réutilisent les éléments). */
  private ingest(card: CardRef): void {
    if (this.processed.get(card.element) === card.siteId) return;
    this.processed.set(card.element, card.siteId);
    card.element.setAttribute('data-tmrl-card', card.siteId);
    const now = Date.now();
    this.sync.observe({ site: this.adapter.id, siteId: card.siteId, source: 'card', data: this.adapter.parseCard(card), seenAt: now });
    const api = this.pendingApi.get(card.siteId);
    if (api) {
      this.pendingApi.delete(card.siteId);
      this.sync.observe({ site: this.adapter.id, siteId: card.siteId, source: 'api', data: api, seenAt: now });
    }
  }

  private apply(card: CardRef): { hidden: boolean; status: PropertyStatus; suggested: boolean } {
    const key = listingKey(this.adapter.id, card.siteId);
    const el = card.element;
    const view: ListingView | undefined = this.sync.view(key);
    const status = view?.status ?? 'none';
    const best = view?.suggestions[0];

    let hidden = this.options.hideStatuses.includes(status);
    if (!hidden && status === 'none' && best && this.options.hideSuggestedDuplicatesOf.includes(best.otherStatus)) hidden = true;

    setAttr(el, 'data-tmrl-status', status === 'none' ? null : status);
    setAttr(el, 'data-tmrl-hidden', hidden ? '' : null);
    setAttr(el, 'data-tmrl-dup', best ? (best.otherStatus === 'rejected' ? 'rejected' : 'suggested') : null);

    renderToolbar(
      this.adapter.toolbarAnchor?.(card) ?? el,
      {
        status,
        contactStage: view?.contactStage,
        hasNote: !!view?.note,
        known: !!view,
        siblings: view?.siblings.length ?? 0,
        siblingsTitle: `Même bien que : ${(view?.siblings ?? []).map((s) => `${siteLabel(s.site)} « ${s.title ?? s.key} »`).join(', ')}`,
        suggestion: best && {
          score: best.score,
          danger: best.otherStatus === 'rejected',
          label: `Probablement la même annonce que ${siteLabel(best.other.site)} « ${best.other.title ?? best.other.key} » — ${best.reasons.join(', ')}. Cliquer pour décider.`,
        },
      },
      {
        onStatus: (s) => this.sync.act({ type: 'setStatus', key, status: s }),
        onDetails: () => showDetails(this.sync, key),
        onSuggestion: () => best && showSuggestion(this.sync, key, best),
      },
    );
    return { hidden, status, suggested: !!best };
  }
}

function isOwnMutation(r: MutationRecord): boolean {
  if (r.type !== 'childList') return false;
  const nodes = [...r.addedNodes, ...r.removedNodes];
  return nodes.length > 0 && nodes.every((n) => n instanceof Element && n.hasAttribute(TOOLBAR_HOST_ATTR));
}

function setAttr(el: Element, name: string, value: string | null): void {
  if (value === null) {
    if (el.hasAttribute(name)) el.removeAttribute(name);
  } else if (el.getAttribute(name) !== value) {
    el.setAttribute(name, value);
  }
}
