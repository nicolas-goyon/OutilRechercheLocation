/**
 * Modale centrée générique : titre + contenu + "✕". Se ferme sur la croix,
 * un clic sur le fond ou Échap. Montée dans le shadow root commun.
 */
import { getUIRoot } from '../shared/dom/uiRoot';
import { THEME } from './theme';

const BACKDROP_ID = 'tmrl-modal-backdrop';

export interface ShowModalOptions {
  title: string;
  content: Node | string;
  /** Largeur CSS. Default: min(560px, 94vw). */
  width?: string;
  onClose?: () => void;
}

let backdropEl: HTMLDivElement | null = null;
let keydownHandler: ((event: KeyboardEvent) => void) | null = null;
let onCloseCb: (() => void) | undefined;

/** Ouvre la modale, en remplaçant celle éventuellement ouverte. */
export function showModal(options: ShowModalOptions): void {
  closeModal();
  const root = getUIRoot();

  const backdrop = document.createElement('div');
  backdrop.id = BACKDROP_ID;
  Object.assign(backdrop.style, {
    position: 'fixed',
    inset: '0',
    zIndex: '2147483647',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    background: 'rgba(0,0,0,.5)',
    font: THEME.font,
  } satisfies Partial<CSSStyleDeclaration>);
  backdrop.addEventListener('click', (event) => {
    if (event.target === backdrop) closeModal();
  });

  const box = document.createElement('div');
  Object.assign(box.style, {
    width: options.width ?? 'min(560px, 94vw)',
    maxHeight: '86vh',
    display: 'flex',
    flexDirection: 'column',
    background: THEME.bg,
    color: THEME.fg,
    borderRadius: '12px',
    boxShadow: '0 8px 30px rgba(0,0,0,.4)',
  } satisfies Partial<CSSStyleDeclaration>);

  const header = document.createElement('div');
  Object.assign(header.style, {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    padding: '12px 8px 12px 16px',
    borderBottom: `1px solid ${THEME.border}`,
  } satisfies Partial<CSSStyleDeclaration>);

  const title = document.createElement('strong');
  title.textContent = options.title;
  title.style.font = `600 15px/1.4 ${THEME.fontFamily}`;

  const closeButton = document.createElement('button');
  closeButton.type = 'button';
  closeButton.textContent = '✕';
  closeButton.setAttribute('aria-label', 'Fermer');
  Object.assign(closeButton.style, {
    border: 'none',
    background: 'transparent',
    color: THEME.muted,
    cursor: 'pointer',
    padding: '6px 10px',
    font: `16px/1 ${THEME.fontFamily}`,
  } satisfies Partial<CSSStyleDeclaration>);
  closeButton.addEventListener('click', closeModal);

  header.append(title, closeButton);

  const body = document.createElement('div');
  Object.assign(body.style, { padding: '16px', overflowY: 'auto' } satisfies Partial<CSSStyleDeclaration>);
  body.append(options.content);

  box.append(header, body);
  backdrop.appendChild(box);
  root.appendChild(backdrop);
  backdropEl = backdrop;
  onCloseCb = options.onClose;

  keydownHandler = (event) => {
    if (event.key === 'Escape') closeModal();
  };
  document.addEventListener('keydown', keydownHandler, true);
}

export function isModalOpen(): boolean {
  return backdropEl !== null;
}

export function closeModal(): void {
  if (!backdropEl) return;
  backdropEl.remove();
  backdropEl = null;
  if (keydownHandler) {
    document.removeEventListener('keydown', keydownHandler, true);
    keydownHandler = null;
  }
  const cb = onCloseCb;
  onCloseCb = undefined;
  cb?.();
}
