/** Utilitaires génériques pour le texte et les nombres. Aucune dépendance au DOM ni aux sites d'annonces. */

/** Convertit en minuscules. Retire les accents et la ponctuation. Réduit les espaces multiples à un espace. */
export function normalizeText(input: string): string {
  return input
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
    .replace(/<br\s*\/?>/g, ' ')
    .replace(/<[^>]+>/g, ' ')
    .replace(/[^a-z0-9]+/g, ' ')
    .trim()
    .replace(/\s+/g, ' ');
}

/** "1 400,50 €" -> 1400.5. "88,32 m²" -> 88.32. Renvoie undefined si le texte ne contient aucun nombre. */
export function parseNumber(input: string | null | undefined): number | undefined {
  if (!input) return undefined;
  const m = input.replace(/[\s\u00a0\u202f]/g, '').match(/\d+(?:[.,]\d+)?/);
  if (!m) return undefined;
  const n = Number(m[0].replace(',', '.'));
  return Number.isFinite(n) ? n : undefined;
}

/** Référence d'agence en majuscules, lettres et chiffres uniquement. Permet de comparer les sites d'annonces. */
export function normalizeRef(ref: string | undefined): string | undefined {
  if (!ref) return undefined;
  const r = ref.toUpperCase().replace(/[^A-Z0-9]/g, '');
  return r.length >= 3 ? r : undefined;
}

/** Ensemble des trigrammes de caractères d'un texte normalisé. */
export function trigrams(text: string): Set<string> {
  const s = ` ${normalizeText(text)} `;
  const out = new Set<string>();
  for (let i = 0; i + 3 <= s.length; i++) out.add(s.slice(i, i + 3));
  return out;
}

/** Similarité de Jaccard entre deux textes (0..1), calculée sur les trigrammes. */
export function textSimilarity(a: string | undefined, b: string | undefined): number | undefined {
  if (!a || !b) return undefined;
  const ta = trigrams(a);
  const tb = trigrams(b);
  if (ta.size < 20 || tb.size < 20) return undefined; // texte trop court pour une comparaison fiable
  let inter = 0;
  for (const t of ta) if (tb.has(t)) inter++;
  return inter / (ta.size + tb.size - inter);
}

/** Distance en mètres entre deux points GPS (haversine). */
export function distanceMeters(a: { lat: number; lon: number }, b: { lat: number; lon: number }): number {
  const R = 6371000;
  const toRad = (d: number) => (d * Math.PI) / 180;
  const dLat = toRad(b.lat - a.lat);
  const dLon = toRad(b.lon - a.lon);
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(toRad(a.lat)) * Math.cos(toRad(b.lat)) * Math.sin(dLon / 2) ** 2;
  return 2 * R * Math.asin(Math.sqrt(h));
}

/** Écart relatif |a-b| / max(a,b). */
export function relativeDiff(a: number, b: number): number {
  const m = Math.max(Math.abs(a), Math.abs(b));
  return m === 0 ? 0 : Math.abs(a - b) / m;
}
