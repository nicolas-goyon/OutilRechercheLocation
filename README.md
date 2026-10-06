# Recherche logement — plugin + serveur local

Outil personnel pour suivre une recherche de logement sur plusieurs sites d'annonces.

Termes utilisés dans ce dépôt :

- **Site d'annonces** : un site externe qui publie des annonces (par exemple Bien'ici).
- **Serveur local** : l'application web de ce dépôt (`server/`). Elle tourne sur votre machine.
- **Plugin** : le script Tampermonkey de ce dépôt (`plugin/`). Il tourne sur les sites d'annonces.
- **Annonce** : une publication sur un site d'annonces.
- **Bien** : un logement réel. Un bien peut avoir plusieurs annonces (doublons).

| Partie | Rôle | Techno |
| --- | --- | --- |
| [`plugin/`](./plugin) | Script Tampermonkey sur les sites d'annonces. Il ajoute des boutons à chaque annonce : 👁 vue, ✕ pas intéressé (masquée), 📞 me plaît / à contacter, note. Il masque des annonces automatiquement. Il affiche des badges de doublons. | TypeScript → bundle esbuild → `@require` jsDelivr |
| [`server/`](./server) | Serveur local. Il stocke toutes les annonces. Il regroupe les doublons en « biens ». Il suit les contacts (un tableau par étape). Il garde l'historique. Il sauvegarde les données. | ASP.NET Core 10 (API + Blazor) + SQLite, Docker |

Le plugin envoie au serveur local les annonces affichées et vos actions (`POST /api/sync`, protégé
par un token). Le serveur local répond avec l'état de chaque annonce.

Si le serveur local est arrêté, le plugin continue de masquer et d'étiqueter les annonces avec son
cache. Il garde les actions en attente. Il les envoie quand le serveur local redémarre.

Sites d'annonces compatibles : **Bien'ici** et **SeLoger** (pages de résultats et page d'une
annonce). Pour ajouter un site d'annonces, lire
[`ARCHITECTURE.md`](./ARCHITECTURE.md).

## Démarrage

### 1. Le serveur local (Docker)

**Image publiée par la CI (recommandé, sans le code source).** Dans un dossier vide :

```bash
curl -LO https://github.com/nicolas-goyon/OutilRechercheLocation/releases/download/server-latest/docker-compose.yml
docker compose up -d
```

Windows (PowerShell) : `Invoke-WebRequest <même URL> -OutFile docker-compose.yml`, puis
`docker compose up -d`. Le fichier est aussi dans le dépôt :
[`deploy/docker-compose.yml`](./deploy/docker-compose.yml).

Ce compose prend l'image `ghcr.io/nicolas-goyon/outilrecherchelocation-server:latest`.

- **Mettre à jour** : `docker compose up -d --pull always`, ou les scripts `update.sh` / `update.ps1`
  (release `server-latest`, ou dossier `deploy/`). Les données sont conservées, la base est migrée au
  démarrage. La page **Paramètres** affiche la version installée.
- **Revenir à une version** : `RL_TAG=0.2.42 docker compose up -d` (tags : `latest`, `<version>`,
  `sha-<commit>`).
- **Autre port** : `RL_PORT=5090 docker compose up -d`.
- **Reprendre les données d'une installation en build local** : `docker volume ls`, puis
  `RL_VOLUME=<nom du volume> docker compose up -d`.

**Build local depuis les sources** (développement), à la racine du dépôt :

```bash
docker compose up -d --build
```

Ensuite :

1. Ouvrir <http://localhost:5080>.
2. Ouvrir la page **Paramètres**. Elle affiche le token du plugin et le lien d'installation.

La commande `docker compose logs server` affiche aussi le token.

Les données sont dans un volume Docker (fichier SQLite) : `recherche-logement-data` avec l'image
publiée. Le port est lié à
`127.0.0.1`. Le réseau n'a donc pas accès au serveur local.

Sans Docker : `cd server && dotnet run --project src/RechercheLogement.Server`. La base est alors
dans `src/RechercheLogement.Server/data/`.

### 2. Le plugin (Tampermonkey, mise à jour automatique)

1. Installer Tampermonkey.
2. Ouvrir
   <https://github.com/nicolas-goyon/OutilRechercheLocation/releases/latest/download/recherche-logement.user.js>.
   La page **Paramètres** du serveur local donne aussi ce lien.
3. Cliquer sur **Installer**.
4. Ouvrir une recherche sur bienici.com ou seloger.com. Au premier lancement, le panneau 🏠 s'ouvre sur
   **Connexion au serveur local**.
