/** Renvoie la vraie window de la page. Contourne le sandbox Tampermonkey s'il existe. */
export function getRootWindow(): Window & typeof globalThis {
  return (typeof unsafeWindow !== 'undefined' && unsafeWindow ? unsafeWindow : window) as Window & typeof globalThis;
}
