/**
 * Types du plugin + contrat d'API avec le serveur local (voir docs/api.md).
 *
 * Le serveur est la source de vérité : il stocke toutes les annonces,
 * regroupe les doublons en "biens" et porte les statuts. Le plugin lui
 * envoie ce qu'il voit (observations) et ce que fait l'utilisateur
 * (actions), et reçoit en retour l'état à afficher (ListingView).
 *
 * Toute modification ici doit être répercutée dans
 * server/src/RechercheLogement.Server/Api/Contracts.cs.
 */

export type SiteId = 'bienici' | 'seloger' | 'leboncoin' | 'pap' | 'logicimmo' | (string & {});

/** `${site}:${siteId}` */
export type ListingKey = string;

export type PropertyStatus =
  /** Jamais qualifiée. */
  | 'none'
  /** Vue, gardée visible mais atténuée. */
  | 'seen'
  /** Vue, pas intéressé : masquée. */
  | 'rejected'
  /** Me plaît : à contacter (suivi du contact sur le site local). */
  | 'toContact';

/** Étape de suivi d'un bien "à contacter" (modifiée surtout depuis le site local). */
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

/** Données descriptives extraites d'une annonce (chaque site en expose plus ou moins). */
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
  agencyRef?: string;
  agencyName?: string;
  photos?: string[];
  /** Empreintes (noms de fichier) des photos d'origine : signal de doublon inter-sites. */
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
  /** 'card' (HTML de la liste) ou 'api' (JSON du site, plus précis). */
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

/** Distribue Omit sur chaque membre de l'union (Omit<Action, ...> casserait le typage). */
export type NewAction = Action extends infer A ? (A extends Action ? Omit<A, 'id' | 'at'> : never) : never;

export interface SyncRequest {
  clientVersion: string;
  observations: Observation[];
  actions: Action[];
  /** Clés dont on veut l'état sans les observer (ex. rafraîchissement). */
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
  /** Doublons probables non encore validés. */
  suggestions: SuggestionView[];
  /** Lien vers la fiche du bien sur le site local. */
  webUrl?: string;
}

export interface SyncResponse {
  serverTime: number;
  appliedActionIds: string[];
  /** Actions refusées (ex. clé inconnue) : à retirer de la file sans réessayer. */
  rejectedActionIds: string[];
  listings: Record<ListingKey, ListingView>;
}
