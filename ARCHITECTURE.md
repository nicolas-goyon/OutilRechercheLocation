# Architecture

```
 Navigateur (bienici.com, ... )                         Machine locale (Docker)
┌──────────────────────────────────┐   POST /api/sync   ┌───────────────────────────────────┐
│ plugin/ (Tampermonkey)           │  Bearer <token>    │ server/ (ASP.NET Core 10)          │
│  adaptateur de site -> cartes    │ ─────────────────▶ │  ApiTokenMiddleware                │
│  SyncEngine : file + cache       │ ◀───────────────── │  Catalog (métier, en mémoire)      │
│  barre d'actions, masquage       │   ListingView      │   ├ DedupScorer (doublons)         │
└──────────────────────────────────┘                    │   └ IPersistence -> SQLite (/data) │
                                                        │  Pages Blazor (tableau de bord...) │
                                                        └───────────────────────────────────┘
```

Termes : voir le glossaire au début de [`README.md`](./README.md). Dans ce document, « serveur »
signifie toujours le serveur local, et « site » signifie toujours un site d'annonces.

## Principes

- **Le serveur est la source de vérité.** Il garde les annonces, les biens, les doublons, les
  statuts et l'historique.
- **Le plugin reste léger et autonome.** Il observe, il envoie, il affiche. Hors ligne, il garde
  une file d'actions et un cache d'états (stockage Tampermonkey). Il continue ainsi de masquer les
  annonces.
- **La décision s'applique au bien, pas à l'annonce.** Exemple : « pas intéressé » sur Bien'ici
  masque aussi l'annonce SeLoger que le serveur reconnaît comme le même bien.
- **Code générique au centre, code spécifique aux bords.** Seul `plugin/src/sites/<site>/` connaît
  un site.

## Arborescence

```
plugin/
  userscripts/            Script installable. Un fichier par instance (template v2).
    recherche-logement.mjs  { headers, config } -> dist/recherche-logement.user.js + .meta.js
    types.d.ts              Type UserscriptInstance (refuse apiToken dans config)
  scripts/build.mjs       Bundle esbuild. Génère les userscripts : @version, @require épinglé
                          sur plugin-vX.Y.Z, @updateURL/@downloadURL sur releases/latest.
  src/index.ts            init({ serverUrl }) : adaptateur + SyncEngine + Tracker + UI. VERSION.
  src/core/connection.ts  URL et token du serveur. Saisis dans le panneau, stockés dans Tampermonkey.
  src/core/types.ts       Types et contrat d'API (copie de server/.../Contracts)
  src/core/api.ts         Client HTTP (GM_xmlhttpRequest, en-tête Authorization)
  src/core/sync.ts        SyncEngine : file d'observations et d'actions, envoi par lots,
                          cache des ListingView, application optimiste, backoff hors ligne
  src/core/storage.ts     Wrapper GM_getValue / GM_setValue
  src/sites/              Adaptateurs : bienici/, seloger/ (cartes, page d'une annonce, JSON du site,
                          emplacements de pub vides à masquer)
  src/app/tracker.ts      Cartes de la page -> observations. État -> attributs data-tmrl-*.
  src/ui/                 Barre par carte, modales, bouton 🏠, CSS injecté
  tests/                  node:test (lecture de fixtures réelles, SyncEngine hors ligne)

server/
  src/RechercheLogement.Core/      Métier pur, sans dépendance NuGet
    Model/                Listing, Property, DuplicateLink, PropertyEvent, statuts, libellés
    Dedup/DedupScorer.cs  Score de doublon avec ses raisons
    Contracts/            DTO de /api/sync
    Services/Catalog.cs   Toutes les opérations (sync, statuts, fusion, requêtes des pages)
    Services/Persistence.cs  IPersistence + ChangeSet + InMemoryPersistence
  src/RechercheLogement.Server/    Hôte web
    Program.cs, ServerSettings.cs
    Api/                  /api/ping, /api/sync, /api/listings/{key}, /api/export + token
    Storage/SqlitePersistence.cs   Schéma, migrations (PRAGMA user_version), écriture en transaction
    Components/           Pages Blazor (interactive server) : tableau de bord, à contacter,
                          biens, fiche, doublons, paramètres
  tests/RechercheLogement.Tests/   xUnit : DedupScorer, Catalog, API (WebApplicationFactory)
  Dockerfile
docker-compose.yml        Build local depuis les sources (développement)
deploy/                   docker-compose.yml (image GHCR :latest) + update.sh / update.ps1
.github/workflows/        plugin.yml (tag plugin-v* + jsDelivr + GitHub Release = mise à jour auto)
                          server.yml (tests + image GHCR + docker-compose sur la pre-release server-latest)
```

## Modèle de données

