/**
 * Contenu des modales du plugin.
 *
 * Le plugin reste léger par choix. La vue complète (comparaison détaillée,
 * suivi des contacts, historique) est sur le serveur local. Chaque vue du
 * plugin contient un lien vers le serveur local.
 */
import type { ConnectionSettings, ConnectionStore } from '../core/connection';
import type { SyncEngine } from '../core/sync';
import type { ListingKey, ListingRef, PropertyStatus, SavedSearchView, SuggestionView } from '../core/types';
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

function webLink(url: string | undefined, label = 'Ouvrir sur le serveur local ↗'): HTMLElement | null {
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
        webLink(sync.view(key)?.webUrl, 'Comparer en détail sur le serveur local ↗'),
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
    placeholder: 'Note personnelle (contact, visite, avis...)',
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
        status === 'toContact' && h('div', { style: { marginTop: '8px' } }, `Suivi : ${CONTACT_STAGE_LABEL[v?.contactStage ?? 'pending']}. Modifier l'étape sur le serveur local.`),
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
        `Doublons probables (${v.suggestions.length})`,
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
  connection: ConnectionStore;
  /** Teste une URL et un token sans les enregistrer. */
  testConnection(settings: ConnectionSettings): Promise<string>;
  version: string;
  pageCounts(): { total: number; hidden: number; seen: number; toContact: number; suggested: number };
  showHidden: boolean;
  setShowHidden(v: boolean): void;
  /** Recherches favorites (serveur local). Absent : la section n'est pas affichée. */
  searches?: {
    /** La page affichée est une page de résultats : on peut l'enregistrer. */
    canSaveCurrent: boolean;
    defaultName: string;
    site: string;
    save(name: string): Promise<SavedSearchView>;
    list(): Promise<SavedSearchView[]>;
  };
}

export function showPanel(ctx: PanelContext): void {
  const { sync } = ctx;
  const page = ctx.pageCounts();
  const conn = ctx.connection.get();
  const toggle = h('input', { type: 'checkbox', checked: ctx.showHidden, on: { change: () => ctx.setShowHidden(toggle.checked) } });

  const stateText = {
    online: ['🟢', 'Connecté au serveur local.'],
    offline: ['🟠', 'Le serveur local ne répond pas. Le plugin garde les actions. Il les envoie quand le serveur répond de nouveau.'],
    unauthorized: ['🔴', 'Le serveur local refuse le token. Copier de nouveau le token depuis la page Paramètres du serveur local.'],
    unconfigured: ['🔴', 'Token absent. Coller le token ci-dessous (page Paramètres du serveur local).'],
    unknown: ['⚪', 'Connexion en cours...'],
  }[sync.state];

  const input = (value: string, placeholder: string, type = 'text') =>
    h('input', {
      value,
      placeholder,
      type,
      spellcheck: false,
      style: { width: '100%', boxSizing: 'border-box', background: THEME.bgSoft, color: THEME.fg, border: `1px solid ${THEME.border}`, borderRadius: '6px', padding: '7px 8px', font: THEME.font },
    });
  const urlInput = input(conn.serverUrl, 'http://localhost:5080');
  const tokenInput = input(conn.token, 'Token (page Paramètres du serveur local)', 'password');
  const feedback = h('div', { style: { minHeight: '18px', marginTop: '6px', fontSize: '12px' } });
  const current = (): ConnectionSettings => ({ serverUrl: urlInput.value, token: tokenInput.value });
  const say = (text: string, color: string) => {
    feedback.textContent = text;
    feedback.style.color = color;
  };

  showModal({
    title: '🏠 Suivi de recherche logement',
    content: h(
      'div',
      null,
      section(
        'Connexion au serveur local',
        h('div', null, `${stateText[0]} ${stateText[1]}`),
        sync.lastError && !['online', 'unconfigured'].includes(sync.state) && h('div', { style: { color: THEME.muted, fontSize: '11px' } }, sync.lastError),
        h('div', { style: { marginTop: '4px', color: THEME.muted } }, `${sync.pendingCount()} élément(s) en attente d'envoi`),
        h('div', { style: { display: 'grid', gridTemplateColumns: '60px 1fr', gap: '6px 8px', alignItems: 'center', marginTop: '10px' } },
          h('label', null, 'URL'), urlInput,
          h('label', null, 'Token'), tokenInput,
        ),
        feedback,
        h('div', { style: { display: 'flex', gap: '8px', marginTop: '4px', alignItems: 'center', flexWrap: 'wrap' } },
          button('Tester', () => {
            say('Test en cours...', THEME.muted);
            ctx.testConnection(current()).then((ok) => say(`✔ ${ok}`, THEME.ok), (e: Error) => say(`✕ ${e.message}`, THEME.rejected));
          }),
          button('Enregistrer', () => {
            ctx.connection.save(current());
            say('Réglages enregistrés. Synchronisation relancée.', THEME.ok);
            setTimeout(() => showPanel(ctx), 1500);
          }, THEME.ok),
          webLink(`${conn.serverUrl}/parametres`, 'Obtenir le token ↗'),
        ),
      ),
      ctx.searches && searchesSection(ctx.searches, conn.serverUrl),
      section(
        'Cette page',
        h('div', null, `${page.total} annonces · ${page.hidden} masquées · ${page.seen} vues · ${page.toContact} à contacter · ${page.suggested} doublons probables`),
        h('label', { style: { display: 'flex', gap: '8px', alignItems: 'center', marginTop: '8px', cursor: 'pointer' } }, toggle, 'Afficher les annonces masquées (en pointillés)'),
      ),
      h('div', { style: { display: 'flex', justifyContent: 'space-between', color: THEME.muted, fontSize: '11px' } },
        `Plugin v${ctx.version}`,
        webLink(conn.serverUrl, 'Ouvrir le serveur local ↗') ?? '',
      ),
    ),
  });
}

