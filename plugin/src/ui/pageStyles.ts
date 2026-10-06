/**
 * Seul CSS injecté directement dans la page. Il agit uniquement sur les
 * attributs data-tmrl-* que le plugin ajoute aux cartes.
 *
 * Le framework du site d'annonces (Vue.js pour Bien'ici) ne change pas ces
 * attributs inconnus pendant un nouveau rendu. Il change par contre `style`
 * et `class`.
 */
const STYLE_ID = 'tmrl-page-styles';

export function installPageStyles(): void {
  if (document.getElementById(STYLE_ID)) return;
  const style = document.createElement('style');
  style.id = STYLE_ID;
  style.textContent = `
[data-tmrl-card] { position: relative !important; }
[data-tmrl-hidden] { display: none !important; }
html[data-tmrl-show-hidden] [data-tmrl-hidden] {
  display: block !important; opacity: .4; outline: 2px dashed #dc2626; outline-offset: 2px;
}
[data-tmrl-status="seen"] { opacity: .55; transition: opacity .15s; }
[data-tmrl-status="seen"]:hover { opacity: 1; }
[data-tmrl-status="toContact"] { outline: 4px solid #16a34a; outline-offset: 2px; border-radius: 6px; box-shadow: 0 0 0 8px rgba(22,163,74,.18); }
[data-tmrl-dup="rejected"]:not([data-tmrl-status]) { outline: 3px dashed #d97706; outline-offset: 2px; }
`;
  (document.head ?? document.documentElement).appendChild(style);
}

export function setShowHidden(show: boolean): void {
  document.documentElement.toggleAttribute('data-tmrl-show-hidden', show);
}