| Entité | Rôle | Champs clés |
| --- | --- | --- |
| **Listing** | Une annonce sur un site. Clé `site:siteId`. | `PropertyId`, `Data` (prix, surface, pièces, CP, GPS, réf. agence, `OtherRefs`, agence + `AgencySiren`, `PhotoKeys`, extrait de description…), `FirstSeenAt`, `LastSeenAt`, `PriceHistory`, `Sources` (`card`/`api`) |
| **Property** | Le bien réel | `Status` (`None`, `Seen`, `Rejected`, `ToContact`), `ContactStage` (`Pending` → `Contacted` → `VisitScheduled` → `Visited` → `ApplicationSent` → `Accepted`/`Declined`), `Note`, `StatusChangedAt` |
| **DuplicateLink** | Paire candidate de doublons | `Score`, `Reasons`, `State` (`Suggested`, `Confirmed`, `Dismissed`), `DecidedBy` (`auto`/`user`) |
| **SavedSearch** | Recherche favorite : lien de recherche d'un site d'annonces (critères dans l'URL). Doublons permis. | `Name`, `Url`, `Site` (déduit de l'URL), `Note`, `Order`, `LastOpenedAt` |
| **PropertyEvent** | Historique d'un bien | changement de statut ou d'étape, note, fusion, dissociation, commentaire |

Règles :

- Chaque annonce a exactement un bien. Une nouvelle annonce crée un bien de statut `None`.
- Confirmer un doublon fusionne les deux biens. Le serveur garde **le bien le plus ancien** (son URL
  et son historique ne changent pas). Le serveur garde le statut de la décision la plus récente.
- Dissocier une annonce la place dans un nouveau bien. Le serveur marque ses liens `Dismissed` et ne
  les propose plus jamais.
- Le serveur ignore un statut du plugin si l'horodatage du statut est **antérieur** au dernier
  changement fait sur le serveur. Ce cas arrive quand une action reste en file pendant une coupure.
- Les données d'une carte HTML ne remplacent pas les données du JSON du site, qui sont plus
  précises.

## Synchronisation (`POST /api/sync`)

Détail dans [`docs/api.md`](./docs/api.md). Le plugin envoie un seul appel par lot. Cet appel
contient :

- `observations` : les annonces vues (données de la carte ou de l'API du site).
- `actions` : `setStatus`, `setNote`, `confirmDuplicate`, `dismissDuplicate`, `detach`. Chaque
  action a un `id` unique et un horodatage `at`.

La réponse contient :

- `appliedActionIds` et `rejectedActionIds`.
- Une `ListingView` par annonce concernée (statut, étape, note, annonces sœurs, suggestions, lien
  vers la fiche).

Les actions sont **idempotentes**. Le serveur garde leurs `id` pendant 90 jours. Si le plugin
renvoie une action après une coupure réseau, le serveur ne l'applique pas une deuxième fois.

## Sécurité

- Le script du plugin est public (GitHub Release). Il ne contient donc jamais le token. Vous saisissez
  le token dans le panneau 🏠. Tampermonkey le stocke hors du script (`GM_setValue`).
- Toutes les routes `/api/*` exigent `Authorization: Bearer <token>`. Le serveur compare le token en
  temps constant.
- Origine du token :
  1. `RechercheLogement__ApiToken`, si cette variable est définie.
  2. Sinon, le serveur génère un token au premier démarrage (32 octets aléatoires) et le stocke en
     base.

  La page Paramètres permet de régénérer le token.
- Le plugin appelle le serveur avec `GM_xmlhttpRequest` (`@connect localhost`). Le serveur n'ouvre
  donc pas d'en-têtes CORS. Aucune autre page web ne peut lire l'API depuis le navigateur.
- Les pages web du serveur n'ont pas d'authentification. Docker lie donc le serveur à `127.0.0.1`
  uniquement.

## Détection de doublons (`DedupScorer`)

1. **Blocage** : le serveur compare uniquement les annonces du même code postal (75116 ≡ 75016).
2. **Rejets durs**. Chacune de ces différences exclut le même bien :
   - transaction différente ou type de bien différent.
   - codes postaux différents (sauf si les points GPS sont à moins de 300 m).
   - distance supérieure à 1,5 km (plus la marge de précision du GPS).
   - écart de surface supérieur à 10 %.
   - écart de 2 pièces ou plus.
   - SIREN d'agence différent (mentions légales RCS / SIRET : agences différentes).
3. **Points**. Score = points / 10, avec un maximum de 1.

   | Critère | Points |
   | --- | --- |
   | Même réf. agence | +6 (réf. compatible : +4). Toutes les références comptent : champ dédié du site, et « Réf. : … » / « Mandat n° … » trouvés dans la description ou un encadré |
   | Même agence | +1 (même SIREN, ou un mot commun dans le nom, hors mots génériques et ville) |
   | Agences différentes (noms) | −3 |
   | Même agence, références différentes | −3 |
   | Photos d'origine identiques | ≥ 2 photos : +6 (1 photo : +4) |
   | Description | quasi identique : +5 (proche : +3) |
   | Surface | écart ≤ 1 m² ou ≤ 2 % : +2 (≤ 5 % : +1) |
   | Prix | écart ≤ 2 % : +2 (≤ 8 % : +1) |
   | Position | GPS ≤ 150 m : +2 (≤ 400 m : +1), plus la marge de précision du GPS. Sans GPS, même CP : +1 |
   | Pièces identiques | +1 |
   | Étage identique | +1 |
   | Chambres identiques | +0,5 |
   | Meublé différent | −1 |

4. **Plafond** : un même bien est rarement confié à deux agences. Score plafonné à 0,6 (jamais de
   fusion automatique, au mieux une suggestion) si les noms d'agence diffèrent (sauf même
   référence) ou si la même agence donne deux références différentes. Un nom réduit à un sigle
   court (« YFR ») ne permet pas de conclure.
