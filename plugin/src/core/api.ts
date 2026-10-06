/**
 * Client HTTP vers le serveur local.
 *
 * Les pages des sites d'annonces sont en https. Le serveur local est en
 * http://localhost. Le navigateur bloque donc un fetch() depuis la page
 * (contenu mixte, CORS, Private Network Access).
 *
 * GM_xmlhttpRequest passe par l'extension et n'a pas ces restrictions. Le
 * script Tampermonkey déclare donc `@grant GM_xmlhttpRequest` et
 * `@connect localhost`.
 *
 * Chaque requête contient `Authorization: Bearer <token>`. Le serveur refuse
 * tout appel /api/* sans le bon token.
 */
import type { SavedSearchRequest, SavedSearchView, SyncRequest, SyncResponse } from './types';

export class ApiError extends Error {
  constructor(
    message: string,
    /** 0 = le serveur ne répond pas (erreur réseau ou délai dépassé). */
    readonly status: number,
  ) {
    super(message);
  }
}

export interface ApiClientOptions {
  baseUrl: string;
  token: string;
  timeoutMs?: number;
}

export interface ServerInfo {
  name: string;
  version: string;
  webUrl: string;
}

export class ApiClient {
  private options: ApiClientOptions;

  constructor(options: ApiClientOptions) {
    this.options = { ...options, baseUrl: options.baseUrl.replace(/\/+$/, '') };
  }

  get baseUrl(): string {
    return this.options.baseUrl;
  }

  /** Change l'URL et le token (après une modification des réglages dans le panneau). */
  configure(baseUrl: string, token: string): void {
    this.options = { ...this.options, baseUrl: baseUrl.replace(/\/+$/, ''), token };
  }

  hasToken(): boolean {
    return this.options.token.length > 0;
  }

  sync(request: SyncRequest): Promise<SyncResponse> {
    return this.request<SyncResponse>('POST', '/api/sync', request);
  }

  ping(): Promise<ServerInfo> {
    return this.request<ServerInfo>('GET', '/api/ping');
  }

  /** Recherches favorites enregistrées sur le serveur local. */
  searches(): Promise<SavedSearchView[]> {
    return this.request<SavedSearchView[]>('GET', '/api/searches');
  }

  /** Enregistre une recherche favorite. Les doublons sont permis (plusieurs variantes d'une même recherche). */
  addSearch(search: SavedSearchRequest): Promise<SavedSearchView> {
    return this.request<SavedSearchView>('POST', '/api/searches', search);
  }

  private request<T>(method: 'GET' | 'POST', path: string, body?: unknown): Promise<T> {
    const url = this.baseUrl + path;
    const headers: Record<string, string> = {
      Accept: 'application/json',
      Authorization: `Bearer ${this.options.token}`,
    };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const data = body === undefined ? undefined : JSON.stringify(body);
    const timeout = this.options.timeoutMs ?? 10_000;

    const parse = (status: number, text: string): T => {
      if (status === 401) throw new ApiError('Le serveur refuse le token.', 401);
      if (status < 200 || status >= 300) throw new ApiError(`HTTP ${status}: ${text.slice(0, 200)}`, status);
      return (text ? JSON.parse(text) : undefined) as T;
    };

    if (typeof GM_xmlhttpRequest === 'function') {
      return new Promise<T>((resolve, reject) => {
        GM_xmlhttpRequest({
          method,
          url,
          headers,
          data,
          timeout,
          onload: (r) => {
            try {
              resolve(parse(r.status, r.responseText));
            } catch (e) {
              reject(e);
            }
          },
          onerror: () => reject(new ApiError('Le serveur ne répond pas.', 0)),
          ontimeout: () => reject(new ApiError('Le serveur ne répond pas (délai dépassé).', 0)),
        });
      });
    }

    // Hors Tampermonkey (tests, ou page fournie par le serveur lui-même).
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeout);
    return fetch(url, { method, headers, body: data, signal: controller.signal })
      .then(async (r) => parse(r.status, await r.text()))
      .catch((e) => {
        throw e instanceof ApiError ? e : new ApiError('Le serveur ne répond pas.', 0);
      })
      .finally(() => clearTimeout(timer));
  }
}
