// Le build transforme ce fichier en dist/recherche-logement.user.js. Il produit
// aussi .meta.js, que Tampermonkey lit pour trouver les mises à jour. La CI
// publie ces deux fichiers dans la GitHub Release de chaque version du plugin.
// Le build ajoute @version, le @require du bundle, @updateURL et @downloadURL.
//
// Ne jamais changer name + namespace. Tampermonkey utilise ces deux valeurs
// pour reconnaître le script pendant une mise à jour.
//
// UN SEUL script pour tous les sites d'annonces. Ajouter ici le @match de
// chaque nouveau site. La file d'attente et le cache du plugin appartiennent
// au script, donc tous les sites les partagent.

/** @type {import('./types').UserscriptInstance} */
export default {
  headers: {
    name: 'Recherche logement — suivi multi-sites',
    namespace: 'local.recherche-logement',
    description: 'Masque et étiquette les annonces immobilières. Synchronise les annonces avec le serveur local.',
    match: ['https://www.bienici.com/*'],
    grant: [
      'unsafeWindow',
      'GM_registerMenuCommand',
      'GM_getValue',
      'GM_setValue',
      'GM_addValueChangeListener',
      'GM_removeValueChangeListener',
      // Appels au serveur local depuis une page https. Le CORS et le contenu mixte ne bloquent pas ces appels.
      'GM_xmlhttpRequest',
    ],
    connect: ['localhost', '127.0.0.1'],
  },

  // Le script transmet cet objet sans modification à
  // window.TMRechercheLogement.init(...). Voir InitConfig dans src/index.ts.
  // JSON uniquement. JAMAIS de token, car ce fichier est public. L'utilisateur
  // saisit le token dans le plugin (bouton 🏠 > Connexion).
  config: {
    serverUrl: 'http://localhost:5080',
    // hideStatuses: ['rejected'],
    // hideSuggestedDuplicatesOf: ['rejected'],
    // buttonOffset: { right: 16, bottom: 16 },
  },
};
