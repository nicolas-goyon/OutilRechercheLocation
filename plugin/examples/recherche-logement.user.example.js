// ==UserScript==
// @name         Recherche logement — suivi multi-sites
// @namespace    local.recherche-logement
// @version      1.0.0
// @description  Masque / étiquette les annonces immobilières et les synchronise avec le site local.
// @match        https://www.bienici.com/*
// @noframes
// @run-at       document-idle
// @grant        unsafeWindow
// @grant        GM_registerMenuCommand
// @grant        GM_getValue
// @grant        GM_setValue
// @grant        GM_addValueChangeListener
// @grant        GM_removeValueChangeListener
// @grant        GM_xmlhttpRequest
// @connect      localhost
// @connect      127.0.0.1
// @require      https://cdn.jsdelivr.net/gh/nicolas-goyon/OutilRechercheLocation@plugin-vX.Y.Z/plugin/dist/recherche-logement.js
// ==/UserScript==

// Le plus simple : copier ce script tout prêt (token inclus) depuis la page
// Paramètres du site local (http://localhost:5080/parametres).
//
// - UN SEUL script pour tous les sites (ajouter ici le @match des futurs sites).
// - @connect localhost + GM_xmlhttpRequest : permettent d'appeler le serveur local
//   depuis une page https sans blocage CORS / contenu mixte.
// - @require : remplacer plugin-vX.Y.Z par le dernier tag plugin-v*
//   publié par la CI. En développement : @require file:///C:/.../plugin/dist/recherche-logement.js

(function () {
  'use strict';

  window.TMRechercheLogement.init({
    serverUrl: 'http://localhost:5080',
    apiToken: 'COLLER_ICI_LE_TOKEN_DES_PARAMETRES',
    // hideStatuses: ['rejected'],              // statuts masqués (default)
    // hideSuggestedDuplicatesOf: ['rejected'], // masquer aussi les doublons PROBABLES d'annonces écartées
    // buttonOffset: { right: 16, bottom: 16 },
    // debug: false,
  });
})();
