/** Ajoute une commande au menu Tampermonkey. Ne fait rien hors d'un userscript. */
export function registerMenuCommand(label: string, onCommand: () => void): void {
  if (typeof GM_registerMenuCommand === 'function') {
    GM_registerMenuCommand(label, onCommand);
  }
}
