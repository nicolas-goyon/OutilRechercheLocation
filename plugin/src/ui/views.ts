/**
 * Contenus des modales du plugin. Le plugin reste volontairement léger :
 * la vue complète (comparaison détaillée, suivi des contacts, historique)
 * est sur le site local, vers lequel chaque vue propose un lien.
 */
import type { SyncEngine } from '../core/sync';
import type { ListingKey, ListingRef, PropertyStatus, SuggestionView } from '../core/types';
import { h } from '../shared/dom/h';
import { siteLabel } from '../sites';
import { closeModal, showModal } from './modal';
import { buttonStyle, CONTACT_STAGE_LABEL, STATUS_LABEL, THEME } from './theme';

const fmtPrice = (n: number | undefined) => (n === undefined ? '' : `${n.toLocaleString('fr-FR')} €`);

function section(title: string, ...children: (Node | string | false | null | undefined)[]): HTMLElement {
  return h(
    'section',
    { style: { marginBottom: '18px' } },
    h('h3', { style: { margin: '0 0 8px', font: `600 13px/1.3 ${THEME.fontFamily}`, color: THEME.muted, textTransform: 'uppercase', letterSpacing: '.04em' } }, title),
    ...children,
  );
}

function button(label: string, onClick: () => void, bg?: string): HTMLButtonElement {
  return h('button', { type: 'button', style: buttonStyle(bg), on: { click: onClick } }, label);
}

function link(ref: ListingRef): HTMLElement {
  const label = `${siteLabel(ref.site)} « ${ref.title ?? ref.key} » ${fmtPrice(ref.price)}`;
  return ref.url ? h('a', { href: ref.url, target: '_blank', rel: 'noopener', style: { color: '#93c5fd' } }, `${label} ↗`) : h('span', null, label);
}

function webLink(url: string | undefined, label = 'Ouvrir sur le site local ↗'): HTMLElement | null {
  return url ? h('a', { href: url, target: '_blank', rel: 'noopener', style: { color: '#93c5fd' } }, label) : null;
}

function statusBadge(status: PropertyStatus): HTMLElement {
  const color = { none: THEME.bgSoft, seen: THEME.seen, rejected: THEME.rejected, toContact: THEME.toContact }[status];
  return h('span', { style: { background: color, borderRadius: '4px', padding: '2px 6px', font: `600 11px/1.4 ${THEME.fontFamily}` } }, STATUS_LABEL[status]);
}

// ------------------------------------------------------------------ doublon probable

