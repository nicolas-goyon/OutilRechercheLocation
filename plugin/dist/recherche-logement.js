"use strict";
var TMRechercheLogement = (() => {
  var __defProp = Object.defineProperty;
  var __getOwnPropDesc = Object.getOwnPropertyDescriptor;
  var __getOwnPropNames = Object.getOwnPropertyNames;
  var __hasOwnProp = Object.prototype.hasOwnProperty;
  var __export = (target, all) => {
    for (var name in all)
      __defProp(target, name, { get: all[name], enumerable: true });
  };
  var __copyProps = (to, from, except, desc) => {
    if (from && typeof from === "object" || typeof from === "function") {
      for (let key of __getOwnPropNames(from))
        if (!__hasOwnProp.call(to, key) && key !== except)
          __defProp(to, key, { get: () => from[key], enumerable: !(desc = __getOwnPropDesc(from, key)) || desc.enumerable });
    }
    return to;
  };
  var __toCommonJS = (mod) => __copyProps(__defProp({}, "__esModule", { value: true }), mod);

  // src/index.ts
  var src_exports = {};
  __export(src_exports, {
    VERSION: () => VERSION,
    init: () => init,
    instance: () => instance,
    searchName: () => searchName
  });

  // src/core/types.ts
  function listingKey(site, siteId) {
    return `${site}:${siteId}`;
  }

  // src/shared/dom/root.ts
  function getRootWindow() {
    return typeof unsafeWindow !== "undefined" && unsafeWindow ? unsafeWindow : window;
  }

  // src/shared/text.ts
  function normalizeText(input) {
    return input.normalize("NFD").replace(/[\u0300-\u036f]/g, "").toLowerCase().replace(/<br\s*\/?>/g, " ").replace(/<[^>]+>/g, " ").replace(/[^a-z0-9]+/g, " ").trim().replace(/\s+/g, " ");
  }
  function parseNumber(input) {
    if (!input) return void 0;
    const m = input.replace(/[\s\u00a0\u202f]/g, "").match(/\d+(?:[.,]\d+)?/);
    if (!m) return void 0;
    const n = Number(m[0].replace(",", "."));
    return Number.isFinite(n) ? n : void 0;
  }
  function normalizeRef(ref) {
    if (!ref) return void 0;
    const r = ref.toUpperCase().replace(/[^A-Z0-9]/g, "");
    return r.length >= 3 ? r : void 0;
  }
  var REF_TOKEN = String.raw`([A-Z0-9](?:[A-Z0-9_./-]{1,28}[A-Z0-9])?)`;
  var REF_PATTERNS = [
    // "Réf. : ABC123", "Référence annonce : 1653L1653", "Ref du bien 28251", "Réf. de l'annonce : 28251"
    new RegExp(String.raw`\br[ée]f(?:[ée]rences?)?\b\.?\s*(?:de\s+l['’]\s*annonce|d['’]annonce|annonce|du\s+bien|bien|agence|interne|mandat|dossier)?\s*(?:n[°ºo]\.?)?\s*[:#]?\s*${REF_TOKEN}`, "giu"),
    // "Mandat n° 1234", "mandat N°M-2024-12"
    new RegExp(String.raw`\bmandat\s*(?:n[°ºo]\.?|num[ée]ro)?\s*[:#]?\s*${REF_TOKEN}`, "giu")
  ];
  function extractRefs(text) {
    if (!text) return [];
    const plain = text.replace(/<[^>]+>/g, " ").replace(/[\u00a0\u202f]/g, " ");
    const out = [];
    for (const re of REF_PATTERNS) {
      for (const m of plain.matchAll(re)) {
        const token = m[1].toUpperCase();
        if (token.length < 3 || !/\d/.test(token) || /^\d{1,2}$/.test(token)) continue;
        if (/^(19|20)\d{2}$/.test(token)) continue;
        if (!out.some((r) => normalizeRef(r) === normalizeRef(token))) out.push(token);
        if (out.length >= 4) return out;
      }
    }
    return out;
  }
  function luhn(digits) {
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
  function extractSiren(text) {
    if (!text) return void 0;
    const plain = text.replace(/<[^>]+>/g, " ").replace(/[\u00a0\u202f]/g, " ");
    for (const m of plain.matchAll(/\bSIRET\b\D{0,15}((?:\d[\s.]?){13}\d)/gi)) {
      const d = m[1].replace(/\D/g, "");
      if (d.length === 14 && luhn(d.slice(0, 9))) return d.slice(0, 9);
    }
    for (const m of plain.matchAll(/\b(?:RCS|SIREN)\b[^0-9]{0,40}?((?:\d[\s.]?){8}\d)(?![\d])/gi)) {
      const d = m[1].replace(/\D/g, "");
      if (d.length === 9 && luhn(d)) return d;
    }
    return void 0;
  }
  function normalizeSiren(value) {
    const d = value?.replace(/\D/g, "") ?? "";
    return d.length === 9 && luhn(d) ? d : d.length === 14 && luhn(d.slice(0, 9)) ? d.slice(0, 9) : void 0;
  }

  // src/sites/bienici/parse.ts
  var CARD_SELECTOR = "article.ad-overview[data-id]";
  var DETAIL_SELECTOR = 'section.section-detailedSheet[id^="section-detailedSheet_"]';
  var AD_SELECTORS = [
    ".search-results-list__commercial-ad",
    ".advertisement-container",
    ".safeFrameContentFullWidth"
  ];
  var PROPERTY_TYPES = [
    [/^appartement/, "flat"],
    [/^studio/, "flat"],
    [/^maison/, "house"],
    [/^villa/, "house"],
    [/^loft/, "loft"],
    [/^chateau/, "castle"],
    [/^hotel particulier/, "townhouse"],
    [/^terrain/, "terrain"],
    [/^parking|^box/, "parking"]
  ];
  function parseBieniciCard(card) {
    const text = (sel) => card.querySelector(sel)?.textContent?.replace(/\s+/g, " ").trim() || void 0;
    const title = text(".real-estate-main-info__title");
    const address = text(".real-estate-main-info__address");
    const priceText = text(".ad-price__the-price");
    const perMonth = text(".ad-price__per-month");
    const href = card.querySelector("a.detailedSheetLink")?.getAttribute("href") ?? void 0;
    const photo = card.querySelector("img.img__image, .ad-overview-photo img")?.getAttribute("src");
    const description = card.querySelector(".ad-overview-description")?.innerHTML;
    const data = {
      ...parseTitle(title),
      ...parseAddress(address),
      title,
      price: parseNumber(priceText),
      url: href ? canonicalUrl(href) : void 0,
      transaction: href?.startsWith("/annonce/location") || perMonth ? "rent" : href?.startsWith("/annonce/vente") ? "buy" : void 0,
      photos: photo ? [stripPhotoParams(photo)] : void 0,
      descriptionExcerpt: description ? normalizeText(description).slice(0, 600) : void 0,
      otherRefs: nonEmpty(extractRefs(description))
    };
    return data;
  }
  function detailSiteId(section2) {
    return section2.id.replace(/^section-detailedSheet_/, "") || void 0;
  }
  function parseBieniciDetail(section2) {
    const text = (sel) => section2.querySelector(sel)?.textContent?.replace(/\s+/g, " ").trim() || void 0;
    const h1 = section2.querySelector("h1");
    const address = text("h1 .fullAddress");
    let heading;
    if (h1) {
      const clone = h1.cloneNode(true);
      clone.querySelector(".fullAddress")?.remove();
      heading = clone.textContent?.replace(/\s+/g, " ").trim() || void 0;
    }
    const transactionWord = heading?.match(/^(location|vente|achat)\s+/i)?.[1]?.toLowerCase();
    const title = heading?.replace(/^(location|vente|achat)\s+/i, "").replace(/^./, (c) => c.toUpperCase());
    const perMonth = text(".detailedSheetPrice .ad-price__per-month");
    const ref = text(".ad-detailed-sheet-main-info")?.match(/R[ée]f\.? de l.annonce\s*:\s*(\S+)/i)?.[1];
    const description = (section2.querySelector(".see-more-description__content") ?? section2.querySelector("section.description p"))?.textContent;
    const photos = [...section2.querySelectorAll('.slideshow img[u="image"], .slideshow img[src2]')].map((img) => img.getAttribute("src2") ?? img.getAttribute("src") ?? "").filter((u) => u.startsWith("https://file.bienici.com/")).map(stripPhotoParams);
    const id = detailSiteId(section2);
    return {
      ...parseTitle(title),
      ...parseAddress(address),
      title,
      price: parseNumber(text(".detailedSheetPrice .ad-price__the-price")),
      transaction: transactionWord === "location" || perMonth ? "rent" : transactionWord ? "buy" : void 0,
      agencyRef: ref,
      otherRefs: nonEmpty(extractRefs(description).filter((r) => r !== ref?.toUpperCase())),
      photos: photos.length ? [...new Set(photos)].slice(0, 4) : void 0,
      descriptionExcerpt: description ? normalizeText(description).slice(0, 600) : void 0,
      url: id ? detailUrl(id, section2) : void 0
    };
  }
  function detailUrl(id, section2) {
    const loc = section2.ownerDocument?.defaultView?.location;
    if (loc && loc.pathname.endsWith(`/${id}`)) return canonicalUrl(loc.pathname);
    const canonical = section2.ownerDocument?.querySelector('link[rel="canonical"]')?.getAttribute("href");
    return canonical && canonical.endsWith(`/${id}`) ? canonicalUrl(canonical) : void 0;
  }
  function parseTitle(title) {
    if (!title) return {};
    const t = normalizeText(title);
    const out = {};
    for (const [re, type] of PROPERTY_TYPES) {
      if (re.test(t)) {
        out.propertyType = type;
        break;
      }
    }
    if (/\bmeuble\b/.test(t)) out.furnished = true;
    const rooms = t.match(/(\d+)\s*pieces?\b/);
    if (rooms) out.rooms = Number(rooms[1]);
    else if (/^studio/.test(t)) out.rooms = 1;
    const surface = title.replace(/[\u00a0\u202f]/g, " ").match(/(\d+(?:[.,]\d+)?)\s*m\u00b2/);
    if (surface) out.surface = Number(surface[1].replace(",", "."));
    return out;
  }
  function parseAddress(address) {
    if (!address) return {};
    const a = address.replace(/[\u00a0\u202f]/g, " ").trim();
    const m = a.match(/^(\d{5})\s+(.+?)(?:\s*\((.+)\))?$/);
    if (!m) return { city: a };
    return { postalCode: m[1], city: m[2].trim(), district: m[3]?.trim() };
  }
  function photoKey(url) {
    if (!url) return void 0;
    const name = url.split("?")[0].split("/").pop()?.toLowerCase().replace(/\.(jpe?g|png|webp|gif)$/, "");
    const significant = name?.replace(/image|photo|img|pic|[_\-.\s]/g, "") ?? "";
    return name && significant.length >= 8 ? name : void 0;
  }
  function canonicalUrl(href) {
    const u = new URL(href, "https://www.bienici.com");
    return u.origin + u.pathname;
  }
  function stripPhotoParams(url) {
    return url.split("?")[0];
  }
  var first = (v) => Array.isArray(v) ? v[0] : v;
  function parseBieniciApiAd(ad) {
    const pos = ad.blurInfo?.position;
    return {
      transaction: ad.adType === "rent" ? "rent" : ad.adType === "buy" ? "buy" : void 0,
      propertyType: ad.propertyType,
      agencyRef: ad.reference || void 0,
      otherRefs: nonEmpty(extractRefs(ad.description).filter((r) => r !== ad.reference?.toUpperCase())),
      agencyName: ad.contactRelativeData?.agencyNameToDisplay || ad.accountDisplayName || void 0,
      agencySiren: normalizeSiren(ad.contactRelativeData?.rcs),
      price: first(ad.price),
      charges: ad.charges,
      surface: first(ad.surfaceArea),
      rooms: first(ad.roomsQuantity),
      bedrooms: ad.bedroomsQuantity,
      floor: ad.floor ?? void 0,
      furnished: ad.isFurnished,
      postalCode: ad.postalCode,
      city: ad.city,
      district: ad.district?.libelle || ad.district?.name || void 0,
      geo: pos ? { lat: pos.lat, lon: pos.lon, precisionM: ad.blurInfo?.radius } : void 0,
      photos: ad.photos?.map((p) => p.url || p.url_photo).filter((u) => !!u).slice(0, 4),
      photoKeys: ad.photos?.map((p) => photoKey(p.url_photo)).filter((u) => !!u).slice(0, 6),
      descriptionExcerpt: ad.description ? normalizeText(ad.description).slice(0, 600) : void 0,
      publishedAt: ad.publicationDate
    };
  }
  function nonEmpty(list) {
    return list.length ? list : void 0;
  }

  // src/sites/bienici/adapter.ts
  var API_RE = /\/realEstateAds?(-one)?\.json\?/;
  var bieniciAdapter = {
    id: "bienici",
    label: "Bien'ici",
    matches: (loc) => /(^|\.)bienici\.com$/.test(loc.hostname),
    findCards(root2) {
      return [...root2.querySelectorAll(CARD_SELECTOR)].map((element) => ({ element, siteId: element.dataset.id ?? "" })).filter((c) => c.siteId);
    },
    parseCard: (card) => parseBieniciCard(card.element),
    observeRoot: () => document.querySelector("#app, main") ?? document.body,
    toolbarAnchor: (card) => card.element,
    hideSelectors: AD_SELECTORS,
    isSearchPage: (loc) => loc.pathname.startsWith("/recherche/"),
    findDetail(root2) {
      const section2 = [...root2.querySelectorAll(DETAIL_SELECTOR)].pop();
      const siteId = section2 && detailSiteId(section2);
      if (!section2 || !siteId) return void 0;
      const anchor = section2.querySelector(".detailedSheetFirstBlock") ?? section2;
      return { siteId, element: section2, anchor };
    },
    parseDetail: (detail) => parseBieniciDetail(detail.element),
    thumbnailUrl: (url) => url.startsWith("https://file.bienici.com/") ? `${url}?width=400&height=240&fit=cover` : url,
    startEnrichment(onData) {
      const win = getRootWindow();
      const done = /* @__PURE__ */ new Set();
      const handle = async (url) => {
        if (!API_RE.test(url) || done.has(url)) return;
        done.add(url);
        try {
          const res = await win.fetch(url, { credentials: "include" });
          if (!res.ok) return;
          const json = await res.json();
          const single = typeof json.id === "string" ? [json] : [];
          for (const ad of [...json.realEstateAds ?? [], ...json.leadingAds ?? [], ...single]) {
            if (ad?.id) onData(ad.id, parseBieniciApiAd(ad));
          }
        } catch (error) {
          console.warn("[RechercheLogement] Échec de l'enrichissement Bien'ici", error);
        }
      };
      for (const e of win.performance.getEntriesByType("resource")) void handle(e.name);
      if (typeof win.PerformanceObserver === "function") {
        new win.PerformanceObserver((list) => {
          for (const e of list.getEntries()) void handle(e.name);
        }).observe({ type: "resource", buffered: false });
      }
    }
  };

  // src/sites/seloger/parse.ts
  var CARD_SELECTOR2 = '[data-testid="serp-core-classified-card-testid"]';
  var DETAIL_SELECTOR2 = '[data-testid="aviv.CDP.main"]';
  var AD_SELECTORS2 = [
    '[data-testid="serp-inline-ad"]',
    '[data-testid="serp-banner-ad"]',
    '[data-testid="ad-sense-test"]',
    '[id^="afscontainer"]'
  ];
  function cardSiteId(card) {
    const fromId = card.id.match(/^classified-card-(.+)$/)?.[1];
    if (fromId) return fromId;
    const href = card.querySelector('a[data-testid="card-mfe-covering-link-testid"]')?.getAttribute("href");
    return href ? siteIdFromUrl(href) : void 0;
  }
  function siteIdFromUrl(href) {
    const path = new URL(href, "https://www.seloger.com").pathname;
    if (!path.startsWith("/annonce/")) return void 0;
    return path.split("/").filter(Boolean).pop()?.replace(/\.htm$/, "") || void 0;
  }
  function canonicalUrl2(href) {
    const u = new URL(href, "https://www.seloger.com");
    return u.origin + u.pathname;
  }
  function stripPhotoSize(url) {
    const u = new URL(url);
    for (const k of ["w", "h", "width", "height", "func"]) u.searchParams.delete(k);
    return u.toString();
  }
  function transactionFromUrl(href) {
    if (!href) return void 0;
    const path = new URL(href, "https://www.seloger.com").pathname;
    if (/^\/annonce\/location\//.test(path)) return "rent";
    if (/^\/annonce\/(achat|vente)\//.test(path)) return "buy";
    return void 0;
  }
  var TYPE_WORDS = [
    [/\b(maison|villa|pavillon|propriete|ferme|mas)\b/, "house"],
    [/\b(appartement|studio|duplex|triplex|chambre)\b/, "flat"],
    [/\bloft\b/, "loft"],
    [/\b(chateau|manoir)\b/, "castle"],
    [/\bhotel particulier\b/, "townhouse"],
    [/\bterrain\b/, "terrain"],
    [/\b(parking|box|garage)\b/, "parking"]
  ];
  function propertyTypeFromText(text) {
    if (!text) return void 0;
    const t = normalizeText(text);
    for (const [re, type] of TYPE_WORDS) if (re.test(t)) return type;
    return void 0;
  }
  var RAW_TYPES = {
    APARTMENT: "flat",
    FLAT: "flat",
    STUDIO: "flat",
    HOUSE: "house",
    VILLA: "house",
    LOFT: "loft",
    CASTLE: "castle",
    TOWNHOUSE: "townhouse",
    LAND: "terrain",
    PLOT: "terrain",
    PARKING: "parking",
    GARAGE: "parking"
  };
  function parseFloor(text) {
    if (!text) return void 0;
    const t = normalizeText(text);
    if (/rez de chaussee|\brdc\b/.test(t)) return 0;
    const m = t.match(/(\d+)\s*(?:e|er|eme|ere)?\s*etage/) ?? t.match(/etage\s*(\d+)/);
    return m ? Number(m[1]) : void 0;
  }
  function parseKeyfacts(facts) {
    const out = {};
    for (const raw of facts) {
      const f = raw.replace(/[  ]/g, " ").trim();
      const t = normalizeText(f);
      if (/^\d+\s*pieces?$/.test(t) || /^studio$/.test(t)) out.rooms = t === "studio" ? 1 : parseNumber(f);
      else if (/^\d+\s*chambres?$/.test(t)) out.bedrooms = parseNumber(f);
      else if (/m²|m2$/.test(f)) out.surface = parseNumber(f);
      else if (/etage|rez de chaussee/.test(t)) out.floor = parseFloor(f);
      else if (/^meuble$/.test(t)) out.furnished = true;
      else if (/^non meuble$/.test(t)) out.furnished = false;
    }
    return out;
  }
  function parseAddress2(address) {
    if (!address) return {};
    const a = address.replace(/[  ]/g, " ").replace(/\s+/g, " ").trim();
    const m = a.match(/^(?:(.+),\s*)?([^,]+?)\s*\((\d{5})\)$/);
    if (!m) return { city: a };
    return { postalCode: m[3], city: m[2].trim(), district: m[1]?.trim() || void 0 };
  }
  function parseSelogerCard(card) {
    const text = (sel) => card.querySelector(sel)?.textContent?.replace(/\s+/g, " ").trim() || void 0;
    const link2 = card.querySelector('a[data-testid="card-mfe-covering-link-testid"]');
    const href = link2?.getAttribute("href") ?? void 0;
    const keyfactsEl = card.querySelector('[data-testid="cardmfe-keyfacts-testid"]');
    const facts = keyfactsEl ? [...keyfactsEl.children].map((c) => c.textContent?.trim() ?? "").filter((f) => f && f !== "·") : [];
    const heading = keyfactsEl?.previousElementSibling?.textContent?.trim() || link2?.getAttribute("title")?.split(" - ")[0]?.trim();
    const price = text('[data-testid="cardmfe-price-testid"]');
    const photo = card.querySelector('[data-testid="card-mfe-picture-box-gallery-test-id"] img')?.getAttribute("src");
    const descBox = card.querySelector('[data-testid="cardmfe-description-text-test-id"]');
    const description = descBox?.lastElementChild?.textContent ?? void 0;
    const city = parseAddress2(text('[data-testid="cardmfe-description-box-address"]'));
    const kf = parseKeyfacts(facts);
    return {
      ...kf,
      ...city,
      title: buildTitle(heading, kf),
      propertyType: propertyTypeFromText(heading),
      price: parseNumber(price),
      url: href ? canonicalUrl2(href) : void 0,
      transaction: transactionFromUrl(href) ?? (/\blouer\b/i.test(heading ?? "") ? "rent" : /\bvendre\b/i.test(heading ?? "") ? "buy" : void 0),
      furnished: kf.furnished ?? (/\bmeubl[ée]/i.test(heading ?? "") ? true : void 0),
      photos: photo?.startsWith("http") ? [stripPhotoSize(photo)] : void 0,
      descriptionExcerpt: description ? normalizeText(description).slice(0, 600) : void 0
    };
  }
  function buildTitle(heading, kf) {
    if (!heading) return void 0;
    const base = heading.replace(/\s+à\s+(louer|vendre)\b.*$/i, "").trim();
    const parts = [base];
    if (kf.rooms) parts.push(`${kf.rooms} pièce${kf.rooms > 1 ? "s" : ""}`);
    if (kf.surface) parts.push(`${String(kf.surface).replace(".", ",")} m²`);
    return parts.join(" ");
  }
  function parseSelogerSerpClassified(c) {
    const raw = c.rawData ?? {};
    const kf = parseKeyfacts(c.hardFacts?.keyfacts ?? []);
    const addr = c.location?.address;
    const photos = (c.gallery?.images ?? []).map((i) => i.url).filter((u) => !!u).slice(0, 4);
    return {
      ...kf,
      url: c.url ? canonicalUrl2(c.url) : void 0,
      title: buildTitle(c.hardFacts?.title, { rooms: raw.nbroom ?? kf.rooms, surface: raw.surface?.main ?? kf.surface }),
      transaction: distribution(raw.distributionType) ?? transactionFromUrl(c.url),
      propertyType: raw.propertyType && RAW_TYPES[raw.propertyType] || propertyTypeFromText(c.hardFacts?.title),
      price: raw.price ?? void 0,
      surface: raw.surface?.main ?? kf.surface,
      rooms: raw.nbroom ?? kf.rooms,
      bedrooms: raw.nbbedroom ?? kf.bedrooms,
      postalCode: addr?.zipCode,
      city: addr?.city,
      district: addr?.district,
      agencyRef: raw.offererMarketingKey || void 0,
      otherRefs: otherRefs(c.mainDescription?.description, raw.offererMarketingKey),
      agencyName: c.provider?.intermediaryCard?.title?.replace(/\s+/g, " ").trim() || void 0,
      agencySiren: extractSiren((c.provider?.agencyLegalInformations ?? []).join("\n")),
      photos: photos.length ? photos : void 0,
      descriptionExcerpt: c.mainDescription?.description ? normalizeText(c.mainDescription.description).slice(0, 600) : void 0,
      publishedAt: c.metadata?.creationDate
    };
  }
  function distribution(d) {
    if (!d) return void 0;
    if (/^RENT/i.test(d)) return "rent";
    if (/^(BUY|SALE|SELL)/i.test(d)) return "buy";
    return void 0;
  }
  var DETAIL_GEO_PRECISION_M = 150;
  function parseSelogerDetailClassified(c, pageUrl) {
    const s = c.sections ?? {};
    const kf = parseKeyfacts(s.hardFacts?.keyfacts ?? []);
    const addr = s.location?.address;
    const coords = s.location?.geometry?.type === "Point" ? s.location.geometry.coordinates : void 0;
    const ref = s.key?.keys?.find((k) => /r[ée]f[ée]rence/i.test(k.label ?? ""))?.value;
    const prices = (s.price?.components ?? []).flatMap(
      (comp) => (comp.units ?? []).flatMap((u) => [u.main?.price, ...(u.details ?? []).flatMap((d) => d.prices ?? [])])
    ).filter((p) => !!p);
    const labelOf = (p) => (typeof p.label === "string" ? p.label : p.label?.main) ?? "";
    const amount = (p) => parseNumber(p?.value?.main?.ariaLabel ?? p?.value?.main?.value);
    const rent = prices.find((p) => /^loyer/i.test(labelOf(p)));
    const charges = prices.find((p) => /charges/i.test(labelOf(p)) && !/^loyer/i.test(labelOf(p)));
    const furnishedValue = (s.features?.details?.categories ?? []).flatMap((cat) => cat.elements ?? []).find((e) => e.icon === "furnished")?.value;
    const photos = (c.domains?.medias?.images ?? []).map((i) => i.url).filter((u) => !!u).slice(0, 4);
    return {
      ...kf,
      url: pageUrl ? canonicalUrl2(pageUrl) : void 0,
      title: buildTitle(s.hardFacts?.title, kf),
      transaction: distribution(c.rawData?.distributionType) ?? transactionFromUrl(pageUrl),
      propertyType: c.rawData?.propertyType && RAW_TYPES[c.rawData.propertyType] || propertyTypeFromText(s.hardFacts?.title),
      price: amount(rent) ?? parseNumber(s.hardFacts?.price?.ariaLabel) ?? c.tracking?.av_items?.[0]?.price,
      charges: amount(charges),
      furnished: furnishedValue ? !/^non\b/i.test(normalizeText(furnishedValue)) : kf.furnished,
      postalCode: addr?.zipCode,
      city: addr?.city,
      district: addr?.district,
      geo: coords ? { lat: coords[1], lon: coords[0], precisionM: DETAIL_GEO_PRECISION_M } : void 0,
      agencyRef: ref || void 0,
      otherRefs: otherRefs(s.description?.description, ref),
      agencyName: c.contactSections?.contactCard?.title?.replace(/\s+/g, " ").trim() || void 0,
      agencySiren: extractSiren((c.contactSections?.provider?.agencyLegalInformations ?? []).join("\n")),
      photos: photos.length ? photos : void 0,
      descriptionExcerpt: s.description?.description ? normalizeText(s.description.description).slice(0, 600) : void 0,
      publishedAt: c.metadata?.creationDate
    };
  }
  function parseSelogerDetailDom(main, pageUrl) {
    const doc = main.ownerDocument ?? main;
    const text = (sel) => doc.querySelector(sel)?.textContent?.replace(/\s+/g, " ").trim() || void 0;
    const heading = text('[data-testid="cdp-hardfacts-title"]');
    const facts = (text('[data-testid="cdp-hardfacts-keyfacts"]') ?? "").split(/\s*[•·]\s*/).filter(Boolean);
    const kf = parseKeyfacts(facts);
    const keys = text('[data-testid="cdp-classified-keys"]');
    const ref = keys?.match(/R[ée]f[ée]rence annonce\s*:\s*(\S+)/i)?.[1];
    const description = text('[data-testid="cdp-main-description-expandable-text"]');
    const srPrice = doc.querySelector('[data-testid="cdp-seo-wrapper"] span[style*="clip"]')?.textContent;
    return {
      ...kf,
      ...parseAddress2(text('[data-testid="cdp-location-address"]')),
      url: pageUrl ? canonicalUrl2(pageUrl) : void 0,
      title: buildTitle(heading, kf),
      transaction: transactionFromUrl(pageUrl),
      propertyType: propertyTypeFromText(heading),
      price: parseNumber(srPrice),
      agencyRef: ref,
      otherRefs: otherRefs(description, ref),
      // Bloc "Informations légales" de l'agence (RCS, SIRET), à côté du contact.
      agencySiren: extractSiren(main.textContent),
      descriptionExcerpt: description ? normalizeText(description).slice(0, 600) : void 0
    };
  }
  function otherRefs(description, mainRef) {
    const main = mainRef?.toUpperCase();
    const list = extractRefs(description).filter((r) => r !== main);
    return list.length ? list : void 0;
  }

  // src/sites/seloger/adapter.ts
  var LIST_API_RE = /\/classifiedList\/[^/?#]+/;
  var selogerAdapter = {
    id: "seloger",
    label: "SeLoger",
    matches: (loc) => /(^|\.)seloger\.com$/.test(loc.hostname),
    // "/classified-search?..." (recherche avec filtres) ou "/recherche/location/appartement/<région>/<ville>/<id>" (page de ville).
    isSearchPage: (loc) => /^\/(classified-search|recherche\/|list\.htm)/.test(loc.pathname),
    findCards(root2) {
      return [...root2.querySelectorAll(CARD_SELECTOR2)].map((element) => ({ element, siteId: cardSiteId(element) ?? "" })).filter((c) => c.siteId);
    },
    parseCard: (card) => parseSelogerCard(card.element),
    hideSelectors: AD_SELECTORS2,
    findDetail(root2) {
      const main = root2.querySelector(DETAIL_SELECTOR2);
      const siteId = main && siteIdFromUrl(location.href);
      if (!main || !siteId) return void 0;
      const title = main.querySelector('[data-testid="cdp-seo-wrapper"]');
      const anchor = title?.parentElement ?? main;
      return { siteId, element: main, anchor };
    },
    parseDetail: (detail) => parseSelogerDetailDom(detail.element, location.href),
    thumbnailUrl: (url) => /^https:\/\/(cdnihddipa\.cloudimg\.io|mms\.seloger\.com)\//.test(url) ? `${url}${url.includes("?") ? "&" : "?"}w=400&h=300` : url,
    startEnrichment(onData) {
      const win = getRootWindow();
      const sw = win;
      const detail = sw.__UFRN_LIFECYCLE_SERVERREQUEST__?.app_cldp?.data?.classified;
      const pageId = siteIdFromUrl(win.location.href);
      if (detail?.id && detail.id === pageId) {
        safely(() => onData(detail.id, parseSelogerDetailClassified(detail, win.location.href)));
      }
      for (const entry of Object.values(sw.__UFRN_FETCHER__?.data ?? {})) {
        for (const c of Object.values(entry?.pageProps?.classifiedsData ?? {})) {
          if (c?.id) safely(() => onData(c.id, parseSelogerSerpClassified(c)));
        }
      }
      const done = /* @__PURE__ */ new Set();
      const handle = async (url) => {
        if (!LIST_API_RE.test(url) || done.has(url)) return;
        done.add(url);
        try {
          const res = await win.fetch(url, { credentials: "include" });
          if (!res.ok) return;
          const json = await res.json();
          const list = Array.isArray(json) ? json : json.classifieds ?? [];
          for (const c of list) if (c?.id) onData(c.id, parseSelogerSerpClassified(c));
        } catch (error) {
          console.warn("[RechercheLogement] Échec de l'enrichissement SeLoger", error);
        }
      };
      for (const e of win.performance.getEntriesByType("resource")) void handle(e.name);
      if (typeof win.PerformanceObserver === "function") {
        new win.PerformanceObserver((list) => {
          for (const e of list.getEntries()) void handle(e.name);
        }).observe({ type: "resource", buffered: false });
      }
    }
  };
  function safely(fn) {
    try {
      fn();
    } catch (error) {
      console.warn("[RechercheLogement] JSON SeLoger illisible", error);
    }
  }

  // src/sites/index.ts
  var ADAPTERS = [bieniciAdapter, selogerAdapter];
  function findAdapter(loc) {
    return ADAPTERS.find((a) => a.matches(loc));
  }
  function siteLabel(id) {
    return ADAPTERS.find((a) => a.id === id)?.label ?? id;
  }

  // src/shared/dom/h.ts
  function h(tag, props = null, ...children) {
    const el = document.createElement(tag);
    if (props) {
      const { style, class: cls, on, attrs, ...rest } = props;
      if (style) Object.assign(el.style, style);
      if (cls) el.className = cls;
      if (on) for (const [ev, fn] of Object.entries(on)) el.addEventListener(ev, fn);
      if (attrs) for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
      Object.assign(el, rest);
    }
    for (const c of children.flat()) {
      if (c === null || c === void 0 || c === false) continue;
      el.append(typeof c === "number" ? String(c) : c);
    }
    return el;
  }

  // src/ui/theme.ts
  var fontFamily = 'system-ui, -apple-system, "Segoe UI", sans-serif';
  var THEME = {
    fontFamily,
    font: `13px/1.45 ${fontFamily}`,
    bg: "#111827",
    bgSoft: "#1f2937",
    fg: "#f9fafb",
    muted: "#9ca3af",
    border: "rgba(255,255,255,.12)",
    accent: "#2563eb",
    seen: "#6b7280",
    rejected: "#dc2626",
    toContact: "#16a34a",
    suggest: "#d97706",
    ok: "#16a34a"
  };
  var STATUS_LABEL = {
    none: "Aucun statut",
    seen: "Vue",
    rejected: "Pas intéressé (masquée)",
    toContact: "Me plaît : à contacter"
  };
  var CONTACT_STAGE_LABEL = {
    pending: "À contacter",
    contacted: "Agence contactée",
    visitScheduled: "Visite prévue",
    visited: "Visitée",
    applicationSent: "Dossier envoyé",
    accepted: "Dossier accepté",
    declined: "Refusé"
  };
  function buttonStyle(bg = THEME.bgSoft) {
    return {
      border: `1px solid ${THEME.border}`,
      background: bg,
      color: THEME.fg,
      borderRadius: "6px",
      padding: "6px 10px",
      cursor: "pointer",
      font: `500 12px/1.2 ${fontFamily}`
    };
  }

  // src/ui/cardToolbar.ts
  var TOOLBAR_HOST_ATTR = "data-tmrl-toolbar";
  function renderToolbar(anchor, model, handlers, variant = "card") {
    if (variant === "detail") return renderDetailBar(anchor, model, handlers);
    let host = anchor.querySelector(`:scope > [${TOOLBAR_HOST_ATTR}]`);
    if (!host) {
      host = document.createElement("div");
      host.setAttribute(TOOLBAR_HOST_ATTR, "");
      Object.assign(host.style, { position: "absolute", top: "6px", left: "6px", right: "6px", zIndex: "20", pointerEvents: "none" });
      for (const ev of ["click", "mousedown", "mouseup", "pointerdown", "pointerup"]) {
        host.addEventListener(ev, (e) => {
          e.stopPropagation();
          if (ev === "click") e.preventDefault();
        });
      }
      host.attachShadow({ mode: "open" });
      anchor.appendChild(host);
    }
    const contact = model.status === "toContact";
    const children = [
      h("style", null, `:host{all:initial} button{pointer-events:auto;transition:transform .1s} button:hover{transform:scale(1.08)}`),
      h(
        "div",
        { style: { display: "flex", flexWrap: "wrap", gap: "4px", alignItems: "center", font: `600 12px/1 ${THEME.fontFamily}` } },
        statusButton("👁", "Vue : garder l'annonce visible, mais atténuée", "seen", model.status, THEME.seen, handlers),
        statusButton("✕", "Pas intéressé : masquer l'annonce", "rejected", model.status, THEME.rejected, handlers),
        statusButton("📞", "Me plaît : à contacter", "toContact", model.status, THEME.toContact, handlers),
        pill(model.hasNote ? "📝" : "⋯", model.hasNote ? "Note et détails" : "Détails et note", THEME.bg, handlers.onDetails),
        model.siblings > 0 && pill(`🔗 ${model.siblings}`, model.siblingsTitle, THEME.accent, handlers.onDetails),
        model.suggestion && pill(
          `≈ ${model.suggestion.danger ? "déjà masquée" : "déjà vue"} ? ${Math.round(model.suggestion.score * 100)} %`,
          model.suggestion.label,
          model.suggestion.danger ? THEME.rejected : THEME.suggest,
          handlers.onSuggestion
        )
      )
    ];
    if (contact) {
      children.push(
        h(
          "div",
          { style: { marginTop: "4px" } },
          pill(`📞 ${CONTACT_STAGE_LABEL[model.contactStage ?? "pending"]}`, "Suivi du contact. Modifier l'étape sur le serveur local.", THEME.toContact, handlers.onDetails)
        )
      );
    }
    host.shadowRoot.replaceChildren(...children);
  }
  function renderDetailBar(anchor, model, handlers) {
    let host = anchor.querySelector(`:scope > [${TOOLBAR_HOST_ATTR}]`);
    if (!host) {
      host = document.createElement("div");
      host.setAttribute(TOOLBAR_HOST_ATTR, "detail");
      Object.assign(host.style, { display: "block", position: "relative", zIndex: "20", margin: "0 0 12px" });
      for (const ev of ["click", "mousedown", "mouseup", "pointerdown", "pointerup"]) host.addEventListener(ev, (e) => e.stopPropagation());
      host.attachShadow({ mode: "open" });
      anchor.prepend(host);
    } else if (anchor.firstElementChild !== host) {
      anchor.prepend(host);
    }
    const s = model.status;
    const statusText = !model.known ? "Envoi au serveur local..." : s === "rejected" ? "Pas intéressé : masquée dans les listes" : s === "seen" ? "Déjà vue" : s === "toContact" ? `📞 ${CONTACT_STAGE_LABEL[model.contactStage ?? "pending"]}` : "Aucun statut";
    const accent = { none: THEME.border, seen: THEME.seen, rejected: THEME.rejected, toContact: THEME.toContact }[s];
    host.shadowRoot.replaceChildren(
      h("style", null, `:host{all:initial} button{transition:transform .1s} button:hover{transform:scale(1.04)}`),
      h(
        "div",
        {
          style: {
            display: "flex",
            flexWrap: "wrap",
            gap: "6px",
            alignItems: "center",
            padding: "8px 10px",
            borderRadius: "8px",
            background: THEME.bg,
            borderLeft: `5px solid ${accent}`,
            color: THEME.fg,
            font: `600 12px/1.2 ${THEME.fontFamily}`,
            boxShadow: "0 1px 4px rgba(0,0,0,.25)"
          }
        },
        h("span", { style: { marginRight: "4px" } }, "🏠 Suivi"),
        labeledStatus("👁 Vue", "seen", s, THEME.seen, handlers),
        labeledStatus("✕ Pas intéressé", "rejected", s, THEME.rejected, handlers),
        labeledStatus("📞 À contacter", "toContact", s, THEME.toContact, handlers),
        pill(model.hasNote ? "📝 Note" : "⋯ Détails / note", "Note, annonces associées, lien vers le serveur local", THEME.bgSoft, handlers.onDetails),
        model.siblings > 0 && pill(`🔗 ${model.siblings} autre(s) annonce(s)`, model.siblingsTitle, THEME.accent, handlers.onDetails),
        model.suggestion && pill(
          `≈ ${model.suggestion.danger ? "déjà masquée" : "déjà vue"} ? ${Math.round(model.suggestion.score * 100)} %`,
          model.suggestion.label,
          model.suggestion.danger ? THEME.rejected : THEME.suggest,
          handlers.onSuggestion
        ),
        h("span", { style: { marginLeft: "auto", color: THEME.muted, fontWeight: "500" } }, statusText)
      )
    );
  }
  function labeledStatus(label, status, current, color, handlers) {
    const active = current === status;
    const b = pill(label, active ? "Statut actuel. Cliquer pour annuler." : `Passer en « ${label} »`, active ? color : THEME.bgSoft, () => handlers.onStatus(active ? "none" : status));
    if (active) b.style.border = "2px solid #fff";
    return b;
  }
  function statusButton(icon, title, status, current, color, handlers) {
    const active = current === status;
    return h(
      "button",
      {
        type: "button",
        title: active ? `${title}. Cliquer pour annuler.` : title,
        style: {
          width: "28px",
          height: "28px",
          borderRadius: "50%",
          border: active ? "2px solid #fff" : `1px solid ${THEME.border}`,
          background: active ? color : "rgba(17,24,39,.85)",
          color: "#fff",
          cursor: "pointer",
          font: `14px/1 ${THEME.fontFamily}`,
          padding: "0",
          boxShadow: "0 1px 4px rgba(0,0,0,.4)"
        },
        on: { click: () => handlers.onStatus(active ? "none" : status) }
      },
      icon
    );
  }
  function pill(text, title, bg, onClick) {
    return h(
      "button",
      {
        type: "button",
        title,
        style: {
          height: "28px",
          padding: "0 9px",
          borderRadius: "14px",
          border: `1px solid ${THEME.border}`,
          background: bg,
          color: "#fff",
          cursor: "pointer",
          font: `600 12px/1 ${THEME.fontFamily}`,
          boxShadow: "0 1px 4px rgba(0,0,0,.4)",
          whiteSpace: "nowrap"
        },
        on: { click: onClick }
      },
      text
    );
  }

  // src/ui/pageStyles.ts
  var STYLE_ID = "tmrl-page-styles";
  function installPageStyles(hideSelectors = []) {
    if (document.getElementById(STYLE_ID)) return;
    const style = document.createElement("style");
    style.id = STYLE_ID;
    style.textContent = pageCss(hideSelectors);
    (document.head ?? document.documentElement).appendChild(style);
  }
  function pageCss(hideSelectors = []) {
    const card = "[data-tmrl-card]:not([data-tmrl-detail])";
    const hide = hideSelectors.length ? `${hideSelectors.join(",\n")} { display: none !important; }
` : "";
    return `
${card} { position: relative !important; }
${card}[data-tmrl-hidden] { display: none !important; }
html[data-tmrl-show-hidden] ${card}[data-tmrl-hidden] {
  display: block !important; opacity: .4; outline: 2px dashed #dc2626; outline-offset: 2px;
}
${card}[data-tmrl-status="seen"] { opacity: .55; transition: opacity .15s; }
${card}[data-tmrl-status="seen"]:hover { opacity: 1; }
${card}[data-tmrl-status="toContact"] { outline: 4px solid #16a34a; outline-offset: 2px; border-radius: 6px; box-shadow: 0 0 0 8px rgba(22,163,74,.18); }
${card}[data-tmrl-dup="rejected"]:not([data-tmrl-status]) { outline: 3px dashed #d97706; outline-offset: 2px; }
${hide}`;
  }
  function setShowHidden(show) {
    document.documentElement.toggleAttribute("data-tmrl-show-hidden", show);
  }

  // src/shared/dom/uiRoot.ts
  var HOST_ID = "tmrl-ui-root";
  var root = null;
  function getUIRoot() {
    if (root && root.host.isConnected) return root;
    const host = document.createElement("div");
    host.id = HOST_ID;
    document.body.appendChild(host);
    root = host.attachShadow({ mode: "open" });
    const reset = document.createElement("style");
    reset.textContent = ":host { all: initial; }";
    root.appendChild(reset);
    return root;
  }

  // src/ui/modal.ts
  var BACKDROP_ID = "tmrl-modal-backdrop";
  var backdropEl = null;
  var keydownHandler = null;
  var onCloseCb;
  function showModal(options) {
    closeModal();
    const root2 = getUIRoot();
    const backdrop = document.createElement("div");
    backdrop.id = BACKDROP_ID;
    Object.assign(backdrop.style, {
      position: "fixed",
      inset: "0",
      zIndex: "2147483647",
      display: "flex",
      alignItems: "center",
      justifyContent: "center",
      background: "rgba(0,0,0,.5)",
      font: THEME.font
    });
    backdrop.addEventListener("click", (event) => {
      if (event.target === backdrop) closeModal();
    });
    const box = document.createElement("div");
    Object.assign(box.style, {
      width: options.width ?? "min(560px, 94vw)",
      maxHeight: "86vh",
      display: "flex",
      flexDirection: "column",
      background: THEME.bg,
      color: THEME.fg,
      borderRadius: "12px",
      boxShadow: "0 8px 30px rgba(0,0,0,.4)"
    });
    const header = document.createElement("div");
    Object.assign(header.style, {
      display: "flex",
      alignItems: "center",
      justifyContent: "space-between",
      gap: "12px",
      padding: "12px 8px 12px 16px",
      borderBottom: `1px solid ${THEME.border}`
    });
    const title = document.createElement("strong");
    title.textContent = options.title;
    title.style.font = `600 15px/1.4 ${THEME.fontFamily}`;
    const closeButton = document.createElement("button");
    closeButton.type = "button";
    closeButton.textContent = "✕";
    closeButton.setAttribute("aria-label", "Fermer");
    Object.assign(closeButton.style, {
      border: "none",
      background: "transparent",
      color: THEME.muted,
      cursor: "pointer",
      padding: "6px 10px",
      font: `16px/1 ${THEME.fontFamily}`
    });
    closeButton.addEventListener("click", closeModal);
    header.append(title, closeButton);
    const body = document.createElement("div");
    Object.assign(body.style, { padding: "16px", overflowY: "auto" });
    body.append(options.content);
    box.append(header, body);
    backdrop.appendChild(box);
    root2.appendChild(backdrop);
    backdropEl = backdrop;
    onCloseCb = options.onClose;
    keydownHandler = (event) => {
      if (event.key === "Escape") closeModal();
    };
    document.addEventListener("keydown", keydownHandler, true);
  }
  function isModalOpen() {
    return backdropEl !== null;
  }
  function closeModal() {
    if (!backdropEl) return;
    backdropEl.remove();
    backdropEl = null;
    if (keydownHandler) {
      document.removeEventListener("keydown", keydownHandler, true);
      keydownHandler = null;
    }
    const cb = onCloseCb;
    onCloseCb = void 0;
    cb?.();
  }

  // src/ui/views.ts
  var fmtPrice = (n) => n === void 0 ? "" : `${n.toLocaleString("fr-FR")} €`;
  function section(title, ...children) {
    return h(
      "section",
      { style: { marginBottom: "18px" } },
      h("h3", { style: { margin: "0 0 8px", font: `600 13px/1.3 ${THEME.fontFamily}`, color: THEME.muted, textTransform: "uppercase", letterSpacing: ".04em" } }, title),
      ...children
    );
  }
  function button(label, onClick, bg) {
    return h("button", { type: "button", style: buttonStyle(bg), on: { click: onClick } }, label);
  }
  function link(ref) {
    const label = `${siteLabel(ref.site)} « ${ref.title ?? ref.key} » ${fmtPrice(ref.price)}`;
    return ref.url ? h("a", { href: ref.url, target: "_blank", rel: "noopener", style: { color: "#93c5fd" } }, `${label} ↗`) : h("span", null, label);
  }
  function webLink(url, label = "Ouvrir sur le serveur local ↗") {
    return url ? h("a", { href: url, target: "_blank", rel: "noopener", style: { color: "#93c5fd" } }, label) : null;
  }
  function statusBadge(status) {
    const color = { none: THEME.bgSoft, seen: THEME.seen, rejected: THEME.rejected, toContact: THEME.toContact }[status];
    return h("span", { style: { background: color, borderRadius: "4px", padding: "2px 6px", font: `600 11px/1.4 ${THEME.fontFamily}` } }, STATUS_LABEL[status]);
  }
  function showSuggestion(sync, key, s) {
    showModal({
      title: `Même bien ? (score ${Math.round(s.score * 100)} %)`,
      content: h(
        "div",
        null,
        section("Autre annonce", h("div", null, link(s.other)), h("div", { style: { marginTop: "6px" } }, "Statut : ", statusBadge(s.otherStatus))),
        section("Pourquoi", h("ul", { style: { margin: "0", paddingLeft: "18px" } }, s.reasons.map((r) => h("li", null, r)))),
        h(
          "div",
          { style: { display: "flex", gap: "8px", justifyContent: "space-between", alignItems: "center", flexWrap: "wrap" } },
          webLink(sync.view(key)?.webUrl, "Comparer en détail sur le serveur local ↗"),
          h(
            "div",
            { style: { display: "flex", gap: "8px" } },
            button("Pas le même bien", () => {
              sync.act({ type: "dismissDuplicate", key, otherKey: s.other.key });
              closeModal();
            }),
            button("✔ Même bien", () => {
              sync.act({ type: "confirmDuplicate", key, otherKey: s.other.key });
              closeModal();
            }, THEME.ok)
          )
        )
      )
    });
  }
  function showDetails(sync, key) {
    const v = sync.view(key);
    const status = v?.status ?? "none";
    const note = h("textarea", {
      value: v?.note ?? "",
      placeholder: "Note personnelle (contact, visite, avis...)",
      rows: 4,
      style: { width: "100%", boxSizing: "border-box", background: THEME.bgSoft, color: THEME.fg, border: `1px solid ${THEME.border}`, borderRadius: "6px", padding: "8px", font: THEME.font }
    });
    const statuses = ["none", "seen", "rejected", "toContact"];
    showModal({
      title: "Annonce",
      content: h(
        "div",
        null,
        section(
          "Statut du bien",
          h("div", { style: { display: "flex", gap: "6px", flexWrap: "wrap" } }, statuses.map(
            (s) => button(STATUS_LABEL[s], () => {
              sync.act({ type: "setStatus", key, status: s });
              showDetails(sync, key);
            }, s === status ? THEME.accent : void 0)
          )),
          status === "toContact" && h("div", { style: { marginTop: "8px" } }, `Suivi : ${CONTACT_STAGE_LABEL[v?.contactStage ?? "pending"]}. Modifier l'étape sur le serveur local.`)
        ),
        section("Note", note, h("div", { style: { marginTop: "6px", textAlign: "right" } }, button("Enregistrer la note", () => {
          sync.act({ type: "setNote", key, note: note.value.trim() });
          closeModal();
        }, THEME.ok))),
        section(
          `Même bien sur d'autres annonces (${v?.siblings.length ?? 0})`,
          !v?.siblings.length ? h("div", { style: { color: THEME.muted } }, "Aucune annonce associée.") : h("ul", { style: { margin: "0", paddingLeft: "18px" } }, v.siblings.map((s) => h("li", { style: { marginBottom: "4px" } }, link(s)))),
          !!v?.siblings.length && h("div", { style: { marginTop: "6px" } }, button("Dissocier cette annonce du bien", () => {
            sync.act({ type: "detach", key });
            closeModal();
          }))
        ),
        !!v?.suggestions.length && section(
          `Doublons probables (${v.suggestions.length})`,
          h("ul", { style: { margin: "0", paddingLeft: "18px" } }, v.suggestions.map(
            (s) => h(
              "li",
              { style: { marginBottom: "4px" } },
              link(s.other),
              ` — ${Math.round(s.score * 100)} % `,
              statusBadge(s.otherStatus),
              " ",
              button("Décider", () => showSuggestion(sync, key, s))
            )
          ))
        ),
        h("div", { style: { display: "flex", justifyContent: "space-between", color: THEME.muted, fontSize: "11px" } }, `Clé : ${key}`, webLink(v?.webUrl) ?? "")
      )
    });
  }
  function showPanel(ctx) {
    const { sync } = ctx;
    const page = ctx.pageCounts();
    const conn = ctx.connection.get();
    const toggle = h("input", { type: "checkbox", checked: ctx.showHidden, on: { change: () => ctx.setShowHidden(toggle.checked) } });
    const stateText = {
      online: ["🟢", "Connecté au serveur local."],
      offline: ["🟠", "Le serveur local ne répond pas. Le plugin garde les actions. Il les envoie quand le serveur répond de nouveau."],
      unauthorized: ["🔴", "Le serveur local refuse le token. Copier de nouveau le token depuis la page Paramètres du serveur local."],
      unconfigured: ["🔴", "Token absent. Coller le token ci-dessous (page Paramètres du serveur local)."],
      unknown: ["⚪", "Connexion en cours..."]
    }[sync.state];
    const input = (value, placeholder, type = "text") => h("input", {
      value,
      placeholder,
      type,
      spellcheck: false,
      style: { width: "100%", boxSizing: "border-box", background: THEME.bgSoft, color: THEME.fg, border: `1px solid ${THEME.border}`, borderRadius: "6px", padding: "7px 8px", font: THEME.font }
    });
    const urlInput = input(conn.serverUrl, "http://localhost:5080");
    const tokenInput = input(conn.token, "Token (page Paramètres du serveur local)", "password");
    const feedback = h("div", { style: { minHeight: "18px", marginTop: "6px", fontSize: "12px" } });
    const current = () => ({ serverUrl: urlInput.value, token: tokenInput.value });
    const say = (text, color) => {
      feedback.textContent = text;
      feedback.style.color = color;
    };
    showModal({
      title: "🏠 Suivi de recherche logement",
      content: h(
        "div",
        null,
        section(
          "Connexion au serveur local",
          h("div", null, `${stateText[0]} ${stateText[1]}`),
          sync.lastError && !["online", "unconfigured"].includes(sync.state) && h("div", { style: { color: THEME.muted, fontSize: "11px" } }, sync.lastError),
          h("div", { style: { marginTop: "4px", color: THEME.muted } }, `${sync.pendingCount()} élément(s) en attente d'envoi`),
          h(
            "div",
            { style: { display: "grid", gridTemplateColumns: "60px 1fr", gap: "6px 8px", alignItems: "center", marginTop: "10px" } },
            h("label", null, "URL"),
            urlInput,
            h("label", null, "Token"),
            tokenInput
          ),
          feedback,
          h(
            "div",
            { style: { display: "flex", gap: "8px", marginTop: "4px", alignItems: "center", flexWrap: "wrap" } },
            button("Tester", () => {
              say("Test en cours...", THEME.muted);
              ctx.testConnection(current()).then((ok) => say(`✔ ${ok}`, THEME.ok), (e) => say(`✕ ${e.message}`, THEME.rejected));
            }),
            button("Enregistrer", () => {
              ctx.connection.save(current());
              say("Réglages enregistrés. Synchronisation relancée.", THEME.ok);
              setTimeout(() => showPanel(ctx), 1500);
            }, THEME.ok),
            webLink(`${conn.serverUrl}/parametres`, "Obtenir le token ↗")
          )
        ),
        ctx.searches && searchesSection(ctx.searches, conn.serverUrl),
        section(
          "Cette page",
          h("div", null, `${page.total} annonces · ${page.hidden} masquées · ${page.seen} vues · ${page.toContact} à contacter · ${page.suggested} doublons probables`),
          h("label", { style: { display: "flex", gap: "8px", alignItems: "center", marginTop: "8px", cursor: "pointer" } }, toggle, "Afficher les annonces masquées (en pointillés)")
        ),
        ctx.display && displaySection(ctx.display),
        h(
          "div",
          { style: { display: "flex", justifyContent: "space-between", color: THEME.muted, fontSize: "11px" } },
          `Plugin v${ctx.version}`,
          webLink(conn.serverUrl, "Ouvrir le serveur local ↗") ?? ""
        )
      )
    });
  }
  function searchesSection(s, serverUrl) {
    const nameInput = h("input", {
      value: s.defaultName,
      placeholder: "Nom de la recherche",
      style: { flex: "1", minWidth: "0", background: THEME.bgSoft, color: THEME.fg, border: `1px solid ${THEME.border}`, borderRadius: "6px", padding: "7px 8px", font: THEME.font }
    });
    const feedback = h("div", { style: { minHeight: "16px", marginTop: "6px", fontSize: "12px" } });
    const list = h("ul", { style: { margin: "8px 0 0", paddingLeft: "18px" } });
    const refresh = () => s.list().then(
      (all) => {
        const mine = all.filter((x) => x.site === s.site);
        list.replaceChildren(
          ...mine.length ? mine.map(
            (x) => h("li", { style: { marginBottom: "3px" } }, h("a", { href: x.url, title: x.url, style: { color: "#93c5fd" } }, x.name), x.note ? h("span", { style: { color: THEME.muted } }, ` — ${x.note}`) : null)
          ) : [h("li", { style: { color: THEME.muted, listStyle: "none", marginLeft: "-18px" } }, "Aucune recherche enregistrée pour ce site.")]
        );
      },
      () => list.replaceChildren(h("li", { style: { color: THEME.muted, listStyle: "none", marginLeft: "-18px" } }, "Liste indisponible (serveur local injoignable)."))
    );
    void refresh();
    return section(
      "⭐ Recherches favorites",
      s.canSaveCurrent && h(
        "div",
        { style: { display: "flex", gap: "8px", alignItems: "center" } },
        nameInput,
        button("⭐ Enregistrer cette recherche", () => {
          feedback.textContent = "Enregistrement...";
          feedback.style.color = THEME.muted;
          s.save(nameInput.value.trim()).then(
            (x) => {
              feedback.textContent = `✔ « ${x.name} » enregistrée. Vous pouvez enregistrer plusieurs variantes.`;
              feedback.style.color = THEME.ok;
              void refresh();
            },
            (e) => {
              feedback.textContent = `✕ ${e.message}`;
              feedback.style.color = THEME.rejected;
            }
          );
        }, THEME.ok)
      ),
      s.canSaveCurrent && feedback,
      list,
      h("div", { style: { marginTop: "6px" } }, webLink(`${serverUrl}/recherches`, "Gérer les recherches sur le serveur local ↗"))
    );
  }
  function displaySection(display) {
    const prefs = display.get();
    const row = (label, checked, onChange) => {
      const box = h("input", { type: "checkbox", checked, on: { change: () => onChange(box.checked) } });
      return h("label", { style: { display: "flex", gap: "8px", alignItems: "center", marginTop: "6px", cursor: "pointer" } }, box, label);
    };
    const seen = row("Masquer les annonces vues 👁", prefs.hideSeen, (v) => display.set({ hideSeen: v }));
    const contact = row("Masquer les annonces à contacter 📞", prefs.hideToContact, (v) => display.set({ hideToContact: v }));
    return section(
      "Affichage",
      h("div", { style: { color: THEME.muted } }, "Les annonces « pas intéressé » sont toujours masquées. Cocher les deux cases pour ne voir que les nouvelles annonces."),
      seen,
      contact,
      h(
        "div",
        { style: { marginTop: "8px" } },
        button(display.onlyNew ? "Tout réafficher (vues et à contacter)" : "Nouvelles annonces seulement", () => {
          const on = !display.onlyNew;
          display.set({ hideSeen: on, hideToContact: on });
          seen.querySelector("input").checked = on;
          contact.querySelector("input").checked = on;
        })
      )
    );
  }

  // src/app/tracker.ts
  var Tracker = class {
    constructor(adapter, sync, options) {
      this.adapter = adapter;
      this.sync = sync;
      this.options = options;
      this.processed = /* @__PURE__ */ new WeakMap();
      this.renderScheduled = false;
      this.counts = { total: 0, hidden: 0, seen: 0, toContact: 0, suggested: 0 };
      this.countListeners = /* @__PURE__ */ new Set();
      this.showHidden = false;
      this.pendingApi = /* @__PURE__ */ new Map();
    }
    start() {
      installPageStyles(this.adapter.hideSelectors);
      const observer = new MutationObserver((records) => {
        if (records.some((r) => !isOwnMutation(r))) this.schedule();
      });
      observer.observe(this.adapter.observeRoot?.() ?? document.body, {
        childList: true,
        subtree: true,
        attributes: true,
        attributeFilter: ["data-id", "href"]
      });
      this.adapter.startEnrichment?.((siteId, data) => {
        if (!document.querySelector(`[data-tmrl-card="${CSS.escape(siteId)}"]`)) {
          this.pendingApi.set(siteId, data);
          return;
        }
        this.sync.observe({ site: this.adapter.id, siteId, source: "api", data, seenAt: Date.now() });
      });
      this.sync.onChange(() => this.schedule());
      document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "visible") {
          const ids = this.adapter.findCards(document).map((c) => c.siteId);
          const detail = this.adapter.findDetail?.(document);
          if (detail) ids.push(detail.siteId);
          this.sync.refresh(ids.map((id) => listingKey(this.adapter.id, id)));
        }
      });
      this.schedule();
    }
    onCounts(cb) {
      this.countListeners.add(cb);
      cb(this.counts);
    }
    pageCounts() {
      return this.counts;
    }
    /** Change les statuts masqués (réglages du panneau) et réapplique l'affichage. */
    setHideStatuses(statuses) {
      this.options.hideStatuses = statuses;
      this.schedule();
    }
    setShowHidden(show) {
      this.showHidden = show;
      setShowHidden(show);
    }
    schedule() {
      if (this.renderScheduled) return;
      this.renderScheduled = true;
      setTimeout(() => {
        this.renderScheduled = false;
        this.scan();
      }, 120);
    }
    scan() {
      const cards = this.adapter.findCards(document);
      const counts = { total: cards.length, hidden: 0, seen: 0, toContact: 0, suggested: 0 };
      for (const card of cards) {
        this.ingest(card);
        const s = this.apply(card);
        if (s.hidden) counts.hidden++;
        if (s.status === "seen") counts.seen++;
        if (s.status === "toContact") counts.toContact++;
        if (s.suggested) counts.suggested++;
      }
      const detail = this.adapter.findDetail?.(document);
      if (detail) {
        this.ingestDetail(detail);
        this.apply(detail, true);
      }
      this.counts = counts;
      for (const cb of this.countListeners) cb(counts);
      if (this.options.debug) console.debug("[RechercheLogement] scan", counts);
    }
    /** Envoie la carte au serveur une fois par couple élément/annonce. Les SPA réutilisent les éléments. */
    ingest(card) {
      if (this.processed.get(card.element) === card.siteId) return;
      this.processed.set(card.element, card.siteId);
      card.element.setAttribute("data-tmrl-card", card.siteId);
      const now = Date.now();
      this.sync.observe({ site: this.adapter.id, siteId: card.siteId, source: "card", data: this.adapter.parseCard(card), seenAt: now });
      const api = this.pendingApi.get(card.siteId);
      if (api) {
        this.pendingApi.delete(card.siteId);
        this.sync.observe({ site: this.adapter.id, siteId: card.siteId, source: "api", data: api, seenAt: now });
      }
    }
    /** Fiche d'annonce : envoyée une fois par annonce (source 'detail', moins précise que le JSON du site). */
    ingestDetail(detail) {
      if (this.processed.get(detail.element) === detail.siteId) return;
      this.processed.set(detail.element, detail.siteId);
      detail.element.setAttribute("data-tmrl-card", detail.siteId);
      detail.element.setAttribute("data-tmrl-detail", "");
      const now = Date.now();
      const data = this.adapter.parseDetail?.(detail);
      if (data) this.sync.observe({ site: this.adapter.id, siteId: detail.siteId, source: "detail", data, seenAt: now });
      const api = this.pendingApi.get(detail.siteId);
      if (api) {
        this.pendingApi.delete(detail.siteId);
        this.sync.observe({ site: this.adapter.id, siteId: detail.siteId, source: "api", data: api, seenAt: now });
      }
      this.sync.refresh([listingKey(this.adapter.id, detail.siteId)]);
    }
    apply(card, isDetail = false) {
      const key = listingKey(this.adapter.id, card.siteId);
      const el = card.element;
      const view = this.sync.view(key);
      const status = view?.status ?? "none";
      const best = view?.suggestions[0];
      let hidden = this.options.hideStatuses.includes(status);
      if (!hidden && status === "none" && best && this.options.hideSuggestedDuplicatesOf.includes(best.otherStatus)) hidden = true;
      if (isDetail) hidden = false;
      setAttr(el, "data-tmrl-status", status === "none" ? null : status);
      setAttr(el, "data-tmrl-hidden", hidden ? "" : null);
      setAttr(el, "data-tmrl-dup", best ? best.otherStatus === "rejected" ? "rejected" : "suggested" : null);
      renderToolbar(
        isDetail ? card.anchor : this.adapter.toolbarAnchor?.(card) ?? el,
        {
          status,
          contactStage: view?.contactStage,
          hasNote: !!view?.note,
          known: !!view,
          siblings: view?.siblings.length ?? 0,
          siblingsTitle: `Même bien que : ${(view?.siblings ?? []).map((s) => `${siteLabel(s.site)} « ${s.title ?? s.key} »`).join(", ")}`,
          suggestion: best && {
            score: best.score,
            danger: best.otherStatus === "rejected",
            label: `Probablement le même bien que ${siteLabel(best.other.site)} « ${best.other.title ?? best.other.key} ». Raisons : ${best.reasons.join(", ")}. Cliquer pour décider.`
          }
        },
        {
          onStatus: (s) => this.sync.act({ type: "setStatus", key, status: s }),
          onDetails: () => showDetails(this.sync, key),
          onSuggestion: () => best && showSuggestion(this.sync, key, best)
        },
        isDetail ? "detail" : "card"
      );
      return { hidden, status, suggested: !!best };
    }
  };
  function isOwnMutation(r) {
    if (r.type !== "childList") return false;
    const nodes = [...r.addedNodes, ...r.removedNodes];
    return nodes.length > 0 && nodes.every((n) => n instanceof Element && n.hasAttribute(TOOLBAR_HOST_ATTR));
  }
  function setAttr(el, name, value) {
    if (value === null) {
      if (el.hasAttribute(name)) el.removeAttribute(name);
    } else if (el.getAttribute(name) !== value) {
      el.setAttribute(name, value);
    }
  }

  // src/core/api.ts
  var ApiError = class extends Error {
    constructor(message, status) {
      super(message);
      this.status = status;
    }
  };
  var ApiClient = class {
    constructor(options) {
      this.options = { ...options, baseUrl: options.baseUrl.replace(/\/+$/, "") };
    }
    get baseUrl() {
      return this.options.baseUrl;
    }
    /** Change l'URL et le token (après une modification des réglages dans le panneau). */
    configure(baseUrl, token) {
      this.options = { ...this.options, baseUrl: baseUrl.replace(/\/+$/, ""), token };
    }
    hasToken() {
      return this.options.token.length > 0;
    }
    sync(request) {
      return this.request("POST", "/api/sync", request);
    }
    ping() {
      return this.request("GET", "/api/ping");
    }
    /** Recherches favorites enregistrées sur le serveur local. */
    searches() {
      return this.request("GET", "/api/searches");
    }
    /** Enregistre une recherche favorite. Les doublons sont permis (plusieurs variantes d'une même recherche). */
    addSearch(search) {
      return this.request("POST", "/api/searches", search);
    }
    request(method, path, body) {
      const url = this.baseUrl + path;
      const headers = {
        Accept: "application/json",
        Authorization: `Bearer ${this.options.token}`
      };
      if (body !== void 0) headers["Content-Type"] = "application/json";
      const data = body === void 0 ? void 0 : JSON.stringify(body);
      const timeout = this.options.timeoutMs ?? 1e4;
      const parse = (status, text) => {
        if (status === 401) throw new ApiError("Le serveur refuse le token.", 401);
        if (status < 200 || status >= 300) throw new ApiError(`HTTP ${status}: ${text.slice(0, 200)}`, status);
        return text ? JSON.parse(text) : void 0;
      };
      if (typeof GM_xmlhttpRequest === "function") {
        return new Promise((resolve, reject) => {
          GM_xmlhttpRequest({
            method,
            url,
            headers,
            data,
            timeout,
            onload: (r) => {
              try {
                resolve(parse(r.status, r.responseText));
              } catch (e) {
                reject(e);
              }
            },
            onerror: () => reject(new ApiError("Le serveur ne répond pas.", 0)),
            ontimeout: () => reject(new ApiError("Le serveur ne répond pas (délai dépassé).", 0))
          });
        });
      }
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), timeout);
      return fetch(url, { method, headers, body: data, signal: controller.signal }).then(async (r) => parse(r.status, await r.text())).catch((e) => {
        throw e instanceof ApiError ? e : new ApiError("Le serveur ne répond pas.", 0);
      }).finally(() => clearTimeout(timer));
    }
  };

  // src/core/connection.ts
  var KEY = "connection";
  var DEFAULT_SERVER_URL = "http://localhost:5080";
  var ConnectionStore = class {
    constructor(store, fallback) {
      this.store = store;
      this.fallback = fallback;
      this.listeners = /* @__PURE__ */ new Set();
      store.onRemoteChange(KEY, () => this.emit());
    }
    get() {
      const saved = this.store.get(KEY, {});
      return {
        serverUrl: normalizeUrl(saved.serverUrl || this.fallback.serverUrl || DEFAULT_SERVER_URL),
        token: (saved.token || this.fallback.token || "").trim()
      };
    }
    isConfigured() {
      return this.get().token.length > 0;
    }
    save(settings) {
      this.store.set(KEY, { serverUrl: normalizeUrl(settings.serverUrl), token: settings.token.trim() });
      this.emit();
    }
    onChange(cb) {
      this.listeners.add(cb);
      return () => this.listeners.delete(cb);
    }
    emit() {
      const s = this.get();
      for (const cb of this.listeners) cb(s);
    }
  };
  function normalizeUrl(url) {
    return (url.trim() || DEFAULT_SERVER_URL).replace(/\/+$/, "");
  }

  // src/core/displayPrefs.ts
  var KEY2 = "display";
  var DEFAULTS = { hideSeen: false, hideToContact: false };
  var DisplayPrefsStore = class {
    constructor(store) {
      this.store = store;
      this.listeners = /* @__PURE__ */ new Set();
      this.prefs = { ...DEFAULTS, ...store.get(KEY2, {}) };
      store.onRemoteChange(KEY2, (v) => {
        this.prefs = { ...DEFAULTS, ...v ?? {} };
        this.emit();
      });
    }
    get() {
      return { ...this.prefs };
    }
    set(patch) {
      this.prefs = { ...this.prefs, ...patch };
      this.store.set(KEY2, this.prefs);
      this.emit();
    }
    /** Mode "nouvelles annonces seulement" : vues et à contacter masquées. */
    get onlyNew() {
      return this.prefs.hideSeen && this.prefs.hideToContact;
    }
    onChange(cb) {
      this.listeners.add(cb);
    }
    emit() {
      for (const cb of this.listeners) cb(this.get());
    }
  };
  function hiddenStatuses(base, prefs) {
    const out = new Set(base);
    if (prefs.hideSeen) out.add("seen");
    if (prefs.hideToContact) out.add("toContact");
    return [...out];
  }

  // src/core/menuCommand.ts
  function registerMenuCommand(label, onCommand) {
    if (typeof GM_registerMenuCommand === "function") {
      GM_registerMenuCommand(label, onCommand);
    }
  }

  // src/core/storage.ts
  function createStore(namespace) {
    const k = (key) => `${namespace}:${key}`;
    if (typeof GM_getValue === "function" && typeof GM_setValue === "function") {
      return {
        get: (key, fallback) => GM_getValue(k(key), fallback),
        set: (key, value) => GM_setValue(k(key), value),
        onRemoteChange(key, cb) {
          if (typeof GM_addValueChangeListener !== "function") return () => {
          };
          const id = GM_addValueChangeListener(k(key), (_name, _old, value, remote) => {
            if (remote) cb(value);
          });
          return () => {
            if (typeof GM_removeValueChangeListener === "function") GM_removeValueChangeListener(id);
          };
        }
      };
    }
    const memory = /* @__PURE__ */ new Map();
    const ls = (() => {
      try {
        return typeof localStorage !== "undefined" ? localStorage : null;
      } catch {
        return null;
      }
    })();
    return {
      get(key, fallback) {
        const raw = ls ? ls.getItem(k(key)) : memory.get(k(key)) ?? null;
        if (raw == null) return fallback;
        try {
          return JSON.parse(raw);
        } catch {
          return fallback;
        }
      },
      set(key, value) {
        const raw = JSON.stringify(value);
        if (ls) ls.setItem(k(key), raw);
        else memory.set(k(key), raw);
      },
      onRemoteChange(key, cb) {
        if (typeof window === "undefined") return () => {
        };
        const handler = (e) => {
          if (e.key === k(key) && e.newValue) cb(JSON.parse(e.newValue));
        };
        window.addEventListener("storage", handler);
        return () => window.removeEventListener("storage", handler);
      }
    };
  }

  // src/core/sync.ts
  var CACHE_KEY = "cache";
  var QUEUE_KEY = "queue";
  var OBS_KEY = "observations";
  var MAX_CACHE = 5e3;
  var MAX_BATCH_OBS = 200;
  var MAX_BACKOFF_MS = 12e4;
  var SyncEngine = class {
    constructor(store, api, options) {
      this.store = store;
      this.api = api;
      this.options = options;
      this.state = "unknown";
      this.want = /* @__PURE__ */ new Set();
      this.timer = null;
      this.inflight = false;
      this.backoffMs = 0;
      this.listeners = /* @__PURE__ */ new Set();
      this.now = options.now ?? Date.now;
      this.cache = store.get(CACHE_KEY, {});
      this.queue = store.get(QUEUE_KEY, []);
      this.pendingObs = store.get(OBS_KEY, {});
      store.onRemoteChange(QUEUE_KEY, (v) => {
        this.queue = mergeQueues(this.queue, v ?? []);
      });
    }
    // ---------------------------------------------------------------- lecture
    view(key) {
      return this.cache[key];
    }
    pendingCount() {
      return this.queue.length + Object.keys(this.pendingObs).length;
    }
    onChange(cb) {
      this.listeners.add(cb);
      return () => this.listeners.delete(cb);
    }
    // ---------------------------------------------------------------- écriture
    observe(obs) {
      const key = `${obs.site}:${obs.siteId}`;
      const prev = this.pendingObs[key];
      this.pendingObs[key] = prev && prev.source === "api" && obs.source !== "api" ? { ...prev, data: { ...obs.data, ...prev.data }, seenAt: obs.seenAt } : prev ? { ...obs, data: { ...prev.data, ...obs.data } } : obs;
      this.store.set(OBS_KEY, this.pendingObs);
      this.schedule();
    }
    /** Demande l'état actuel de ces annonces au prochain envoi. */
    refresh(keys) {
      for (const k of keys) this.want.add(k);
      this.schedule();
    }
    act(action) {
      const full = { ...action, id: newId(), at: this.now() };
      this.queue.push(full);
      this.persistQueue();
      this.applyOptimistic(full);
      this.emit();
      this.schedule(0);
    }
    /** Envoie immédiatement (bouton "Réessayer"). */
    retryNow() {
      this.backoffMs = 0;
      this.schedule(0);
    }
    // ---------------------------------------------------------------- envoi
    schedule(delay = this.options.debounceMs ?? 300) {
      if (this.timer) clearTimeout(this.timer);
      this.timer = setTimeout(() => void this.flush(), Math.max(delay, this.backoffMs));
      this.timer.unref?.();
    }
    async flush() {
      if (this.inflight) return;
      if (!this.api.hasToken()) {
        if (this.state !== "unconfigured") {
          this.state = "unconfigured";
          this.emit();
        }
        return;
      }
      const obsEntries = Object.entries(this.pendingObs).slice(0, MAX_BATCH_OBS);
      const actions = [...this.queue];
      const want = [...this.want];
      if (obsEntries.length === 0 && actions.length === 0 && want.length === 0) return;
      this.inflight = true;
      const request = {
        clientVersion: this.options.clientVersion,
        observations: obsEntries.map(([, o]) => o),
        actions,
        want
      };
      try {
        const res = await this.api.sync(request);
        for (const [k, o] of obsEntries) if (this.pendingObs[k] === o) delete this.pendingObs[k];
        const done = /* @__PURE__ */ new Set([...res.appliedActionIds, ...res.rejectedActionIds]);
        this.queue = this.queue.filter((a) => !done.has(a.id));
        for (const k of want) this.want.delete(k);
        const now = this.now();
        for (const [k, v] of Object.entries(res.listings)) this.cache[k] = { ...v, cachedAt: now };
        for (const a of this.queue) this.applyOptimistic(a);
        this.store.set(OBS_KEY, this.pendingObs);
        this.persistQueue(done);
        this.persistCache();
        this.state = "online";
        this.lastError = void 0;
        this.lastSyncAt = now;
        this.backoffMs = 0;
      } catch (e) {
        const err = e;
        this.state = err.status === 401 ? "unauthorized" : "offline";
        this.lastError = err.message;
        this.backoffMs = Math.min(MAX_BACKOFF_MS, this.backoffMs ? this.backoffMs * 2 : 5e3);
      } finally {
        this.inflight = false;
        this.emit();
        if (this.pendingCount() > 0 || this.want.size > 0) this.schedule();
      }
    }
    // ---------------------------------------------------------------- interne
    /** Applique au cache l'effet visible d'une action, avant la réponse du serveur. */
    applyOptimistic(a) {
      const v = this.cache[a.key] ?? (this.cache[a.key] = emptyView(a.key, this.now()));
      const sameProperty = Object.values(this.cache).filter((c) => c.propertyId === v.propertyId);
      switch (a.type) {
        case "setStatus":
          for (const c of sameProperty) {
            c.status = a.status;
            if (a.status === "toContact" && !c.contactStage) c.contactStage = "pending";
          }
          break;
        case "setNote":
          for (const c of sameProperty) c.note = a.note || void 0;
          break;
        case "confirmDuplicate": {
          const s = v.suggestions.find((x) => x.other.key === a.otherKey);
          v.suggestions = v.suggestions.filter((x) => x.other.key !== a.otherKey);
          if (s) {
            v.siblings = [...v.siblings, s.other];
            if (v.status === "none") v.status = s.otherStatus;
          }
          break;
        }
        case "dismissDuplicate":
          v.suggestions = v.suggestions.filter((x) => x.other.key !== a.otherKey);
          break;
        case "detach":
          v.siblings = [];
          v.propertyId = `local:${a.key}`;
          v.status = "none";
          break;
      }
      this.persistCache();
    }
    persistQueue(done) {
      const stored = this.store.get(QUEUE_KEY, []);
      this.queue = mergeQueues(this.queue, stored).filter((a) => !done?.has(a.id));
      this.store.set(QUEUE_KEY, this.queue);
    }
    persistCache() {
      const keys = Object.keys(this.cache);
      if (keys.length > MAX_CACHE) {
        keys.sort((a, b) => this.cache[a].cachedAt - this.cache[b].cachedAt).slice(0, keys.length - MAX_CACHE).forEach((k) => delete this.cache[k]);
      }
      this.store.set(CACHE_KEY, this.cache);
    }
    emit() {
      for (const cb of this.listeners) cb();
    }
  };
  function emptyView(key, now) {
    return { key, propertyId: `local:${key}`, status: "none", siblings: [], suggestions: [], cachedAt: now };
  }
  function mergeQueues(a, b) {
    const byId = /* @__PURE__ */ new Map();
    for (const x of [...a, ...b]) byId.set(x.id, x);
    return [...byId.values()].sort((x, y) => x.at - y.at);
  }
  function newId() {
    return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
  }

  // src/ui/floatingButton.ts
  function installFloatingButton(options) {
    const root2 = getUIRoot();
    root2.getElementById(options.id)?.remove();
    const button2 = document.createElement("button");
    button2.id = options.id;
    button2.type = "button";
    button2.textContent = options.label;
    button2.title = options.title ?? options.label;
    button2.setAttribute("aria-label", options.title ?? options.label);
    Object.assign(button2.style, {
      position: "fixed",
      right: `${options.offset?.right ?? 16}px`,
      bottom: `${options.offset?.bottom ?? 16}px`,
      zIndex: "2147483646",
      width: "48px",
      height: "48px",
      borderRadius: "50%",
      border: "none",
      background: THEME.accent,
      color: THEME.fg,
      cursor: "pointer",
      font: `20px/1 ${THEME.fontFamily}`,
      boxShadow: "0 4px 14px rgba(0,0,0,.35)"
    });
    button2.addEventListener("click", options.onClick);
    const badge = document.createElement("span");
    Object.assign(badge.style, {
      position: "absolute",
      top: "-4px",
      right: "-4px",
      minWidth: "18px",
      height: "18px",
      padding: "0 5px",
      borderRadius: "9px",
      background: THEME.rejected,
      color: "#fff",
      font: `700 11px/18px ${THEME.fontFamily}`,
      display: "none",
      boxSizing: "border-box"
    });
    button2.appendChild(badge);
    root2.appendChild(button2);
    return {
      setBadge(text, color) {
        badge.style.display = text ? "block" : "none";
        badge.textContent = text ?? "";
        if (color) badge.style.background = color;
      }
    };
  }

  // src/index.ts
  var VERSION = true ? "2.0.0" : "dev";
  var instance;
  function init(config = {}) {
    if (window.self !== window.top) return void 0;
    if (instance) return instance;
    const adapter = findAdapter(location);
    if (!adapter) {
      if (config.debug) console.debug("[RechercheLogement] site d'annonces non compatible", location.hostname);
      return void 0;
    }
    const store = createStore("recherche-logement");
    const connection = new ConnectionStore(store, { serverUrl: config.serverUrl, token: config.apiToken });
    const initial = connection.get();
    const api = new ApiClient({ baseUrl: initial.serverUrl, token: initial.token });
    const sync = new SyncEngine(store, api, { clientVersion: VERSION });
    connection.onChange((s) => {
      api.configure(s.serverUrl, s.token);
      sync.retryNow();
    });
    window.addEventListener("pagehide", () => void sync.flush());
    const display = new DisplayPrefsStore(store);
    const baseHidden = config.hideStatuses ?? ["rejected"];
    const tracker = new Tracker(adapter, sync, {
      hideStatuses: hiddenStatuses(baseHidden, display.get()),
      hideSuggestedDuplicatesOf: config.hideSuggestedDuplicatesOf ?? [],
      debug: config.debug ?? false
    });
    display.onChange((p) => tracker.setHideStatuses(hiddenStatuses(baseHidden, p)));
    const testConnection = async (s) => {
      const info = await new ApiClient({ baseUrl: normalizeUrl(s.serverUrl), token: s.token.trim(), timeoutMs: 5e3 }).ping();
      return `Connecté (${info.name} v${info.version})`;
    };
    const openPanel = () => showPanel({
      sync,
      connection,
      testConnection,
      version: VERSION,
      pageCounts: () => tracker.pageCounts(),
      showHidden: tracker.showHidden,
      setShowHidden: (v) => tracker.setShowHidden(v),
      display,
      searches: connection.isConfigured() ? {
        canSaveCurrent: adapter.isSearchPage?.(location) ?? false,
        defaultName: searchName(document.title, adapter.label),
        site: adapter.id,
        save: (name) => api.addSearch({ name: name || void 0, url: location.href }),
        list: () => api.searches()
      } : void 0
    });
    const button2 = installFloatingButton({
      id: "tmrl-button",
      label: "🏠",
      title: `Suivi de recherche logement (v${VERSION})`,
      onClick: openPanel,
      offset: config.buttonOffset
    });
    const refreshBadge = () => {
      const c = tracker.pageCounts();
      if (sync.state === "unauthorized" || sync.state === "unconfigured") button2.setBadge("!", THEME.rejected);
      else if (sync.state === "offline") button2.setBadge("⚠", THEME.suggest);
      else if (c.suggested > 0) button2.setBadge(`≈${c.suggested}`, THEME.suggest);
      else button2.setBadge(c.hidden > 0 ? String(c.hidden) : null, THEME.rejected);
    };
    tracker.onCounts(refreshBadge);
    sync.onChange(refreshBadge);
    registerMenuCommand("Ouvrir le panneau (connexion, réglages)", openPanel);
    registerMenuCommand("Afficher / masquer les annonces masquées", () => tracker.setShowHidden(!tracker.showHidden));
    registerMenuCommand("Nouvelles annonces seulement (oui / non)", () => {
      const on = !display.onlyNew;
      display.set({ hideSeen: on, hideToContact: on });
    });
    registerMenuCommand("Ouvrir le serveur local", () => window.open(connection.get().serverUrl, "_blank"));
    tracker.start();
    if (!connection.isConfigured()) setTimeout(() => !isModalOpen() && openPanel(), 800);
    instance = { sync, tracker, connection };
    return instance;
  }
  function searchName(title, siteLabel2) {
    const cleaned = title.replace(/\s*[-–|]\s*(Bien[’']ici|SeLoger)\s*$/i, "").trim();
    return (cleaned || `Recherche ${siteLabel2}`).slice(0, 120);
  }
  return __toCommonJS(src_exports);
})();
if (typeof window !== 'undefined') { window.TMRechercheLogement = TMRechercheLogement; }
//# sourceMappingURL=recherche-logement.js.map
