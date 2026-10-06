// ==UserScript==
// @name          Recherche logement — suivi multi-sites
// @namespace     local.recherche-logement
// @version       0.1.2
// @description   Masque / étiquette les annonces immobilières et les synchronise avec le site local.
// @homepageURL   https://github.com/nicolas-goyon/OutilRechercheLocation
// @match         https://www.bienici.com/*
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
// @require       https://cdn.jsdelivr.net/gh/nicolas-goyon/OutilRechercheLocation@plugin-v0.1.2/plugin/dist/recherche-logement.js
// @updateURL     https://github.com/nicolas-goyon/OutilRechercheLocation/releases/latest/download/recherche-logement.meta.js
// @downloadURL   https://github.com/nicolas-goyon/OutilRechercheLocation/releases/latest/download/recherche-logement.user.js
// ==/UserScript==

// GÉNÉRÉ par plugin/scripts/build.mjs depuis plugin/userscripts/recherche-logement.mjs — ne pas modifier.
// Tampermonkey écrase ce script à chaque mise à jour automatique : modifier
// le fichier d'instance dans le dépôt et publier une nouvelle version.
// Le token du site local ne figure pas ici : il se saisit dans le plugin
// (bouton 🏠 > Connexion) et reste dans le stockage Tampermonkey.

(function () {
  'use strict';

  window.TMRechercheLogement.init({
    "serverUrl": "http://localhost:5080"
  });
})();
