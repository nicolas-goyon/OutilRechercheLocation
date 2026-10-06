// Devient dist/recherche-logement.user.js (+ .meta.js pour la vérification des
// mises à jour), publié en asset de la GitHub Release à chaque version du plugin.
// Le build ajoute @version, le @require du bundle, @updateURL et @downloadURL.
//
// Ne jamais changer name + namespace : Tampermonkey s'en sert pour reconnaître
// le script lors des mises à jour.
//
// UN SEUL script pour tous les sites : ajouter ici le @match de chaque nouveau
// site (la file d'attente et le cache du plugin sont propres au script).

/** @type {import('./types').UserscriptInstance} */
export default {
  headers: {
    name: 'Recherche logement — suivi multi-sites',
    namespace: 'local.recherche-logement',
    description: 'Masque / étiquette les annonces immobilières et les synchronise avec le site local.',
    match: ['https://www.bienici.com/*'],
    grant: [
      'unsafeWindow',
      'GM_registerMenuCommand',
      'GM_getValue',
      'GM_setValue',
      'GM_addValueChangeListener',
      'GM_removeValueChangeListener',
      // Appels au site local depuis une page https, sans blocage CORS / contenu mixte.
      'GM_xmlhttpRequest',
    ],
    connect: ['localhost', '127.0.0.1'],
  },

  // Passé tel quel à window.TMRechercheLogement.init(...) — voir InitConfig dans
  // src/index.ts. JSON uniquement, et JAMAIS de token (le fichier est public) :
  // le token se saisit dans le plugin, bouton 🏠 > Connexion.
  config: {
    serverUrl: 'http://localhost:5080',
    // hideStatuses: ['rejected'],
    // hideSuggestedDuplicatesOf: ['rejected'],
    // buttonOffset: { right: 16, bottom: 16 },
  },
};
