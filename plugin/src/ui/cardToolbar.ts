/**
 * Barre d'actions en haut de chaque carte d'annonce :
 *
 *   [👁] [✕] [📞]  [⋯]  [🔗 2]  [≈ déjà vue ? 72 %]
 *   [📞 Visite prévue]            <- uniquement pour les biens "à contacter"
 *
 * Sur la page d'une annonce (variante 'detail'), la même barre est un bandeau
 * posé au-dessus du titre, avec des libellés en clair :
 *
 *   🏠 Suivi  [👁 Vue] [✕ Pas intéressé] [📞 À contacter]  [⋯ Détails]  [🔗 2] ...
 *
 * Chaque barre a son propre shadow root. Le CSS du site d'annonces ne
 * s'applique donc pas à la barre. La barre arrête la propagation des clics.
 * Un clic sur la barre n'ouvre donc pas l'annonce.
 */
import type { ContactStage, PropertyStatus } from '../core/types';
import { h } from '../shared/dom/h';
import { CONTACT_STAGE_LABEL, THEME } from './theme';

export const TOOLBAR_HOST_ATTR = 'data-tmrl-toolbar';

export interface ToolbarModel {
  status: PropertyStatus;
  contactStage?: ContactStage;
  hasNote: boolean;
  /** false si le serveur n'a encore jamais répondu pour cette annonce. */
  known: boolean;
  siblings: number;
  siblingsTitle: string;
  suggestion?: { score: number; label: string; danger: boolean };
}

export interface ToolbarHandlers {
  onStatus(status: PropertyStatus): void;
  onDetails(): void;
  onSuggestion(): void;
}

export type ToolbarVariant = 'card' | 'detail';

export function renderToolbar(anchor: HTMLElement, model: ToolbarModel, handlers: ToolbarHandlers, variant: ToolbarVariant = 'card'): void {
  if (variant === 'detail') return renderDetailBar(anchor, model, handlers);
  let host = anchor.querySelector<HTMLElement>(`:scope > [${TOOLBAR_HOST_ATTR}]`);
  if (!host) {
    host = document.createElement('div');
    host.setAttribute(TOOLBAR_HOST_ATTR, '');
    Object.assign(host.style, { position: 'absolute', top: '6px', left: '6px', right: '6px', zIndex: '20', pointerEvents: 'none' });
    for (const ev of ['click', 'mousedown', 'mouseup', 'pointerdown', 'pointerup']) {
      host.addEventListener(ev, (e) => {
        e.stopPropagation();
        if (ev === 'click') e.preventDefault();
      });
    }
    host.attachShadow({ mode: 'open' });
    anchor.appendChild(host);
  }
  const contact = model.status === 'toContact';
  const children: Node[] = [
    h('style', null, `:host{all:initial} button{pointer-events:auto;transition:transform .1s} button:hover{transform:scale(1.08)}`),
    h(
      'div',
      { style: { display: 'flex', flexWrap: 'wrap', gap: '4px', alignItems: 'center', font: `600 12px/1 ${THEME.fontFamily}` } },
      statusButton('👁', 'Vue : garder l\'annonce visible, mais atténuée', 'seen', model.status, THEME.seen, handlers),
      statusButton('✕', 'Pas intéressé : masquer l\'annonce', 'rejected', model.status, THEME.rejected, handlers),
      statusButton('📞', 'Me plaît : à contacter', 'toContact', model.status, THEME.toContact, handlers),
      pill(model.hasNote ? '📝' : '⋯', model.hasNote ? 'Note et détails' : 'Détails et note', THEME.bg, handlers.onDetails),
      model.siblings > 0 && pill(`🔗 ${model.siblings}`, model.siblingsTitle, THEME.accent, handlers.onDetails),
      model.suggestion &&
        pill(
          `≈ ${model.suggestion.danger ? 'déjà masquée' : 'déjà vue'} ? ${Math.round(model.suggestion.score * 100)} %`,
          model.suggestion.label,
          model.suggestion.danger ? THEME.rejected : THEME.suggest,
          handlers.onSuggestion,
        ),
    ),
  ];
  if (contact) {
    children.push(
      h(
        'div',
        { style: { marginTop: '4px' } },
        pill(`📞 ${CONTACT_STAGE_LABEL[model.contactStage ?? 'pending']}`, 'Suivi du contact. Modifier l\'étape sur le serveur local.', THEME.toContact, handlers.onDetails),
      ),
    );
  }
  host.shadowRoot!.replaceChildren(...children);
}