5. **Seuils** :
   - score ≥ 0,8 : fusion automatique (réversible).
   - score ≥ 0,5 : suggestion.

Un critère absent d'une des deux annonces ne compte ni pour ni contre.

## Ajouter un site

1. Créer `plugin/src/sites/<site>/parse.ts`. Ce fichier contient des fonctions pures. Ajouter une
   fixture HTML réelle et un test.
2. Créer `plugin/src/sites/<site>/adapter.ts`. Ce fichier contient les sélecteurs. Il peut aussi
   enrichir les données avec le JSON du site.
   Optionnel : `findDetail` / `parseDetail` (page d'une annonce : bandeau de suivi),
   `hideSelectors` (emplacements de pub vides à masquer), `isSearchPage` (bouton « Enregistrer
   cette recherche » du panneau 🏠).
3. Enregistrer l'adaptateur dans `plugin/src/sites/index.ts`.
4. Ajouter la ligne `@match` du site dans `plugin/userscripts/recherche-logement.mjs`. La mise à
   jour automatique déploie ce changement.
5. Ajouter le libellé du site dans `server/.../Model/Labels.cs`, et son domaine dans
   `SavedSearch.SiteOf` (`Model.cs`) pour regrouper ses recherches favorites.

## Mes projets (recherche faite par le serveur)

Partie séparée du catalogue : autres données (`projets.db`), autres services, autres pages.

```
Core/Projects/ProjectModel.cs   SearchProject (critères, lieux, sites), ProjectResult, ProjectRun,
                                SourceSites (catalogue des sites + catégorie + disponible ou "à venir"),
                                ProjectMatcher (filtre final : critères, mots-clés)
Core/Projects/ProjectStore.cs   Projets, résultats, passages en mémoire + IProjectPersistence
Server/Projects/Collectors.cs   ISiteCollector : BieniciCollector, SelogerCollector
Server/Projects/ProjectRunner.cs  Lancement (un à la fois) + ProjectScheduler (BackgroundService, chaque minute)
Server/Projects/SqliteProjectPersistence.cs  projets.db (objets en JSON)
Components/Pages/Projets.razor, ProjetEdit.razor, Projet.razor
```

Déroulement d'un lancement, pour chaque site activé :

1. Lieux : chaque saisie (« Rodez », « 12850 ») est résolue une fois par site, puis gardée dans le
   projet (Bien'ici : `res.bienici.com/suggest.json` → `zoneIds` ; SeLoger :
   `search-mfe-bff/autocomplete/suggestion` → `placeIds`).
2. Recherche avec les filtres du site, plus récentes d'abord (Bien'ici : `realEstateAds.json?filters=…`,
   100 par page, 3 pages ; SeLoger : `POST serp-bff/search`, 30 par page, 4 pages, puis
   `classifiedList/<ids>` pour les données).
3. Conversion au format commun (`ListingData`, mêmes règles que le plugin), filtre final
   `ProjectMatcher`, fusion dans les résultats (une annonce déjà connue n'est plus « nouvelle »).
4. Un `ProjectRun` par site : OK (nombres) ou message d'erreur lisible (anti-robot, lieu inconnu…).

Ajouter un site : une entrée `Available: true` dans `SourceSites`, une classe `ISiteCollector`
enregistrée dans `CollectorHttp.AddCollectors`, des tests de conversion sans réseau (`ProjectTests`).

## Pistes

- **Relier Mes projets au catalogue** : envoyer les résultats d'un projet dans `Catalog.Sync`
  (observations `api`), pour profiter des doublons et des statuts du plugin. Séparé pour l'instant.
- **SeLoger bloqué côté serveur** : faire faire la recherche par le plugin (navigateur) pour le
  compte du serveur.
- Hash perceptuel des photos, pour les sites qui hébergent une nouvelle copie des images.
