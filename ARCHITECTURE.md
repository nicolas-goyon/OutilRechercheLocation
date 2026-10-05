# Architecture

```
 Navigateur (bienici.com, ... )                         Machine locale (Docker)
┌──────────────────────────────────┐   POST /api/sync   ┌───────────────────────────────────┐
│ plugin/ (Tampermonkey)           │  Bearer <token>    │ server/ (ASP.NET Core 10)          │
│  adaptateur du site -> cartes    │ ─────────────────▶ │  ApiTokenMiddleware                │
│  SyncEngine : file + cache       │ ◀───────────────── │  Catalog (métier, en mémoire)      │
│  barre d'actions, masquage       │   ListingView      │   ├ DedupScorer (doublons)         │
└──────────────────────────────────┘                    │   └ IPersistence -> SQLite (/data) │
                                                        │  Site Blazor (tableau de bord...)  │
                                                        └───────────────────────────────────┘
```

## Principes

- **Le serveur est la source de vérité** : annonces, biens, doublons, statuts, historique.
- **Le plugin reste léger et autonome** : il observe, envoie, affiche. Hors ligne, il garde une
  file d'actions et un cache d'états (stockage Tampermonkey) pour continuer à masquer.
- **La décision porte sur le bien, pas sur l'annonce** : « pas intéressé » sur Bien'ici masque
  aussi l'annonce SeLoger reconnue comme le même bien.
- **Générique au centre, spécifique aux bords** : seul `plugin/src/sites/<site>/` connaît un site.

## Arborescence

```
plugin/
  src/index.ts            init({ serverUrl, apiToken }) : adaptateur + SyncEngine + Tracker + UI
  src/core/types.ts       Types + contrat d'API (miroir de server/.../Contracts)
  src/core/api.ts         Client HTTP (GM_xmlhttpRequest, header Authorization)
  src/core/sync.ts        SyncEngine : observations + actions en file, envoi par lots,
                          cache des ListingView, application optimiste, backoff hors ligne
  src/core/storage.ts     Wrapper GM_getValue / GM_setValue
  src/sites/              Adaptateurs (bienici/ : parsing carte + JSON API)
  src/app/tracker.ts      Cartes de la page -> observations ; état -> attributs data-tmrl-*
  src/ui/                 Barre par carte, modales, bouton 🏠, CSS injecté
  tests/                  node:test (parsing sur fixtures réelles, SyncEngine hors ligne)

server/
  src/RechercheLogement.Core/      Métier pur, sans dépendance NuGet
    Model/                Listing, Property, DuplicateLink, PropertyEvent, statuts, libellés
    Dedup/DedupScorer.cs  Score de doublon explicable
    Contracts/            DTO de /api/sync
    Services/Catalog.cs   Toutes les opérations (sync, statuts, fusion, requêtes du site)
    Services/Persistence.cs  IPersistence + ChangeSet + InMemoryPersistence
  src/RechercheLogement.Server/    Hôte web
    Program.cs, ServerSettings.cs
    Api/                  /api/ping, /api/sync, /api/listings/{key}, /api/export + token
    Storage/SqlitePersistence.cs   Schéma, migrations (PRAGMA user_version), écriture transactionnelle
    Components/           Pages Blazor (interactive server) : tableau de bord, à contacter,
                          biens, fiche, doublons, paramètres
  tests/RechercheLogement.Tests/   xUnit : DedupScorer, Catalog, API (WebApplicationFactory)
  Dockerfile
docker-compose.yml
.github/workflows/        plugin.yml (tag plugin-v* + jsDelivr), server.yml (tests + image GHCR)
```

## Modèle de données

| Entité | Rôle | Champs clés |
| --- | --- | --- |
| **Listing** | Une annonce sur un site, clé `site:siteId` | `PropertyId`, `Data` (prix, surface, pièces, CP, GPS, réf. agence, `PhotoKeys`, extrait de description…), `FirstSeenAt`, `LastSeenAt`, `PriceHistory`, `Sources` (`card`/`api`) |
| **Property** | Le bien réel | `Status` (`None`, `Seen`, `Rejected`, `ToContact`), `ContactStage` (`Pending` → `Contacted` → `VisitScheduled` → `Visited` → `ApplicationSent` → `Accepted`/`Declined`), `Note`, `StatusChangedAt` |
| **DuplicateLink** | Paire candidate | `Score`, `Reasons`, `State` (`Suggested`, `Confirmed`, `Dismissed`), `DecidedBy` (`auto`/`user`) |
| **PropertyEvent** | Historique d'un bien | changement de statut / d'étape, note, fusion, dissociation, commentaire |

