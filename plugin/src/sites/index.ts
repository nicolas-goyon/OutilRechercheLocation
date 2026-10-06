/**
 * Liste des sites d'annonces compatibles.
 *
 * Pour ajouter un site (LeBonCoin, PAP...) :
 *  1. Créer sites/<site>/adapter.ts. Ce fichier implémente SiteAdapter.
 *  2. Ajouter l'adaptateur dans ADAPTERS ci-dessous.
 *  3. Ajouter la ligne @match du site dans userscripts/recherche-logement.mjs.
 *     Utiliser le MÊME script : tous les sites partagent alors la même base.
 */
import { bieniciAdapter } from './bienici/adapter';
import { selogerAdapter } from './seloger/adapter';
import type { SiteAdapter } from './types';

export const ADAPTERS: SiteAdapter[] = [bieniciAdapter, selogerAdapter];

export function findAdapter(loc: Location): SiteAdapter | undefined {
  return ADAPTERS.find((a) => a.matches(loc));
}

export function siteLabel(id: string): string {
  return ADAPTERS.find((a) => a.id === id)?.label ?? id;
}

export function thumbnailFor(site: string, url: string): string {
  return ADAPTERS.find((a) => a.id === site)?.thumbnailUrl?.(url) ?? url;
}
