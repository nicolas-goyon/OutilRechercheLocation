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
    instance: () => instance
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

  // src/sites/bienici/parse.ts
  var CARD_SELECTOR = "article.ad-overview[data-id]";
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
      descriptionExcerpt: description ? normalizeText(description).slice(0, 600) : void 0
    };
    return data;
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
      agencyName: ad.accountDisplayName || void 0,
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

  // src/sites/bienici/adapter.ts
  var API_RE = /\/realEstateAds\.json\?/;
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
          for (const ad of [...json.realEstateAds ?? [], ...json.leadingAds ?? []]) {
            if (ad?.id) onData(ad.id, parseBieniciApiAd(ad));
          }
        } catch (error) {
          console.warn("[RechercheLogement] enrichissement Bien'ici impossible", error);
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

  // src/sites/index.ts
  var ADAPTERS = [bieniciAdapter];
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
    none: "Non qualifi\xE9e",
    seen: "Vue",
    rejected: "Pas int\xE9ress\xE9 (masqu\xE9e)",
    toContact: "Me pla\xEEt \u2014 \xE0 contacter"
  };
  var CONTACT_STAGE_LABEL = {
    pending: "\xC0 contacter",
    contacted: "Agence contact\xE9e",
    visitScheduled: "Visite pr\xE9vue",
    visited: "Visit\xE9e",
    applicationSent: "Dossier envoy\xE9",
    accepted: "Dossier accept\xE9",
    declined: "Refus\xE9"
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
  function renderToolbar(anchor, model, handlers) {
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
        statusButton("\u{1F441}", "Vue (garder visible, att\xE9nu\xE9e)", "seen", model.status, THEME.seen, handlers),
        statusButton("\u2715", "Vue, pas int\xE9ress\xE9 : masquer", "rejected", model.status, THEME.rejected, handlers),
        statusButton("\u{1F4DE}", "Me pla\xEEt : \xE0 contacter", "toContact", model.status, THEME.toContact, handlers),
        pill(model.hasNote ? "\u{1F4DD}" : "\u22EF", model.hasNote ? "Note et d\xE9tails" : "D\xE9tails / note", THEME.bg, handlers.onDetails),
        model.siblings > 0 && pill(`\u{1F517} ${model.siblings}`, model.siblingsTitle, THEME.accent, handlers.onDetails),
        model.suggestion && pill(
          `\u2248 ${model.suggestion.danger ? "d\xE9j\xE0 \xE9cart\xE9e" : "d\xE9j\xE0 vue"} ? ${Math.round(model.suggestion.score * 100)} %`,
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
          pill(`\u{1F4DE} ${CONTACT_STAGE_LABEL[model.contactStage ?? "pending"]}`, "Suivi du contact (modifiable sur le site local)", THEME.toContact, handlers.onDetails)
        )
      );
    }
    host.shadowRoot.replaceChildren(...children);
  }
  function statusButton(icon, title, status, current, color, handlers) {
    const active = current === status;
    return h(
      "button",
      {
        type: "button",
        title: active ? `${title} \u2014 cliquer pour annuler` : title,
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
  function installPageStyles() {
    if (document.getElementById(STYLE_ID)) return;
    const style = document.createElement("style");
    style.id = STYLE_ID;
    style.textContent = `
[data-tmrl-card] { position: relative !important; }
[data-tmrl-hidden] { display: none !important; }
html[data-tmrl-show-hidden] [data-tmrl-hidden] {
  display: block !important; opacity: .4; outline: 2px dashed #dc2626; outline-offset: 2px;
}
[data-tmrl-status="seen"] { opacity: .55; transition: opacity .15s; }
[data-tmrl-status="seen"]:hover { opacity: 1; }
[data-tmrl-status="toContact"] { outline: 4px solid #16a34a; outline-offset: 2px; border-radius: 6px; box-shadow: 0 0 0 8px rgba(22,163,74,.18); }
[data-tmrl-dup="rejected"]:not([data-tmrl-status]) { outline: 3px dashed #d97706; outline-offset: 2px; }
`;
    (document.head ?? document.documentElement).appendChild(style);
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
    closeButton.textContent = "\u2715";
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
  var fmtPrice = (n) => n === void 0 ? "" : `${n.toLocaleString("fr-FR")} \u20AC`;
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
    const label = `${siteLabel(ref.site)} \xAB ${ref.title ?? ref.key} \xBB ${fmtPrice(ref.price)}`;
    return ref.url ? h("a", { href: ref.url, target: "_blank", rel: "noopener", style: { color: "#93c5fd" } }, `${label} \u2197`) : h("span", null, label);
  }
  function webLink(url, label = "Ouvrir sur le site local \u2197") {
    return url ? h("a", { href: url, target: "_blank", rel: "noopener", style: { color: "#93c5fd" } }, label) : null;
  }
  function statusBadge(status) {
    const color = { none: THEME.bgSoft, seen: THEME.seen, rejected: THEME.rejected, toContact: THEME.toContact }[status];
    return h("span", { style: { background: color, borderRadius: "4px", padding: "2px 6px", font: `600 11px/1.4 ${THEME.fontFamily}` } }, STATUS_LABEL[status]);
  }
  function showSuggestion(sync, key, s) {
    showModal({
      title: `M\xEAme bien ? (score ${Math.round(s.score * 100)} %)`,
      content: h(
        "div",
        null,
        section("Autre annonce", h("div", null, link(s.other)), h("div", { style: { marginTop: "6px" } }, "Statut : ", statusBadge(s.otherStatus))),
        section("Pourquoi", h("ul", { style: { margin: "0", paddingLeft: "18px" } }, s.reasons.map((r) => h("li", null, r)))),
        h(
          "div",
          { style: { display: "flex", gap: "8px", justifyContent: "space-between", alignItems: "center", flexWrap: "wrap" } },
          webLink(sync.view(key)?.webUrl, "Comparer en d\xE9tail sur le site local \u2197"),
          h(
            "div",
            { style: { display: "flex", gap: "8px" } },
            button("Pas le m\xEAme bien", () => {
              sync.act({ type: "dismissDuplicate", key, otherKey: s.other.key });
              closeModal();
            }),
            button("\u2714 M\xEAme bien", () => {
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
      placeholder: "Note personnelle (contact, visite, impressions...)",
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
          status === "toContact" && h("div", { style: { marginTop: "8px" } }, `Suivi : ${CONTACT_STAGE_LABEL[v?.contactStage ?? "pending"]} (\xE0 mettre \xE0 jour sur le site local)`)
        ),
        section("Note", note, h("div", { style: { marginTop: "6px", textAlign: "right" } }, button("Enregistrer la note", () => {
          sync.act({ type: "setNote", key, note: note.value.trim() });
          closeModal();
        }, THEME.ok))),
        section(
          `M\xEAme bien sur d'autres annonces (${v?.siblings.length ?? 0})`,
          !v?.siblings.length ? h("div", { style: { color: THEME.muted } }, "Aucune annonce associ\xE9e.") : h("ul", { style: { margin: "0", paddingLeft: "18px" } }, v.siblings.map((s) => h("li", { style: { marginBottom: "4px" } }, link(s)))),
          !!v?.siblings.length && h("div", { style: { marginTop: "6px" } }, button("Dissocier cette annonce du bien", () => {
            sync.act({ type: "detach", key });
            closeModal();
          }))
        ),
        !!v?.suggestions.length && section(
          `Doublons possibles (${v.suggestions.length})`,
          h("ul", { style: { margin: "0", paddingLeft: "18px" } }, v.suggestions.map(
            (s) => h(
              "li",
              { style: { marginBottom: "4px" } },
              link(s.other),
              ` \u2014 ${Math.round(s.score * 100)} % `,
              statusBadge(s.otherStatus),
              " ",
              button("D\xE9cider", () => showSuggestion(sync, key, s))
            )
          ))
        ),
        h("div", { style: { display: "flex", justifyContent: "space-between", color: THEME.muted, fontSize: "11px" } }, `Cl\xE9 : ${key}`, webLink(v?.webUrl) ?? "")
      )
    });
  }
  function showPanel(ctx) {
    const { sync } = ctx;
    const page = ctx.pageCounts();
    const toggle = h("input", { type: "checkbox", checked: ctx.showHidden, on: { change: () => ctx.setShowHidden(toggle.checked) } });
    const stateText = {
      online: ["\u{1F7E2}", "Connect\xE9 au serveur local"],
      offline: ["\u{1F7E0}", "Serveur local injoignable \u2014 les actions sont gard\xE9es et seront envoy\xE9es \xE0 son retour"],
      unauthorized: ["\u{1F534}", "Token refus\xE9 \u2014 v\xE9rifier apiToken dans le script Tampermonkey (Param\xE8tres du site local)"],
      unknown: ["\u26AA", "Connexion en cours..."]
    }[sync.state];
    showModal({
      title: "\u{1F3E0} Suivi de recherche logement",
      content: h(
        "div",
        null,
        section(
          "Serveur",
          h("div", null, `${stateText[0]} ${stateText[1]}`),
          sync.lastError && sync.state !== "online" && h("div", { style: { color: THEME.muted, fontSize: "11px" } }, sync.lastError),
          h("div", { style: { marginTop: "4px" } }, `${sync.pendingCount()} \xE9l\xE9ment(s) en attente d'envoi`),
          h(
            "div",
            { style: { display: "flex", gap: "8px", marginTop: "8px", alignItems: "center" } },
            button("R\xE9essayer maintenant", () => {
              sync.retryNow();
              setTimeout(() => showPanel(ctx), 1500);
            }),
            webLink(ctx.serverUrl, "Ouvrir le site local \u2197")
          )
        ),
        section(
          "Cette page",
          h("div", null, `${page.total} annonces \xB7 ${page.hidden} masqu\xE9es \xB7 ${page.seen} vues \xB7 ${page.toContact} \xE0 contacter \xB7 ${page.suggested} doublons possibles`),
          h("label", { style: { display: "flex", gap: "8px", alignItems: "center", marginTop: "8px", cursor: "pointer" } }, toggle, "Afficher les annonces masqu\xE9es (en pointill\xE9s)")
        )
      )
    });
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
      installPageStyles();
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
          this.sync.refresh(this.adapter.findCards(document).map((c) => listingKey(this.adapter.id, c.siteId)));
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
      this.counts = counts;
      for (const cb of this.countListeners) cb(counts);
      if (this.options.debug) console.debug("[RechercheLogement] scan", counts);
    }
    /** Envoie la carte au serveur une fois par élément/annonce (les SPA réutilisent les éléments). */
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
    apply(card) {
      const key = listingKey(this.adapter.id, card.siteId);
      const el = card.element;
      const view = this.sync.view(key);
      const status = view?.status ?? "none";
      const best = view?.suggestions[0];
      let hidden = this.options.hideStatuses.includes(status);
      if (!hidden && status === "none" && best && this.options.hideSuggestedDuplicatesOf.includes(best.otherStatus)) hidden = true;
      setAttr(el, "data-tmrl-status", status === "none" ? null : status);
      setAttr(el, "data-tmrl-hidden", hidden ? "" : null);
      setAttr(el, "data-tmrl-dup", best ? best.otherStatus === "rejected" ? "rejected" : "suggested" : null);
      renderToolbar(
        this.adapter.toolbarAnchor?.(card) ?? el,
        {
          status,
          contactStage: view?.contactStage,
          hasNote: !!view?.note,
          known: !!view,
          siblings: view?.siblings.length ?? 0,
          siblingsTitle: `M\xEAme bien que : ${(view?.siblings ?? []).map((s) => `${siteLabel(s.site)} \xAB ${s.title ?? s.key} \xBB`).join(", ")}`,
          suggestion: best && {
            score: best.score,
            danger: best.otherStatus === "rejected",
            label: `Probablement la m\xEAme annonce que ${siteLabel(best.other.site)} \xAB ${best.other.title ?? best.other.key} \xBB \u2014 ${best.reasons.join(", ")}. Cliquer pour d\xE9cider.`
          }
        },
        {
          onStatus: (s) => this.sync.act({ type: "setStatus", key, status: s }),
          onDetails: () => showDetails(this.sync, key),
          onSuggestion: () => best && showSuggestion(this.sync, key, best)
        }
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
      this.options = options;
      this.baseUrl = options.baseUrl.replace(/\/+$/, "");
    }
    sync(request) {
      return this.request("POST", "/api/sync", request);
    }
    ping() {
      return this.request("GET", "/api/ping");
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
        if (status === 401) throw new ApiError("Token refus\xE9 par le serveur", 401);
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
            onerror: () => reject(new ApiError("Serveur injoignable", 0)),
            ontimeout: () => reject(new ApiError("D\xE9lai d\xE9pass\xE9", 0))
          });
        });
      }
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), timeout);
      return fetch(url, { method, headers, body: data, signal: controller.signal }).then(async (r) => parse(r.status, await r.text())).catch((e) => {
        throw e instanceof ApiError ? e : new ApiError("Serveur injoignable", 0);
      }).finally(() => clearTimeout(timer));
    }
  };

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
      this.pendingObs[key] = prev && prev.source === "api" && obs.source === "card" ? { ...prev, data: { ...obs.data, ...prev.data }, seenAt: obs.seenAt } : prev ? { ...obs, data: { ...prev.data, ...obs.data } } : obs;
      this.store.set(OBS_KEY, this.pendingObs);
      this.schedule();
    }
    /** Demande l'état à jour de ces annonces au prochain envoi. */
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
    /** Force un envoi immédiat (bouton "Réessayer"). */
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
    /** Applique localement l'effet visible d'une action, en attendant la réponse du serveur. */
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
  var VERSION = "0.2.0";
  var instance;
  function init(config) {
    if (window.self !== window.top) return void 0;
    if (instance) return instance;
    const adapter = findAdapter(location);
    if (!adapter) {
      if (config.debug) console.debug("[RechercheLogement] site non support\xE9", location.hostname);
      return void 0;
    }
    if (!config.apiToken) {
      console.warn("[RechercheLogement] apiToken manquant : copier le snippet depuis Param\xE8tres du site local.");
    }
    const serverUrl = (config.serverUrl ?? "http://localhost:5080").replace(/\/+$/, "");
    const api = new ApiClient({ baseUrl: serverUrl, token: config.apiToken ?? "" });
    const sync = new SyncEngine(createStore("recherche-logement"), api, { clientVersion: VERSION });
    window.addEventListener("pagehide", () => void sync.flush());
    const tracker = new Tracker(adapter, sync, {
      hideStatuses: config.hideStatuses ?? ["rejected"],
      hideSuggestedDuplicatesOf: config.hideSuggestedDuplicatesOf ?? [],
      debug: config.debug ?? false
    });
    const openPanel = () => showPanel({
      sync,
      serverUrl,
      pageCounts: () => tracker.pageCounts(),
      showHidden: tracker.showHidden,
      setShowHidden: (v) => tracker.setShowHidden(v)
    });
    const button2 = installFloatingButton({
      id: "tmrl-button",
      label: "\u{1F3E0}",
      title: "Suivi de recherche logement",
      onClick: openPanel,
      offset: config.buttonOffset
    });
    const refreshBadge = () => {
      const c = tracker.pageCounts();
      if (sync.state === "unauthorized") button2.setBadge("!", THEME.rejected);
      else if (sync.state === "offline") button2.setBadge("\u26A0", THEME.suggest);
      else if (c.suggested > 0) button2.setBadge(`\u2248${c.suggested}`, THEME.suggest);
      else button2.setBadge(c.hidden > 0 ? String(c.hidden) : null, THEME.rejected);
    };
    tracker.onCounts(refreshBadge);
    sync.onChange(refreshBadge);
    registerMenuCommand("Ouvrir le panneau", openPanel);
    registerMenuCommand("Afficher / cacher les annonces masqu\xE9es", () => tracker.setShowHidden(!tracker.showHidden));
    registerMenuCommand("Ouvrir le site local", () => window.open(serverUrl, "_blank"));
    tracker.start();
    instance = { sync, tracker };
    return instance;
  }
  return __toCommonJS(src_exports);
})();
if (typeof window !== 'undefined') { window.TMRechercheLogement = TMRechercheLogement; }
//# sourceMappingURL=recherche-logement.js.map
