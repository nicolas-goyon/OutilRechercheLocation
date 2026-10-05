/**
 * Toute l'UI "globale" (bouton flottant, modale) est montée dans ce shadow
 * root unique, pour que le CSS du site ne s'y applique pas (et inversement).
 * Les barres d'actions par carte ont chacune leur propre petit shadow root
 * (voir ui/cardToolbar.ts) puisqu'elles vivent à l'intérieur des cartes.
 */
const HOST_ID = 'tmrl-ui-root';

let root: ShadowRoot | null = null;

export function getUIRoot(): ShadowRoot {
  if (root && root.host.isConnected) return root;

  const host = document.createElement('div');
  host.id = HOST_ID;
  document.body.appendChild(host);

  root = host.attachShadow({ mode: 'open' });

  const reset = document.createElement('style');
  reset.textContent = ':host { all: initial; }';
  root.appendChild(reset);

  return root;
}
