/**
 * Registre des sites supportés. Pour ajouter SeLoger, LeBonCoin, PAP... :
 * créer sites/<site>/adapter.ts implémentant SiteAdapter, l'ajouter ici,
 * et ajouter le @match correspondant dans le script Tampermonkey (le MÊME
 * script, pour partager la base).
 */
import { bieniciAdapter } from './bienici/adapter';
import type { SiteAdapter } from './types';

export const ADAPTERS: SiteAdapter[] = [bieniciAdapter];

export function findAdapter(loc: Location): SiteAdapter | undefined {
  return ADAPTERS.find((a) => a.matches(loc));
}

export function siteLabel(id: string): string {
  return ADAPTERS.find((a) => a.id === id)?.label ?? id;
}

export function thumbnailFor(site: string, url: string): string {
  return ADAPTERS.find((a) => a.id === site)?.thumbnailUrl?.(url) ?? url;
}
