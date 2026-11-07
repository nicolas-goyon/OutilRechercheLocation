/**
 * Informations détaillées des annonces, communes à tous les sites : équipements, classes énergie, mode de vente.
 * Mêmes valeurs que le serveur (RechercheLogement.Core/Model/ListingDetails.cs).
 */
import { normalizeText } from './text';

export type FeatureId =
  | 'garden'
  | 'pool'
  | 'terrace'
  | 'balcony'
  | 'garage'
  | 'parking'
  | 'cellar'
  | 'elevator'
  | 'fireplace'
  | 'airConditioning'
  | 'accessible';

/** Équipements : true présent, false annoncé absent. Un équipement non mentionné n'a pas de clé. */
export type ListingFeatures = Partial<Record<FeatureId, boolean>>;

/** Mode de vente particulier. Absent : vente classique. */
export type SaleKind = 'life-annuity' | 'auction' | 'tender';

/** "d", "Performance énergétique : D" -> "D". Autre chose ("NS", "VI", vide) : undefined. */
export function energyClass(value: string | null | undefined): string | undefined {
  let t = value?.trim().toUpperCase();
  if (!t) return undefined;
  if (t.length > 1 && t.includes(':')) t = t.slice(t.lastIndexOf(':') + 1).trim();
  return /^[A-G]$/.test(t) ? t : undefined;
}

/** Équipements connus ; les valeurs inconnues sont ignorées. Aucun : undefined. Deux fois le même : l'un des deux suffit. */
export function featuresOf(values: [FeatureId, boolean | undefined][]): ListingFeatures | undefined {
  const out: ListingFeatures = {};
  for (const [id, value] of values) {
    if (value === undefined) continue;
    out[id] = (out[id] ?? false) || value;
  }
  return Object.keys(out).length ? out : undefined;
}

/** Mots du texte -> équipement. "Garage" et "parking" sont distincts ; "box" est un garage. */
const FEATURE_WORDS: [RegExp, FeatureId][] = [
  [/\bjardin/, 'garden'],
  [/\bpiscine/, 'pool'],
  [/\bterrasse/, 'terrace'],
  [/\bbalcon/, 'balcony'],
  [/\b(garage|box)\b/, 'garage'],
  [/\b(parking|stationnement)/, 'parking'],
  [/\bcaves?\b/, 'cellar'],
  [/\bascenseur/, 'elevator'],
  [/\bcheminee/, 'fireplace'],
  [/\bclimatis/, 'airConditioning'],
  [/\b(pmr|handicap|mobilite reduite)/, 'accessible'],
];

/**
 * Équipements écrits en toutes lettres ("Jardin", "Pas de cave", "Garage, Parking") -> équipements.
 * "Pas de …" / "Sans …" : absent. Plusieurs éléments séparés par une virgule ou un point-virgule.
 */
export function featuresFromText(items: (string | undefined)[]): ListingFeatures | undefined {
  const values: [FeatureId, boolean][] = [];
  for (const item of items) {
    for (const part of (item ?? '').split(/[,;]/)) {
      const t = normalizeText(part);
      if (!t) continue;
      const present = !/^(pas d|sans |aucun|non )/.test(t);
      for (const [re, id] of FEATURE_WORDS) if (re.test(t)) values.push([id, present]);
    }
  }
  return featuresOf(values);
}

/** "1 Salle de bain; 1 Salle de douche" -> 2 ; "2 salles d'eau" -> 2. Aucun nombre : undefined. */
export function bathroomsFromText(text: string | undefined): number | undefined {
  if (!text) return undefined;
  const counts = [...normalizeText(text).matchAll(/(\d+) salles? (?:de bain|d eau|de douche)/g)].map((m) => Number(m[1]));
  return counts.length ? counts.reduce((a, b) => a + b, 0) : undefined;
}
