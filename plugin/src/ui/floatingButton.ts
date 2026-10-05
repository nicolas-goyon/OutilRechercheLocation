/** Bouton flottant fixe qui ouvre le panneau, avec un badge (nombre d'annonces masquées sur la page). */
import { getUIRoot } from '../shared/dom/uiRoot';
import { THEME } from './theme';

export interface FloatingButtonOptions {
  id: string;
  label: string;
  title?: string;
  onClick: () => void;
  /** Distance from the bottom-right corner, in pixels. Default: 16/16. */
  offset?: { right?: number; bottom?: number };
}

export interface FloatingButtonHandle {
  setBadge(text: string | null, color?: string): void;
}

export function installFloatingButton(options: FloatingButtonOptions): FloatingButtonHandle {
  const root = getUIRoot();
  root.getElementById(options.id)?.remove();

  const button = document.createElement('button');
  button.id = options.id;
  button.type = 'button';
  button.textContent = options.label;
  button.title = options.title ?? options.label;
  button.setAttribute('aria-label', options.title ?? options.label);
  Object.assign(button.style, {
    position: 'fixed',
    right: `${options.offset?.right ?? 16}px`,
    bottom: `${options.offset?.bottom ?? 16}px`,
    zIndex: '2147483646',
    width: '48px',
    height: '48px',
    borderRadius: '50%',
    border: 'none',
    background: THEME.accent,
    color: THEME.fg,
    cursor: 'pointer',
    font: `20px/1 ${THEME.fontFamily}`,
    boxShadow: '0 4px 14px rgba(0,0,0,.35)',
  } satisfies Partial<CSSStyleDeclaration>);
  button.addEventListener('click', options.onClick);

  const badge = document.createElement('span');
  Object.assign(badge.style, {
    position: 'absolute',
    top: '-4px',
    right: '-4px',
    minWidth: '18px',
    height: '18px',
    padding: '0 5px',
    borderRadius: '9px',
    background: THEME.rejected,
    color: '#fff',
    font: `700 11px/18px ${THEME.fontFamily}`,
    display: 'none',
    boxSizing: 'border-box',
  } satisfies Partial<CSSStyleDeclaration>);
  button.appendChild(badge);
  root.appendChild(button);

  return {
    setBadge(text, color) {
      badge.style.display = text ? 'block' : 'none';
      badge.textContent = text ?? '';
      if (color) badge.style.background = color;
    },
  };
}
