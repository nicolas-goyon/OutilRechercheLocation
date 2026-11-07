/**
 * Types du plugin et contrat d'API avec le serveur local (voir docs/api.md).
 *
 * Le serveur est la source de vérité. Il stocke toutes les annonces. Il
 * regroupe les doublons en "biens". Il garde les statuts.
 *
 * Le plugin envoie au serveur les annonces affichées (observations) et les
 * actions de l'utilisateur (actions). Le serveur renvoie l'état à afficher
 * (ListingView).
 *
 * Toute modification de ce fichier exige la même modification dans
 * server/src/RechercheLogement.Core/Contracts/SyncContracts.cs.
 */

import type { ListingFeatures, SaleKind } from '../shared/details';

export type SiteId = 'bienici' | 'seloger' | 'leboncoin' | 'pap' | 'logicimmo' | (string & {});

/** `${site}:${siteId}` */
export type ListingKey = string;

export type PropertyStatus =
  /** Aucun statut. */
  | 'none'
  /** Vue : reste visible, mais atténuée. */
  | 'seen'
  /** Pas intéressé : masquée. */
  | 'rejected'
  /** Me plaît : à contacter (suivi du contact sur le serveur local). */
  | 'toContact';

/** Étape de suivi d'un bien "à contacter". L'utilisateur la modifie surtout sur le serveur local. */
export type ContactStage =
  | 'pending'
  | 'contacted'
  | 'visitScheduled'
  | 'visited'
  | 'applicationSent'
  | 'accepted'
  | 'declined';

export type TransactionType = 'rent' | 'buy';

export interface GeoPoint {
  lat: number;
  lon: number;
  precisionM?: number;
}

/** Données extraites d'une annonce. La quantité de données change selon le site d'annonces. */
export interface ListingData {
  url?: string;
  title?: string;
  transaction?: TransactionType;
  propertyType?: string;
  price?: number;
  charges?: number;
  surface?: number;
  rooms?: number;
  bedrooms?: number;
  floor?: number;
  furnished?: boolean;
  postalCode?: string;
  city?: string;
  district?: string;
  geo?: GeoPoint;
  /** Référence de l'annonce donnée par l'agence (champ dédié du site). Souvent identique d'un site à l'autre. */
  agencyRef?: string;
  /** Autres références trouvées (encadré de la page, "Réf. : …" ou "Mandat n° …" dans la description). */
  otherRefs?: string[];
  agencyName?: string;
  /** SIREN de l'agence (9 chiffres, RCS / SIRET des mentions légales). Identifie l'agence d'un site à l'autre. */
  agencySiren?: string;
  photos?: string[];
  /** Empreintes (noms de fichier) des photos d'origine. Elles aident à trouver un doublon entre sites. */
  photoKeys?: string[];
  /** Extrait normalisé de la description (≤ 600 caractères). */
  descriptionExcerpt?: string;
  publishedAt?: string;
  /** Surface du terrain, en m². */
  landSurface?: number;
  /** Classe du DPE, de "A" à "G". */
  energyClass?: string;
  /** Classe d'émission de gaz à effet de serre (GES), de "A" à "G". */
  ghgClass?: string;
  yearBuilt?: number;
  /** Salles de bain et salles d'eau. */
  bathrooms?: number;
  heating?: string;
  exposure?: string;
  /** Précision du type ("Chalet", "Villa", "Grange"…). */
  subtype?: string;
  /** Logement neuf. false : ancien. */
  newBuild?: boolean;
  /** Travaux à prévoir. false : bon état annoncé. */
  workNeeded?: boolean;
  saleKind?: SaleKind;
  /** Taxe foncière annuelle, en euros. */
  propertyTax?: number;
  features?: ListingFeatures;
}

export function listingKey(site: SiteId, siteId: string): ListingKey {
  return `${site}:${siteId}`;
}

// ------------------------------------------------------------------ contrat /api/sync

export interface Observation {
  site: SiteId;
  siteId: string;
  /** 'card' (HTML de la liste), 'api' (JSON du site, plus précis) ou 'detail' (réservé). */
  source: 'card' | 'api' | 'detail';
  data: ListingData;
  seenAt: number;
}

export type Action =
  | { id: string; at: number; type: 'setStatus'; key: ListingKey; status: PropertyStatus }
  | { id: string; at: number; type: 'setNote'; key: ListingKey; note: string }
  | { id: string; at: number; type: 'confirmDuplicate'; key: ListingKey; otherKey: ListingKey }
  | { id: string; at: number; type: 'dismissDuplicate'; key: ListingKey; otherKey: ListingKey }
  | { id: string; at: number; type: 'detach'; key: ListingKey };

/** Applique Omit à chaque membre de l'union. Omit<Action, ...> casse le typage de l'union. */
export type NewAction = Action extends infer A ? (A extends Action ? Omit<A, 'id' | 'at'> : never) : never;

export interface SyncRequest {
  clientVersion: string;
  observations: Observation[];
  actions: Action[];
  /** Clés dont le plugin demande l'état sans observation (par exemple pour un rafraîchissement). */
  want: ListingKey[];
}

export interface ListingRef {
  key: ListingKey;
  site: SiteId;
  url?: string;
  title?: string;
  price?: number;
}

export interface SuggestionView {
  other: ListingRef;
  otherStatus: PropertyStatus;
  score: number;
  reasons: string[];
}

/** État d'une annonce tel que le plugin l'affiche. */
export interface ListingView {
  key: ListingKey;
  propertyId: string;
  status: PropertyStatus;
  contactStage?: ContactStage;
  note?: string;
  /** Autres annonces confirmées comme le même bien. */
  siblings: ListingRef[];
  /** Doublons probables pas encore confirmés. */
  suggestions: SuggestionView[];
  /** Lien vers la fiche du bien sur le serveur local. */
  webUrl?: string;
}

export interface SyncResponse {
  serverTime: number;
  appliedActionIds: string[];
  /** Actions refusées (par exemple clé inconnue). Le plugin les retire de la file et ne les renvoie pas. */
  rejectedActionIds: string[];
  listings: Record<ListingKey, ListingView>;
}

// ------------------------------------------------------------------ contrat /api/searches (recherches favorites)

/** Copie de server/.../Contracts/SearchContracts.cs. */
export interface SavedSearchRequest {
  name?: string;
  url: string;
  note?: string;
}

export interface SavedSearchView {
  id: string;
  name: string;
  url: string;
  site: string;
  note?: string;
  createdAt: number;
  lastOpenedAt?: number;
}

/** Requête que le serveur demande d'exécuter dans un onglet du site (GET /api/relay/next). */
export interface RelayRequest {
  id: string;
  method: string;
  url: string;
  body?: string;
  /** Type de réponse attendu : JSON par défaut, "text/html" pour une page (Leboncoin, PAP). */
  accept?: string;
}

/** Réponse du site, renvoyée telle quelle au serveur. status 0 : le navigateur n'a pas pu appeler le site. */
export interface RelayResult {
  status: number;
  body?: string;
  error?: string;
}

/** Photos gardées par annonce. Même limite côté serveur (ListingData.MaxPhotos). */
export const MAX_PHOTOS = 40;

/** GET /api/relay/status : recherches du serveur en cours sur un site. */
export interface RelayStatus {
  running: { projectId: string; project: string; step: string }[];
  /** Projets actifs qui utilisent ce site. */
  projects: number;
}
