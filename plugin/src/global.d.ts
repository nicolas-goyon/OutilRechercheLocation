export {};

declare global {
  /** esbuild remplace cette valeur au build (`define` dans scripts/build.mjs). */
  const __PLUGIN_VERSION__: string;

  /**
   * Fourni par Tampermonkey/Greasemonkey avec @grant unsafeWindow. Absent hors
   * d'un userscript. Le type est donc optionnel.
   */
  const unsafeWindow: (Window & typeof globalThis) | undefined;

  /** Fourni par @grant GM_registerMenuCommand. Ajoute une entrée au menu Tampermonkey. */
  function GM_registerMenuCommand(name: string, callback: () => void, accessKey?: string): number;

  /** @grant GM_getValue / GM_setValue. Stockage persistant du script. Tous les @match du script partagent ce stockage. */
  function GM_getValue<T>(name: string, defaultValue: T): T;
  function GM_setValue<T>(name: string, value: T): void;

  /** @grant GM_addValueChangeListener. Synchronise les onglets. */
  function GM_addValueChangeListener(
    name: string,
    listener: (name: string, oldValue: unknown, newValue: unknown, remote: boolean) => void,
  ): number;
  function GM_removeValueChangeListener(listenerId: number): void;

  /**
   * @grant GM_xmlhttpRequest (+ @connect localhost). Envoie des requêtes au
   * serveur local. Le CORS et le contenu mixte (https -> http) ne bloquent pas
   * ces requêtes.
   */
  function GM_xmlhttpRequest(details: {
    method: 'GET' | 'POST' | 'PUT' | 'DELETE';
    url: string;
    headers?: Record<string, string>;
    data?: string;
    timeout?: number;
    responseType?: 'text' | 'json';
    onload?: (response: { status: number; responseText: string }) => void;
    onerror?: (error: unknown) => void;
    ontimeout?: () => void;
  }): { abort(): void };
}
