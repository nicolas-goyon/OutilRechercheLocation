/**
 * Seul CSS injecté directement dans la page. Il agit uniquement sur les
 * attributs data-tmrl-* que le plugin ajoute aux cartes.
 *
 * Le framework du site d'annonces (Vue.js pour Bien'ici) ne change pas ces
 * attributs inconnus pendant un nouveau rendu. Il change par contre `style`
 * et `class`.
 */
const STYLE_ID = 'tmrl-page-styles';

/**
 * @param hideSelectors emplacements publicitaires du site (voir SiteAdapter.hideSelectors).
 *   Avec un bloqueur de pub, ces blocs restent vides et laissent des trous dans la liste.
 */
export function installPageStyles(hideSelectors: string[] = []): void {
  if (document.getElementById(STYLE_ID)) return;
  const style = document.createElement('style');
  style.id = STYLE_ID;
  style.textContent = pageCss(hideSelectors);
  (document.head ?? document.documentElement).appendChild(style);
}

/** CSS injecté. Les fiches (data-tmrl-detail) ne sont jamais masquées ni atténuées. */
export function pageCss(hideSelectors: string[] = []): string {
  const card = '[data-tmrl-card]:not([data-tmrl-detail])';
  const hide = hideSelectors.length ? `${hideSelectors.join(',\n')} { display: none !important; }\n` : '';
  return `
${card} { position: relative !important; }
${card}[data-tmrl-hidden] { display: none !important; }
html[data-tmrl-show-hidden] ${card}[data-tmrl-hidden] {
  display: block !important; opacity: .4; outline: 2px dashed #dc2626; outline-offset: 2px;
}
${card}[data-tmrl-status="seen"] { opacity: .55; transition: opacity .15s; }
${card}[data-tmrl-status="seen"]:hover { opacity: 1; }
${card}[data-tmrl-status="toContact"] { outline: 4px solid #16a34a; outline-offset: 2px; border-radius: 6px; box-shadow: 0 0 0 8px rgba(22,163,74,.18); }
${card}[data-tmrl-dup="rejected"]:not([data-tmrl-status]) { outline: 3px dashed #d97706; outline-offset: 2px; }
${hide}`;
}

export function setShowHidden(show: boolean): void {
  document.documentElement.toggleAttribute('data-tmrl-show-hidden', show);
}
