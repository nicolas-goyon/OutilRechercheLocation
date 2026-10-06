/**
 * Toute l'UI "globale" (bouton flottant, modale) est dans ce shadow root
 * unique. Le CSS du site d'annonces ne s'applique donc pas à l'UI. Le CSS de
 * l'UI ne s'applique pas au site d'annonces.
 *
 * Chaque barre d'actions a son propre shadow root (voir ui/cardToolbar.ts),
 * car elle est à l'intérieur d'une carte.
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
