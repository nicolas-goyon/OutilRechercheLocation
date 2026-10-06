import type { ListingData, SiteId } from '../core/types';

/** Une carte d'annonce dans la page. */
export interface CardRef {
  /** Identifiant de l'annonce sur le site d'annonces. Il ne change pas entre les visites. */
  siteId: string;
  /** Élément racine de la carte. Le plugin masque ou atténue cet élément. */
  element: HTMLElement;
}

/**
 * Tout le code spécifique à un site d'annonces.
 *
 * Pour ajouter un site, écrire un adaptateur (sites/<site>/) et l'enregistrer
 * dans sites/index.ts. Le reste du plugin (stockage, doublons, UI) est
 * générique.
 */
export interface SiteAdapter {
  id: SiteId;
  /** Nom affiché à l'utilisateur. */
  label: string;
  matches(location: Location): boolean;
  /** Cartes d'annonces présentes dans le DOM. */
  findCards(root: ParentNode): CardRef[];
  /** Données lisibles sur la carte. */
  parseCard(card: CardRef): ListingData;
  /** Élément à observer pour trouver les changements de page (SPA). Default: document.body. */
  observeRoot?(): Element | null;
  /**
   * Source de données supplémentaire (par exemple le JSON de l'API du site).
   * L'adaptateur appelle onData pour chaque annonce enrichie.
   */
  startEnrichment?(onData: (siteId: string, data: ListingData) => void): void;
  /** URL de miniature pour une photo stockée. Le plugin ne charge donc pas l'image en pleine taille. */
  thumbnailUrl?(photoUrl: string): string;
  /** Élément de la carte qui reçoit la barre d'actions. Default: la carte. */
  toolbarAnchor?(card: CardRef): HTMLElement;
}
