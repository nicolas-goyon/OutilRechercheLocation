/**
 * Client HTTP vers le serveur local.
 *
 * Les pages des sites sont en https et le serveur local en http://localhost :
 * un fetch() depuis la page serait bloqué (contenu mixte, CORS, Private
 * Network Access). GM_xmlhttpRequest passe par l'extension et n'a pas ces
 * restrictions — d'où `@grant GM_xmlhttpRequest` et `@connect localhost`
 * dans le script Tampermonkey.
 *
 * Chaque requête porte `Authorization: Bearer <token>` : le serveur refuse
 * tout appel /api/* sans le bon token.
 */
import type { SyncRequest, SyncResponse } from './types';

export class ApiError extends Error {
  constructor(
    message: string,
    /** 0 = serveur injoignable / timeout. */
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
  readonly baseUrl: string;

  constructor(private readonly options: ApiClientOptions) {
    this.baseUrl = options.baseUrl.replace(/\/+$/, '');
  }

  sync(request: SyncRequest): Promise<SyncResponse> {
    return this.request<SyncResponse>('POST', '/api/sync', request);
  }

  ping(): Promise<ServerInfo> {
    return this.request<ServerInfo>('GET', '/api/ping');
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
      if (status === 401) throw new ApiError('Token refusé par le serveur', 401);
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
          onerror: () => reject(new ApiError('Serveur injoignable', 0)),
          ontimeout: () => reject(new ApiError('Délai dépassé', 0)),
        });
      });
    }

    // Hors Tampermonkey (tests, page servie par le serveur lui-même).
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeout);
    return fetch(url, { method, headers, body: data, signal: controller.signal })
      .then(async (r) => parse(r.status, await r.text()))
      .catch((e) => {
        throw e instanceof ApiError ? e : new ApiError('Serveur injoignable', 0);
      })
      .finally(() => clearTimeout(timer));
  }
}
