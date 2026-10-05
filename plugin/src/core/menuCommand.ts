/** Registers a Tampermonkey menu command; no-op outside a userscript context. */
export function registerMenuCommand(label: string, onCommand: () => void): void {
  if (typeof GM_registerMenuCommand === 'function') {
    GM_registerMenuCommand(label, onCommand);
  }
}