5. Coller le token (page **Paramètres** du serveur local).
6. Cliquer sur **Tester**, puis sur **Enregistrer**.

La barre d'actions apparaît alors sur chaque annonce. Le bouton 🏠 (en bas à droite) affiche l'état
de la connexion :

- badge `!` : le token est absent ou le serveur local refuse le token.
- badge `⚠` : le serveur local est arrêté.

**Mise à jour automatique** (mécanisme du template v2) :

1. Le script installé contient un `@updateURL` vers
   `releases/latest/download/recherche-logement.meta.js`.
2. Tampermonkey lit ce fichier à intervalles réguliers. Par défaut, il le lit une fois par jour
   (*Paramètres → Mise à jour des scripts*). Le bouton « Rechercher des mises à jour » du tableau de
   bord force une lecture.
3. Si le `@version` de ce fichier est plus élevé, Tampermonkey télécharge le nouveau script.
4. Le `@require` du nouveau script pointe vers le bundle du même tag sur jsDelivr.

Ne pas modifier le script dans l'éditeur Tampermonkey. La mise à jour suivante écrase les
modifications.

**Le script ne contient jamais le token**, car GitHub publie ce script. Vous saisissez le token dans
le panneau 🏠. Le plugin le garde dans le stockage Tampermonkey. Les mises à jour du script ne
changent pas ce stockage. Le build et la CI refusent un `apiToken` dans `plugin/userscripts/*.mjs`.

**Un seul script pour tous les sites d'annonces.** Pour ajouter un site d'annonces, ajouter une
ligne `@match` dans
[`plugin/userscripts/recherche-logement.mjs`](./plugin/userscripts/recherche-logement.mjs). Les
lignes `@connect localhost` et `@connect 127.0.0.1` autorisent le plugin à contacter le serveur
local.

En développement, `cd plugin && npm run build` produit aussi `dist/recherche-logement.user.js`
(avec la version de `package.json`). Pour tester un bundle local :

1. Remplacer le `@require` du script par `file:///…/plugin/dist/recherche-logement.js`.
2. Activer « Autoriser l'accès aux URL de fichiers » dans l'extension.

## Utilisation

Dans le plugin, sur chaque annonce :

| Bouton | Effet |
| --- | --- |
| 👁 | Vue : l'annonce reste visible, mais atténuée. |
| ✕ | Pas intéressé : l'annonce est masquée. Si le serveur local reconnaît un doublon, ses doublons sont masqués sur tous les sites d'annonces. |
| 📞 | Me plaît, à contacter : contour vert et étiquette d'étape (« À contacter », « Visite prévue »…). |
| ⋯ / 📝 | Note, annonces associées, lien vers la fiche du bien sur le serveur local. |
| 🔗 n | Le même bien a n autres annonces. |
| ≈ déjà vue ? 60 % | Doublon probable à confirmer. Cliquer sur « Même bien » ou « Pas le même ». |

Sur la page d'une annonce, le plugin affiche un bandeau **🏠 Suivi** au-dessus du titre, avec les
mêmes boutons (en clair) et le statut actuel. La page d'une annonce n'est jamais masquée.

Le plugin masque aussi les emplacements de pub des listes. Avec un bloqueur de pub, ces blocs
restent vides et laissent des trous (liste à côté de la carte sur Bien'ici, liste SeLoger).

Panneau 🏠, section **Affichage** : « Masquer les annonces vues 👁 » et « Masquer les annonces à
contacter 📞 ». Les deux cases cochées (ou le bouton « Nouvelles annonces seulement », aussi dans le
menu Tampermonkey) : seules les annonces jamais classées restent visibles. Le réglage vaut pour tous
les sites et tous les onglets. Les annonces « pas intéressé » restent toujours masquées.

Panneau 🏠, section **⭐ Recherches favorites** : sur une page de résultats, le bouton
« Enregistrer cette recherche » envoie l'URL (avec tous les critères) au serveur local. La section
liste aussi les recherches déjà enregistrées pour ce site.

Pages du serveur local :

- **Tableau de bord** : compteurs, biens à contacter, biens vus récemment.
- **📞 À contacter** : un tableau par étape. Les étapes sont : à contacter → agence contactée →
  visite prévue → visitée → dossier envoyé → dossier accepté / refusé. Les flèches font passer un
  bien à l'étape précédente ou suivante.
- **Tous les biens** : filtres (statut, texte, code postal, prix max, surface min) et tri.
- **Fiche d'un bien** : toutes ses annonces (tous sites d'annonces), historique de prix, note,
  commentaires datés (appels, visites…). Un bouton dissocie une annonce liée par erreur.
