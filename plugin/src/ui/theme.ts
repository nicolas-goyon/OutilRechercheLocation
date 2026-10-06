/** Couleurs et polices de toute l'UI injectée. */
const fontFamily = 'system-ui, -apple-system, "Segoe UI", sans-serif';

export const THEME = {
  fontFamily,
  font: `13px/1.45 ${fontFamily}`,
  bg: '#111827',
  bgSoft: '#1f2937',
  fg: '#f9fafb',
  muted: '#9ca3af',
  border: 'rgba(255,255,255,.12)',
  accent: '#2563eb',
  seen: '#6b7280',
  rejected: '#dc2626',
  toContact: '#16a34a',
  suggest: '#d97706',
  ok: '#16a34a',
} as const;

export const STATUS_LABEL = {
  none: 'Aucun statut',
  seen: 'Vue',
  rejected: 'Pas intéressé (masquée)',
  toContact: 'Me plaît : à contacter',
} as const;

export const CONTACT_STAGE_LABEL = {
  pending: 'À contacter',
  contacted: 'Agence contactée',
  visitScheduled: 'Visite prévue',
  visited: 'Visitée',
  applicationSent: 'Dossier envoyé',
  accepted: 'Dossier accepté',
  declined: 'Refusé',
} as const;

export function buttonStyle(bg: string = THEME.bgSoft): Partial<CSSStyleDeclaration> {
  return {
    border: `1px solid ${THEME.border}`,
    background: bg,
    color: THEME.fg,
    borderRadius: '6px',
    padding: '6px 10px',
    cursor: 'pointer',
    font: `500 12px/1.2 ${fontFamily}`,
  };
}
