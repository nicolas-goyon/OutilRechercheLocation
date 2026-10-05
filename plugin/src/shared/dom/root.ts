/** Returns the page's real window, bypassing the Tampermonkey sandbox when present. */
export function getRootWindow(): Window & typeof globalThis {
  return (typeof unsafeWindow !== 'undefined' && unsafeWindow ? unsafeWindow : window) as Window & typeof globalThis;
}
