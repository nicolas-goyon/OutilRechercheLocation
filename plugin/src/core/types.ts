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
