/**
 * Relais : exécute dans cet onglet les requêtes que le serveur ne peut pas faire
 * lui-même (recherches de « Mes projets » sur un site qui refuse les appels hors
 * navigateur).
 *
 * Le serveur envoie une requête à la fois, avec un délai entre deux requêtes. Le
 * plugin n'exécute que les adresses déclarées par l'adaptateur du site. La
 * réponse repart telle quelle : si le site demande une vérification, le serveur
 * arrête la recherche et l'utilisateur la passe lui-même dans l'onglet.
 */
import type { RelayRequest, RelayResult } from './types';

export interface RelayApi {
  hasToken(): boolean;
  relayNext(site: string): Promise<RelayRequest | undefined>;
  relayResult(id: string, result: RelayResult): Promise<void>;
}

export interface RelayOptions {
  site: string;
  allowedPaths: RegExp[];
  /** Origine de l'onglet : les autres origines sont refusées. */
  origin: string;
  fetch: (url: string, init: RequestInit) => Promise<Response>;
  /** Attente après une erreur ou sans token. */
  retryMs?: number;
  sleep?: (ms: number) => Promise<void>;
}

/** Adresse sur l'origine de l'onglet, et dans la liste de l'adaptateur. */
export function isAllowed(url: string, origin: string, allowedPaths: RegExp[]): boolean {
  try {
    const u = new URL(url);
    return u.origin === origin && allowedPaths.some((p) => p.test(u.pathname));
  } catch {
    return false;
  }
}

export class RelayWorker {
  private running = false;

  constructor(
    private readonly api: RelayApi,
    private readonly options: RelayOptions,
  ) {}

  start(): void {
    if (this.running) return;
    this.running = true;
    void this.loop();
  }

  stop(): void {
    this.running = false;
  }

  private async loop(): Promise<void> {
    const retry = this.options.retryMs ?? 30_000;
    const sleep = this.options.sleep ?? ((ms: number) => new Promise<void>((r) => setTimeout(r, ms)));
    while (this.running) {
      try {
        if (!this.api.hasToken() || !(await this.step())) await sleep(this.api.hasToken() ? 0 : retry);
      } catch {
        // Serveur arrêté : le plugin continue de fonctionner, et réessaiera plus tard.
        await sleep(retry);
      }
    }
  }

  /** Traite une requête, s'il y en a une. */
  async step(): Promise<boolean> {
    const request = await this.api.relayNext(this.options.site);
    if (!request) return false;
    await this.api.relayResult(request.id, await this.execute(request));
    return true;
  }

  private async execute(r: RelayRequest): Promise<RelayResult> {
    if (!isAllowed(r.url, this.options.origin, this.options.allowedPaths)) {
      return { status: 0, error: 'adresse non autorisée par le plugin' };
    }
    try {
      const res = await this.options.fetch(r.url, {
        method: r.method,
        credentials: 'include',
        headers: r.body === undefined || r.body === null ? { Accept: 'application/json' } : { Accept: 'application/json', 'Content-Type': 'application/json' },
        body: r.body ?? undefined,
      });
      return { status: res.status, body: await res.text() };
    } catch (e) {
      return { status: 0, error: e instanceof Error ? e.message : String(e) };
    }
  }
}
