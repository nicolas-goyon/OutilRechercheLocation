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
Core/Projects/ProjectModel.cs     SearchProject (critères, mode de zone, AreaPlan, cache des lieux par site),
                                  ProjectResult (+ historique, groupe), ProjectMatcher, AreaFilter
Core/Projects/ProjectStore.cs     Projets, résultats, passages ; suivi (changements, retraits, réapparitions)
                                  et regroupement des annonces du même bien (DedupScorer)
Core/Geo/GeoShape.cs              Cercle ou polygones (isochrone) : appartenance, bord, distances
Core/Text/RefExtractor.cs         Références et SIREN écrits en clair (mêmes règles que le plugin)

Server/Sites/                     UN DOSSIER PAR SITE, chaque dossier regroupe les fonctionnalités du site
  SiteModule.cs                   Contrats : ISiteModule, IListingParser, ISearchAdapter, PlaceKinds...
  SiteRegistry.cs                 Modules enregistrés + sites "à venir" (PAP, Logic-Immo, Leboncoin, agences)
  SitesRegistration.cs            Client HTTP commun + enregistrement des modules
  Common/                         SiteHttp (erreurs lisibles), JsonRead (lecture tolérante)
  Bienici/  BieniciSite.cs            module (identité + fonctionnalités)
            BieniciListingParser.cs   1. parser d'annonces : JSON de l'API -> ListingData
            BieniciSearchAdapter.cs   2. adaptateur de recherche : lieux (suggest.json) + recherche
  Seloger/  SelogerSite.cs, SelogerListingParser.cs, SelogerSearchAdapter.cs (même découpage)
Server/Geo/GeoServices.cs         Géocodage (IGN), communes (geo.api.gouv.fr), isochrones (Valhalla/OSM),
                                  suggestions de saisie (lieux, adresses) et point -> commune / adresse
Server/Projects/AreaPlanner.cs    Zone d'un projet : point, forme exacte, départements, communes
Server/Projects/ProjectRunner.cs  Lancement + ProjectScheduler (BackgroundService, chaque minute)
Components/Pages/Projets.razor, ProjetEdit.razor, Projet.razor
Components/Shared/SuggestInput.razor   Champ avec suggestions (flèches, Entrée, « ; » ou « , » valide)
Components/Shared/ZoneMap.razor        Carte Leaflet (wwwroot/app.js : rl.zoneMap) : clic, marqueur
                                       déplaçable, cercle, isochrone, lieux choisis
```

Saisie de la zone (`ProjetEdit`) : en mode lieux, chaque suggestion choisie devient un
`ProjectLocation` typé (commune avec code INSEE, codes postaux et centre ; code postal ; département),
affiché en étiquette. Les projets plus anciens gardent des lieux « libres » (texte transmis tel quel).
En modes rayon et temps de trajet, le point choisi (suggestion ou carte) est enregistré dans
`SearchProject.Center` : il n'est plus géocodé au lancement, et l'empreinte de la zone suit ses
coordonnées. Un texte non validé reste géocodé au lancement. Leaflet 1.9.4 est chargé depuis unpkg
(avec contrôle d'intégrité) et le fond de carte vient des tuiles OpenStreetMap.

Déroulement d'un lancement :

1. **Zone** (modes rayon / temps de trajet) : point choisi (sinon géocodage du texte), cercle ou isochrone (cercle estimé
   si l'isochrone échoue), départements touchés (centre + 24 points du bord), communes de ces
   départements avec leur centre (« dans la zone » si le centre y est, marge 1,5 km). Gardée dans le
   projet, recalculée si la zone change ou tous les 30 jours.
2. **Lieux par site** : ce que chaque site sait chercher (`ISearchAdapter.SupportedPlaces`) décide.
   Mode lieux : commune (nom + code postal pour départager les homonymes), code postal ou département
   choisis. Zone : codes postaux de la zone (≤ 80), sinon départements.
   Les lieux reconnus sont gardés dans `SearchProject.PlaceCache`.
3. **Recherche** avec les filtres du site, plus récentes d'abord (Bien'ici : 100 par page ; SeLoger :
   30 par page puis `classifiedList`), jusqu'à 300 (lieux) ou 1 000 (zone) annonces.
4. **Filtre local** : critères (`ProjectMatcher`) et forme exacte de la zone (`AreaFilter` : GPS de
   l'annonce, sinon centre de sa commune, marge 500 m + flou GPS).
5. **Suivi** (`ProjectStore.ApplyResults`) : nouvelle annonce (regroupée avec un bien connu si le score
   de doublon ≥ 0,8), changements (prix, charges, surface, pièces, meublé, agence, titre, texte),
   retrait (absente d'une recherche **complète**), réapparition. Un `ProjectRun` par site.

Ajouter un site : un dossier `Server/Sites/<Site>/` avec `<Site>Site.cs` (module) et ses
fonctionnalités, une ligne dans `SitesRegistration.AddSites`, des tests de conversion sans réseau
(`ProjectTests`). Ajouter une fonctionnalité à tous les sites : une interface dans `SiteModule.cs`,
une propriété dans `ISiteModule`, une classe par dossier.

## Pistes

- **Relier Mes projets au catalogue** : envoyer les résultats d'un projet dans `Catalog.Sync`
  (observations `api`), pour profiter des doublons et des statuts du plugin. Séparé pour l'instant.
- **SeLoger bloqué côté serveur** : faire faire la recherche par le plugin (navigateur) pour le
  compte du serveur.
- Hash perceptuel des photos, pour les sites qui hébergent une nouvelle copie des images.