/** Bandeau de la page d'une annonce : premier enfant de l'ancre, dans le flux de la page. */
function renderDetailBar(anchor: HTMLElement, model: ToolbarModel, handlers: ToolbarHandlers): void {
  let host = anchor.querySelector<HTMLElement>(`:scope > [${TOOLBAR_HOST_ATTR}]`);
  if (!host) {
    host = document.createElement('div');
    host.setAttribute(TOOLBAR_HOST_ATTR, 'detail');
    Object.assign(host.style, { display: 'block', position: 'relative', zIndex: '20', margin: '0 0 12px' });
    for (const ev of ['click', 'mousedown', 'mouseup', 'pointerdown', 'pointerup']) host.addEventListener(ev, (e) => e.stopPropagation());
    host.attachShadow({ mode: 'open' });
    anchor.prepend(host);
  } else if (anchor.firstElementChild !== host) {
    anchor.prepend(host);
  }
  const s = model.status;
  const statusText = !model.known
    ? 'Envoi au serveur local...'
    : s === 'rejected'
      ? 'Pas intéressé : masquée dans les listes'
      : s === 'seen'
        ? 'Déjà vue'
        : s === 'toContact'
          ? `📞 ${CONTACT_STAGE_LABEL[model.contactStage ?? 'pending']}`
          : 'Aucun statut';
  const accent = { none: THEME.border, seen: THEME.seen, rejected: THEME.rejected, toContact: THEME.toContact }[s];
  host.shadowRoot!.replaceChildren(
    h('style', null, `:host{all:initial} button{transition:transform .1s} button:hover{transform:scale(1.04)}`),
    h(
      'div',
      {
        style: {
          display: 'flex',
          flexWrap: 'wrap',
          gap: '6px',
          alignItems: 'center',
          padding: '8px 10px',
          borderRadius: '8px',
          background: THEME.bg,
          borderLeft: `5px solid ${accent}`,
          color: THEME.fg,
          font: `600 12px/1.2 ${THEME.fontFamily}`,
          boxShadow: '0 1px 4px rgba(0,0,0,.25)',
        },
      },
      h('span', { style: { marginRight: '4px' } }, '🏠 Suivi'),
      labeledStatus('👁 Vue', 'seen', s, THEME.seen, handlers),
      labeledStatus('✕ Pas intéressé', 'rejected', s, THEME.rejected, handlers),
      labeledStatus('📞 À contacter', 'toContact', s, THEME.toContact, handlers),
      pill(model.hasNote ? '📝 Note' : '⋯ Détails / note', 'Note, annonces associées, lien vers le serveur local', THEME.bgSoft, handlers.onDetails),
      model.siblings > 0 && pill(`🔗 ${model.siblings} autre(s) annonce(s)`, model.siblingsTitle, THEME.accent, handlers.onDetails),
      model.suggestion &&
        pill(
          `≈ ${model.suggestion.danger ? 'déjà masquée' : 'déjà vue'} ? ${Math.round(model.suggestion.score * 100)} %`,
          model.suggestion.label,
          model.suggestion.danger ? THEME.rejected : THEME.suggest,
          handlers.onSuggestion,
        ),
      h('span', { style: { marginLeft: 'auto', color: THEME.muted, fontWeight: '500' } }, statusText),
    ),
  );
}

function labeledStatus(label: string, status: PropertyStatus, current: PropertyStatus, color: string, handlers: ToolbarHandlers): HTMLButtonElement {
  const active = current === status;
  const b = pill(label, active ? 'Statut actuel. Cliquer pour annuler.' : `Passer en « ${label} »`, active ? color : THEME.bgSoft, () => handlers.onStatus(active ? 'none' : status));
  if (active) b.style.border = '2px solid #fff';
  return b;
}

function statusButton(icon: string, title: string, status: PropertyStatus, current: PropertyStatus, color: string, handlers: ToolbarHandlers): HTMLButtonElement {
  const active = current === status;
  return h(
    'button',
    {
      type: 'button',
      title: active ? `${title}. Cliquer pour annuler.` : title,
      style: {
        width: '28px',
        height: '28px',
        borderRadius: '50%',
        border: active ? '2px solid #fff' : `1px solid ${THEME.border}`,
        background: active ? color : 'rgba(17,24,39,.85)',
        color: '#fff',
        cursor: 'pointer',
        font: `14px/1 ${THEME.fontFamily}`,
        padding: '0',
        boxShadow: '0 1px 4px rgba(0,0,0,.4)',
      },
      on: { click: () => handlers.onStatus(active ? 'none' : status) },
    },
    icon,
  );
}

function pill(text: string, title: string, bg: string, onClick: () => void): HTMLButtonElement {
  return h(
    'button',
    {
      type: 'button',
      title,
      style: {
        height: '28px',
        padding: '0 9px',
        borderRadius: '14px',
        border: `1px solid ${THEME.border}`,
        background: bg,
        color: '#fff',
        cursor: 'pointer',
        font: `600 12px/1 ${THEME.fontFamily}`,
        boxShadow: '0 1px 4px rgba(0,0,0,.4)',
        whiteSpace: 'nowrap',
      },
      on: { click: onClick },
    },
    text,
  );
}