export function showSuggestion(sync: SyncEngine, key: ListingKey, s: SuggestionView): void {
  showModal({
    title: `Même bien ? (score ${Math.round(s.score * 100)} %)`,
    content: h(
      'div',
      null,
      section('Autre annonce', h('div', null, link(s.other)), h('div', { style: { marginTop: '6px' } }, 'Statut : ', statusBadge(s.otherStatus))),
      section('Pourquoi', h('ul', { style: { margin: '0', paddingLeft: '18px' } }, s.reasons.map((r) => h('li', null, r)))),
      h(
        'div',
        { style: { display: 'flex', gap: '8px', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap' } },
        webLink(sync.view(key)?.webUrl, 'Comparer en détail sur le site local ↗'),
        h(
          'div',
          { style: { display: 'flex', gap: '8px' } },
          button('Pas le même bien', () => {
            sync.act({ type: 'dismissDuplicate', key, otherKey: s.other.key });
            closeModal();
          }),
          button('✔ Même bien', () => {
            sync.act({ type: 'confirmDuplicate', key, otherKey: s.other.key });
            closeModal();
          }, THEME.ok),
        ),
      ),
    ),
  });
}

// ------------------------------------------------------------------ détails

export function showDetails(sync: SyncEngine, key: ListingKey): void {
  const v = sync.view(key);
  const status = v?.status ?? 'none';
  const note = h('textarea', {
    value: v?.note ?? '',
    placeholder: 'Note personnelle (contact, visite, impressions...)',
    rows: 4,
    style: { width: '100%', boxSizing: 'border-box', background: THEME.bgSoft, color: THEME.fg, border: `1px solid ${THEME.border}`, borderRadius: '6px', padding: '8px', font: THEME.font },
  });
  const statuses: PropertyStatus[] = ['none', 'seen', 'rejected', 'toContact'];

  showModal({
    title: 'Annonce',
    content: h(
      'div',
      null,
      section(
        'Statut du bien',
        h('div', { style: { display: 'flex', gap: '6px', flexWrap: 'wrap' } }, statuses.map((s) =>
          button(STATUS_LABEL[s], () => {
            sync.act({ type: 'setStatus', key, status: s });
            showDetails(sync, key);
          }, s === status ? THEME.accent : undefined),
        )),
        status === 'toContact' && h('div', { style: { marginTop: '8px' } }, `Suivi : ${CONTACT_STAGE_LABEL[v?.contactStage ?? 'pending']} (à mettre à jour sur le site local)`),
      ),
      section('Note', note, h('div', { style: { marginTop: '6px', textAlign: 'right' } }, button('Enregistrer la note', () => {
        sync.act({ type: 'setNote', key, note: note.value.trim() });
        closeModal();
      }, THEME.ok))),
      section(
        `Même bien sur d'autres annonces (${v?.siblings.length ?? 0})`,
        !v?.siblings.length
          ? h('div', { style: { color: THEME.muted } }, 'Aucune annonce associée.')
          : h('ul', { style: { margin: '0', paddingLeft: '18px' } }, v.siblings.map((s) => h('li', { style: { marginBottom: '4px' } }, link(s)))),
        !!v?.siblings.length && h('div', { style: { marginTop: '6px' } }, button('Dissocier cette annonce du bien', () => {
          sync.act({ type: 'detach', key });
          closeModal();
        })),
      ),
      !!v?.suggestions.length && section(
        `Doublons possibles (${v.suggestions.length})`,
        h('ul', { style: { margin: '0', paddingLeft: '18px' } }, v.suggestions.map((s) =>
          h('li', { style: { marginBottom: '4px' } }, link(s.other), ` — ${Math.round(s.score * 100)} % `, statusBadge(s.otherStatus), ' ',
            button('Décider', () => showSuggestion(sync, key, s))),
        )),
      ),
      h('div', { style: { display: 'flex', justifyContent: 'space-between', color: THEME.muted, fontSize: '11px' } }, `Clé : ${key}`, webLink(v?.webUrl) ?? ''),
    ),
  });
}

// ------------------------------------------------------------------ panneau principal

export interface PanelContext {
  sync: SyncEngine;
  serverUrl: string;
  pageCounts(): { total: number; hidden: number; seen: number; toContact: number; suggested: number };
  showHidden: boolean;
  setShowHidden(v: boolean): void;
}

export function showPanel(ctx: PanelContext): void {
  const { sync } = ctx;
  const page = ctx.pageCounts();
  const toggle = h('input', { type: 'checkbox', checked: ctx.showHidden, on: { change: () => ctx.setShowHidden(toggle.checked) } });

  const stateText = {
    online: ['🟢', 'Connecté au serveur local'],
    offline: ['🟠', 'Serveur local injoignable — les actions sont gardées et seront envoyées à son retour'],
    unauthorized: ['🔴', 'Token refusé — vérifier apiToken dans le script Tampermonkey (Paramètres du site local)'],
    unknown: ['⚪', 'Connexion en cours...'],
  }[sync.state];

  showModal({
    title: '🏠 Suivi de recherche logement',
    content: h(
      'div',
      null,
      section(
        'Serveur',
        h('div', null, `${stateText[0]} ${stateText[1]}`),
        sync.lastError && sync.state !== 'online' && h('div', { style: { color: THEME.muted, fontSize: '11px' } }, sync.lastError),
        h('div', { style: { marginTop: '4px' } }, `${sync.pendingCount()} élément(s) en attente d'envoi`),
        h('div', { style: { display: 'flex', gap: '8px', marginTop: '8px', alignItems: 'center' } },
          button('Réessayer maintenant', () => {
            sync.retryNow();
            setTimeout(() => showPanel(ctx), 1500);
          }),
          webLink(ctx.serverUrl, 'Ouvrir le site local ↗'),
        ),
      ),
      section(
        'Cette page',
        h('div', null, `${page.total} annonces · ${page.hidden} masquées · ${page.seen} vues · ${page.toContact} à contacter · ${page.suggested} doublons possibles`),
        h('label', { style: { display: 'flex', gap: '8px', alignItems: 'center', marginTop: '8px', cursor: 'pointer' } }, toggle, 'Afficher les annonces masquées (en pointillés)'),
      ),
    ),
  });
}