- **Doublons à vérifier** : comparaison côte à côte.
- **⭐ Recherches** : liens de recherche favoris (Bien'ici, SeLoger…), avec vos critères dans l'URL.
  Plusieurs recherches par site, même URL possible (doublons permis). Ajouter, modifier, dupliquer
  (pour une variante avec d'autres critères), réordonner, ouvrir une ou toutes les recherches d'un
  site. Le tableau de bord affiche des raccourcis.
- **🔎 Mes projets** : recherches complètes que le serveur lance lui-même (voir ci-dessous).
- **Paramètres** : token (copier / régénérer), lien d'installation du plugin, export JSON.

### Mes projets

Un projet regroupe des critères et des sites. Le serveur interroge les sites, garde les annonces
conformes et signale les nouvelles depuis la dernière visite. Ces données sont **séparées** du suivi
fait avec le plugin : autre base (`projets.db`, à côté de la base principale), autres pages.

- Critères : location / achat, appartement / maison, lieux (une ville ou un code postal par ligne),
  prix, surface, pièces, chambres, meublé, mots-clés souhaités (au moins un) et exclus.
- Sites, par catégorie, activables un par un :
  - *Sites d'annonces immobilières* : **Bien'ici**, **SeLoger** (PAP, Logic-Immo : à venir).
  - *Sites d'annonces généralistes* : Leboncoin (à venir).
  - *Sites d'agences* : à venir.
- Lancement : à la demande (« ▶ Lancer ») ou automatique (toutes les 1, 3, 6, 12 ou 24 h). Une
  recherche à la fois, avec une pause entre les pages. Au plus 300 annonces par site et par passage
  (les plus récentes).
- Page d'un projet : état de chaque site (nombre d'annonces, erreur lisible), lieux reconnus,
  annonces avec badge « Nouvelle », filtres (nouvelles seulement, par site, tri), bouton « Masquer ».
- `RechercheLogement__ProjectsAutoRun=false` coupe les lancements automatiques.
  `RechercheLogement__ProjectsDatabasePath` change l'emplacement de `projets.db`.

## Développement

```bash
# Plugin
cd plugin && npm install && npm test && npm run build      # -> plugin/dist/recherche-logement.js + .user.js / .meta.js

# Serveur local
cd server && dotnet test                                    # tests xUnit (métier + API)
dotnet run --project src/RechercheLogement.Server           # http://localhost:5080
```

## CI/CD (GitHub Actions)

- [`plugin.yml`](./.github/workflows/plugin.yml) lance les tests et le build. Sur `main`, il publie :
  - le bundle sur un tag `plugin-vX.Y.Z`, que jsDelivr sert.
  - une GitHub Release avec `recherche-logement.user.js` et `.meta.js`.

  Cette Release déclenche la mise à jour automatique des plugins installés. Pour monter la version,
  écrire `[minor]` ou `[major]` dans un message de commit. Seul ce workflow crée des GitHub
  Releases. La Release « latest » est donc toujours la dernière version du plugin.
- [`server.yml`](./.github/workflows/server.yml) lance le build et les tests .NET, et valide
  `deploy/docker-compose.yml`. Sur `main`, il publie :
  - l'image `ghcr.io/nicolas-goyon/outilrecherchelocation-server` (tags `latest`, `0.2.<n° de run>`,
    `sha-<commit>`).
  - `docker-compose.yml`, `update.sh` et `update.ps1` en artefact du run (`server-deploy`) et sur la
    release `server-latest` (adresse fixe). Cette release est une *pre-release* : elle ne devient
    jamais « latest », qui reste réservé au plugin.

  Au premier envoi, GitHub crée le paquet GHCR en **privé**. Le rendre public (GitHub > Packages >
  outilrecherchelocation-server > Package settings > Change visibility), sinon `docker login ghcr.io`
  est nécessaire avant `docker compose up`.

## Limites connues

- Bien'ici : le plugin relit le JSON de la liste. Cela fait une requête de plus par page. Les deux
  annonces « mises en avant » n'ont souvent que les données de la carte.
- Mes projets, SeLoger : SeLoger protège son site contre les robots. Un appel venant du serveur
  (et pas d'un navigateur) peut être refusé : la page du projet affiche alors « SeLoger refuse la
  requête… ». Bien'ici n'a pas cette protection.
- SeLoger : le JSON de la liste ne contient ni position GPS ni photos d'origine (les photos sont
  renommées). La position GPS et les charges viennent de la page de l'annonce, une fois ouverte.
- Le serveur local n'a pas de connexion utilisateur. Il doit tourner sur votre machine, lié à
  `127.0.0.1`.
