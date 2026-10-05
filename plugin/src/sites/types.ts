import type { ListingData, SiteId } from '../core/types';

/** Une carte d'annonce présente dans la page. */
export interface CardRef {
  /** Identifiant de l'annonce sur le site (stable entre les visites). */
  siteId: string;
  /** Élément racine de la carte : c'est lui qu'on masque/atténue. */
  element: HTMLElement;
}

/**
 * Tout ce qui est propre à un site. Ajouter un site = écrire un adaptateur
 * (sites/<site>/) et l'enregistrer dans sites/index.ts ; le reste du plugin
 * (stockage, doublons, UI) est générique.
 */
export interface SiteAdapter {
  id: SiteId;
  /** Nom affiché à l'utilisateur. */
  label: string;
  matches(location: Location): boolean;
  /** Cartes d'annonces actuellement dans le DOM. */
  findCards(root: ParentNode): CardRef[];
  /** Données lisibles sur la carte elle-même. */
  parseCard(card: CardRef): ListingData;
  /** Élément à observer pour détecter les changements de page (SPA). Default: document.body. */
  observeRoot?(): Element | null;
  /**
   * Source de données complémentaire (ex. JSON de l'API du site), appelée
   * avec un callback à invoquer pour chaque annonce enrichie.
   */
  startEnrichment?(onData: (siteId: string, data: ListingData) => void): void;
  /** URL de miniature pour une photo stockée (évite de charger l'image pleine taille). */
  thumbnailUrl?(photoUrl: string): string;
  /** Où placer la barre d'actions dans la carte. Default: la carte elle-même. */
  toolbarAnchor?(card: CardRef): HTMLElement;
}
