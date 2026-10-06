// ==UserScript==
// @name          Recherche logement — suivi multi-sites
// @namespace     local.recherche-logement
// @version       2.0.0
// @description   Masque et étiquette les annonces immobilières. Synchronise les annonces avec le serveur local.
// @homepageURL   https://github.com/nicolas-goyon/OutilRechercheLocation
// @match         https://www.bienici.com/*
// @match         https://www.seloger.com/*
// @noframes
// @run-at        document-idle
// @grant         unsafeWindow
// @grant         GM_registerMenuCommand
// @grant         GM_getValue
// @grant         GM_setValue
// @grant         GM_addValueChangeListener
// @grant         GM_removeValueChangeListener
// @grant         GM_xmlhttpRequest
// @connect       localhost
// @connect       127.0.0.1
// @require       https://cdn.jsdelivr.net/gh/nicolas-goyon/OutilRechercheLocation@plugin-v2.0.0/plugin/dist/recherche-logement.js
// @updateURL     https://github.com/nicolas-goyon/OutilRechercheLocation/releases/latest/download/recherche-logement.meta.js
// @downloadURL   https://github.com/nicolas-goyon/OutilRechercheLocation/releases/latest/download/recherche-logement.user.js
// ==/UserScript==
