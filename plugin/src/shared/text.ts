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

// ------------------------------------------------------------------ références et identité de l'agence

const REF_TOKEN = String.raw`([A-Z0-9](?:[A-Z0-9_./-]{1,28}[A-Z0-9])?)`;
const REF_PATTERNS = [
  // "Réf. : ABC123", "Référence annonce : 1653L1653", "Ref du bien 28251", "Réf. de l'annonce : 28251"
  new RegExp(String.raw`\br[ée]f(?:[ée]rences?)?\b\.?\s*(?:de\s+l['’]\s*annonce|d['’]annonce|annonce|du\s+bien|bien|agence|interne|mandat|dossier)?\s*(?:n[°ºo]\.?)?\s*[:#]?\s*${REF_TOKEN}`, 'giu'),
  // "Mandat n° 1234", "mandat N°M-2024-12"
  new RegExp(String.raw`\bmandat\s*(?:n[°ºo]\.?|num[ée]ro)?\s*[:#]?\s*${REF_TOKEN}`, 'giu'),
];

/**
 * Références d'annonce écrites en clair ("Réf. : LA2100-REGOURD12", "Mandat n° 1234").
 * Garde uniquement les jetons de 3 caractères ou plus qui contiennent un chiffre
 * (exclut "réfrigérateur", "refait", "référence du DPE"...). Au plus 4 résultats, sans doublon.
 */
export function extractRefs(text: string | null | undefined): string[] {
  if (!text) return [];
  const plain = text.replace(/<[^>]+>/g, ' ').replace(/[\u00a0\u202f]/g, ' ');
  const out: string[] = [];
  for (const re of REF_PATTERNS) {
    for (const m of plain.matchAll(re)) {
      const token = m[1].toUpperCase();
      if (token.length < 3 || !/\d/.test(token) || /^\d{1,2}$/.test(token)) continue;
      // Années et montants isolés ("réf. 2024", "mandat 1 200 €") : trop ambigus.
      if (/^(19|20)\d{2}$/.test(token)) continue;
      if (!out.some((r) => normalizeRef(r) === normalizeRef(token))) out.push(token);
      if (out.length >= 4) return out;
    }
  }
  return out;
}

/** Contrôle de Luhn (SIREN / SIRET). */
function luhn(digits: string): boolean {
  let sum = 0;
  for (let i = 0; i < digits.length; i++) {
    let d = Number(digits[digits.length - 1 - i]);
    if (i % 2 === 1) {
      d *= 2;
      if (d > 9) d -= 9;
    }
    sum += d;
  }
  return sum % 10 === 0;
}

/**
 * SIREN de l'agence (9 chiffres) dans des mentions légales :
 * "<b>RCS:</b> 325539286", "RCS Rodez 407 797 521", "SIRET : 325 539 286 00105", "SIREN 407797521".
 * Renvoie undefined si aucun numéro valide (clé de Luhn) n'est trouvé.
 */
export function extractSiren(text: string | null | undefined): string | undefined {
  if (!text) return undefined;
  const plain = text.replace(/<[^>]+>/g, ' ').replace(/[\u00a0\u202f]/g, ' ');
  for (const m of plain.matchAll(/\bSIRET\b\D{0,15}((?:\d[\s.]?){13}\d)/gi)) {
    const d = m[1].replace(/\D/g, '');
    if (d.length === 14 && luhn(d.slice(0, 9))) return d.slice(0, 9);
  }
  for (const m of plain.matchAll(/\b(?:RCS|SIREN)\b[^0-9]{0,40}?((?:\d[\s.]?){8}\d)(?![\d])/gi)) {
    const d = m[1].replace(/\D/g, '');
    if (d.length === 9 && luhn(d)) return d;
  }
  return undefined;
}

/** Valeur SIREN brute ("407797521", "407 797 521") -> 9 chiffres validés, sinon undefined. */
export function normalizeSiren(value: string | null | undefined): string | undefined {
  const d = value?.replace(/\D/g, '') ?? '';
  return d.length === 9 && luhn(d) ? d : d.length === 14 && luhn(d.slice(0, 9)) ? d.slice(0, 9) : undefined;
}