// ------------------------------------------------------------------ recherches favorites

function searchesSection(s: NonNullable<PanelContext['searches']>, serverUrl: string): HTMLElement {
  const nameInput = h('input', {
    value: s.defaultName,
    placeholder: 'Nom de la recherche',
    style: { flex: '1', minWidth: '0', background: THEME.bgSoft, color: THEME.fg, border: `1px solid ${THEME.border}`, borderRadius: '6px', padding: '7px 8px', font: THEME.font },
  });
  const feedback = h('div', { style: { minHeight: '16px', marginTop: '6px', fontSize: '12px' } });
  const list = h('ul', { style: { margin: '8px 0 0', paddingLeft: '18px' } });

  const refresh = () =>
    s.list().then(
      (all) => {
        const mine = all.filter((x) => x.site === s.site);
        list.replaceChildren(
          ...(mine.length
            ? mine.map((x) =>
                h('li', { style: { marginBottom: '3px' } }, h('a', { href: x.url, title: x.url, style: { color: '#93c5fd' } }, x.name), x.note ? h('span', { style: { color: THEME.muted } }, ` — ${x.note}`) : null),
              )
            : [h('li', { style: { color: THEME.muted, listStyle: 'none', marginLeft: '-18px' } }, 'Aucune recherche enregistrée pour ce site.')]),
        );
      },
      () => list.replaceChildren(h('li', { style: { color: THEME.muted, listStyle: 'none', marginLeft: '-18px' } }, 'Liste indisponible (serveur local injoignable).')),
    );
  void refresh();

  return section(
    '⭐ Recherches favorites',
    s.canSaveCurrent &&
      h(
        'div',
        { style: { display: 'flex', gap: '8px', alignItems: 'center' } },
        nameInput,
        button('⭐ Enregistrer cette recherche', () => {
          feedback.textContent = 'Enregistrement...';
          feedback.style.color = THEME.muted;
          s.save(nameInput.value.trim()).then(
            (x) => {
              feedback.textContent = `✔ « ${x.name} » enregistrée. Vous pouvez enregistrer plusieurs variantes.`;
              feedback.style.color = THEME.ok;
              void refresh();
            },
            (e: Error) => {
              feedback.textContent = `✕ ${e.message}`;
              feedback.style.color = THEME.rejected;
            },
          );
        }, THEME.ok),
      ),
    s.canSaveCurrent && feedback,
    list,
    h('div', { style: { marginTop: '6px' } }, webLink(`${serverUrl}/recherches`, 'Gérer les recherches sur le serveur local ↗')),
  );
}
