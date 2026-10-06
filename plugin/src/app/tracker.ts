/**
 * Orchestrateur dans la page. Il relie l'adaptateur de site, la synchronisation
 * avec le serveur et l'UI.
 *
 *  - Il trouve les cartes d'annonces. Il les trouve de nouveau après chaque
 *    nouveau rendu, car les sites d'annonces sont des SPA.
 *  - Il envoie les cartes au serveur comme observations.
 *  - Il envoie aussi les données enrichies (JSON du site d'annonces).
 *  - Il applique l'état reçu (ou l'état en cache) :
 *     - attributs data-tmrl-*, qui masquent, atténuent ou colorent les cartes.
 *     - une barre d'actions sur chaque carte.
 *  - Sur la page d'une annonce (fiche), il pose un bandeau de suivi au-dessus
 *    du titre. La fiche n'est jamais masquée, même si le bien est rejeté.
 */
import type { SyncEngine } from '../core/sync';
import { listingKey, type ListingView, type PropertyStatus } from '../core/types';
import type { CardRef, DetailRef, SiteAdapter } from '../sites/types';
import { siteLabel } from '../sites';
import { renderToolbar, TOOLBAR_HOST_ATTR } from '../ui/cardToolbar';
import { installPageStyles, setShowHidden } from '../ui/pageStyles';
import { showDetails, showSuggestion } from '../ui/views';

export interface TrackerOptions {
  /** Statuts dont les cartes sont masquées. Default: ['rejected']. */
  hideStatuses: PropertyStatus[];
  /** Une annonce sans statut est aussi masquée si un doublon PROBABLE a l'un de ces statuts. Default: []. */
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
    installPageStyles(this.adapter.hideSelectors);

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
      // Le plugin enregistre uniquement les annonces affichées. L'API renvoie parfois d'autres annonces.
      if (!document.querySelector(`[data-tmrl-card="${CSS.escape(siteId)}"]`)) {
        this.pendingApi.set(siteId, data);
        return;
      }
      this.sync.observe({ site: this.adapter.id, siteId, source: 'api', data, seenAt: Date.now() });
    });

    this.sync.onChange(() => this.schedule());
    // Retour sur l'onglet : le plugin demande de nouveau l'état. Un statut a pu changer sur le serveur local.
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') {
        const ids = this.adapter.findCards(document).map((c) => c.siteId);
        const detail = this.adapter.findDetail?.(document);
        if (detail) ids.push(detail.siteId);
        this.sync.refresh(ids.map((id) => listingKey(this.adapter.id, id)));
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

  /** Change les statuts masqués (réglages du panneau) et réapplique l'affichage. */
  setHideStatuses(statuses: PropertyStatus[]): void {
    this.options.hideStatuses = statuses;
    this.schedule();
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
    const detail = this.adapter.findDetail?.(document);
    if (detail) {
      this.ingestDetail(detail);
      this.apply(detail, true);
    }
    this.counts = counts;
    for (const cb of this.countListeners) cb(counts);
    if (this.options.debug) console.debug('[RechercheLogement] scan', counts);
  }

  /** Envoie la carte au serveur une fois par couple élément/annonce. Les SPA réutilisent les éléments. */
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

  /** Fiche d'annonce : envoyée une fois par annonce (source 'detail', moins précise que le JSON du site). */
  private ingestDetail(detail: DetailRef): void {
    if (this.processed.get(detail.element) === detail.siteId) return;
    this.processed.set(detail.element, detail.siteId);
    detail.element.setAttribute('data-tmrl-card', detail.siteId);
    detail.element.setAttribute('data-tmrl-detail', '');
    const now = Date.now();
    const data = this.adapter.parseDetail?.(detail);
    if (data) this.sync.observe({ site: this.adapter.id, siteId: detail.siteId, source: 'detail', data, seenAt: now });
    const api = this.pendingApi.get(detail.siteId);
    if (api) {
      this.pendingApi.delete(detail.siteId);
      this.sync.observe({ site: this.adapter.id, siteId: detail.siteId, source: 'api', data: api, seenAt: now });
    }
    // État frais : le statut a pu changer depuis la liste (autre onglet, serveur local).
    this.sync.refresh([listingKey(this.adapter.id, detail.siteId)]);
  }

  private apply(card: CardRef | DetailRef, isDetail = false): { hidden: boolean; status: PropertyStatus; suggested: boolean } {
    const key = listingKey(this.adapter.id, card.siteId);
    const el = card.element;
    const view: ListingView | undefined = this.sync.view(key);
    const status = view?.status ?? 'none';
    const best = view?.suggestions[0];

    let hidden = this.options.hideStatuses.includes(status);
    if (!hidden && status === 'none' && best && this.options.hideSuggestedDuplicatesOf.includes(best.otherStatus)) hidden = true;
    if (isDetail) hidden = false;

    setAttr(el, 'data-tmrl-status', status === 'none' ? null : status);
    setAttr(el, 'data-tmrl-hidden', hidden ? '' : null);
    setAttr(el, 'data-tmrl-dup', best ? (best.otherStatus === 'rejected' ? 'rejected' : 'suggested') : null);

    renderToolbar(
      isDetail ? (card as DetailRef).anchor : (this.adapter.toolbarAnchor?.(card as CardRef) ?? el),
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
          label: `Probablement le même bien que ${siteLabel(best.other.site)} « ${best.other.title ?? best.other.key} ». Raisons : ${best.reasons.join(', ')}. Cliquer pour décider.`,
        },
      },
      {
        onStatus: (s) => this.sync.act({ type: 'setStatus', key, status: s }),
        onDetails: () => showDetails(this.sync, key),
        onSuggestion: () => best && showSuggestion(this.sync, key, best),
      },
      isDetail ? 'detail' : 'card',
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