Règles :

- Chaque annonce a exactement un bien ; une nouvelle annonce crée un bien `None`.
- Confirmer un doublon fusionne les biens : on garde **le plus ancien** (URL et historique stables)
  et le statut de la décision la plus récente.
- Dissocier remet l'annonce dans un nouveau bien et marque les liens `Dismissed` (plus jamais
  proposés).
- Un statut envoyé par le plugin avec un horodatage **antérieur** au dernier changement fait sur le
  site est ignoré (cas d'une action restée en file pendant une coupure).
- Les données d'une carte HTML ne remplacent pas celles, plus précises, du JSON du site.

## Synchronisation (`POST /api/sync`)

Détail dans [`docs/api.md`](./docs/api.md). En résumé, un seul appel par lot :

- `observations` : annonces vues (données carte ou API) ;
- `actions` : `setStatus`, `setNote`, `confirmDuplicate`, `dismissDuplicate`, `detach`, chacune
  avec un `id` unique et un horodatage `at` ;
- réponse : `appliedActionIds` / `rejectedActionIds` et une `ListingView` par annonce concernée
  (statut, étape, note, annonces sœurs, suggestions, lien vers la fiche).

Les actions sont **idempotentes** : le serveur mémorise leurs `id` (90 jours), un renvoi après
une coupure réseau n'est pas réappliqué.

## Sécurité

- Toutes les routes `/api/*` exigent `Authorization: Bearer <token>` (comparaison à temps
  constant). Token : `RechercheLogement__ApiToken` s'il est défini, sinon généré au premier
  démarrage (32 octets aléatoires) et stocké en base ; régénérable dans Paramètres.
- Le plugin appelle le serveur via `GM_xmlhttpRequest` (`@connect localhost`) : pas d'en-têtes CORS
  à ouvrir côté serveur, donc aucune autre page web ne peut lire l'API depuis le navigateur.
- Le site web lui-même n'a pas d'authentification : Docker le lie à `127.0.0.1` uniquement.

## Détection de doublons (`DedupScorer`)

1. **Blocage** : comparaison uniquement entre annonces du même code postal (75116 ≡ 75016).
2. **Rejets durs** : transaction ou type différents, CP différents (sauf GPS < 300 m), distance
   > 1,5 km (+ flou), surface à plus de 10 %, ≥ 2 pièces d'écart.
3. **Points** (score = points / 10, plafonné à 1) : même réf. agence +6 (compatible +4) ; ≥ 2 photos
   d'origine identiques +6 (1 : +4) ; description quasi identique +5 (proche +3) ; surface ±1 m²/2 %
   +2 (5 % : +1) ; prix ≤ 2 % +2 (≤ 8 % : +1) ; GPS ≤ 150 m +2 (≤ 400 m : +1) sinon même CP +1 ;
   pièces +1 ; étage +1 ; chambres +0,5 ; meublé ≠ −1.
4. **Seuils** : ≥ 0,8 fusion automatique (réversible) ; ≥ 0,5 suggestion.

## Ajouter un site

1. `plugin/src/sites/<site>/parse.ts` (fonctions pures + fixture HTML réelle + test) et
   `adapter.ts` (sélecteurs, enrichissement éventuel via le JSON du site).
2. L'enregistrer dans `plugin/src/sites/index.ts`, ajouter son `@match` au script Tampermonkey et
   son libellé dans `server/.../Model/Labels.cs`.

## Pistes

- **Collecte automatique** : un service d'arrière-plan côté serveur (`IHostedService`) qui
  interroge les recherches enregistrées et appelle `Catalog.Sync` avec des observations — même
  chemin que le plugin, donc doublons et statuts gérés sans code supplémentaire.
- Fiche d'annonce sur le site d'origine (barre de statut + enrichissement).
- Hash perceptuel des photos pour les sites qui ré-hébergent les images.
