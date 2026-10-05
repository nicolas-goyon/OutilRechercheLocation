export {};

declare global {
  /**
   * Provided by Tampermonkey/Greasemonkey via @grant unsafeWindow. Absent
   * outside a userscript context, hence the optional typing.
   */
  const unsafeWindow: (Window & typeof globalThis) | undefined;

  /** Provided by @grant GM_registerMenuCommand. Adds an entry to the Tampermonkey menu. */
  function GM_registerMenuCommand(name: string, callback: () => void, accessKey?: string): number;

  /** @grant GM_getValue / GM_setValue — stockage persistant propre au script (partagé entre tous ses @match). */
  function GM_getValue<T>(name: string, defaultValue: T): T;
  function GM_setValue<T>(name: string, value: T): void;

  /** @grant GM_addValueChangeListener — synchronisation entre onglets. */
  function GM_addValueChangeListener(
    name: string,
    listener: (name: string, oldValue: unknown, newValue: unknown, remote: boolean) => void,
  ): number;
  function GM_removeValueChangeListener(listenerId: number): void;

  /**
   * @grant GM_xmlhttpRequest (+ @connect localhost) — requêtes vers le serveur
   * local sans être bloqué par le CORS / le contenu mixte de la page (https -> http).
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
