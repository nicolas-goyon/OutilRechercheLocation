# API plugin ↔ serveur

Base : `http://localhost:5080`. Toutes les routes `/api/*` exigent cet en-tête :

```
Authorization: Bearer <token>
```

Si le token est absent ou incorrect, le serveur répond `401 {"error": "Token API absent ou incorrect."}`.

Formats :

- JSON en camelCase.
- Énumérations en camelCase (`toContact`, `visitScheduled`…).
- Dates en millisecondes Unix.

Le contrat existe en deux copies :

- plugin : `plugin/src/core/types.ts`.
- serveur : `server/src/RechercheLogement.Core/Contracts/SyncContracts.cs`.

Toute modification d'une copie exige la même modification dans l'autre copie.

## `GET /api/ping`

```json
{ "name": "recherche-logement", "version": "0.2.0", "webUrl": "http://localhost:5080" }
```

## `POST /api/sync`

Requête. Un lot contient au maximum 1000 observations et 1000 actions.

```json
{
  "clientVersion": "0.2.0",
  "observations": [
    {
      "site": "bienici", "siteId": "hektor-sia-immo-2342", "source": "api", "seenAt": 1791217372725,
      "data": {
        "title": "Appartement 3 pièces 88 m²", "transaction": "rent", "propertyType": "flat",
        "price": 1866, "charges": 65, "surface": 88.32, "rooms": 3, "bedrooms": 2, "floor": 1,
        "furnished": false, "postalCode": "75019", "city": "Paris 19e", "district": "Bassin de la Villette",
        "geo": { "lat": 48.8901, "lon": 2.3769, "precisionM": 50 },
        "agencyRef": "TSLAP320002342", "photos": ["https://file.bienici.com/photo/…"],
        "photoKeys": ["photo_3c9c36dc663b54820545f52f6aef3a1e"], "descriptionExcerpt": "exclusivite sia …",
        "url": "https://www.bienici.com/annonce/location/…/hektor-sia-immo-2342"
      }
    }
  ],
  "actions": [
    { "id": "mfx1-ab12cd34", "at": 1791217380000, "type": "setStatus", "key": "bienici:hektor-sia-immo-2342", "status": "rejected" },
    { "id": "mfx2-ef56gh78", "at": 1791217390000, "type": "confirmDuplicate", "key": "pap:123", "otherKey": "bienici:hektor-sia-immo-2342" }
  ],
  "want": ["bienici:autre-annonce"]
}
```

| `source` | sens |
| --- | --- |
| `card` | Données lues sur la carte HTML. Elles ne remplacent pas des données `api` déjà connues. |
| `api` | JSON du site d'annonces (plus précis). |
| `detail` | Page d'une annonce (réservé pour une version future). |

| `type` d'action | champs |
| --- | --- |
| `setStatus` | `status` : `none` \| `seen` \| `rejected` \| `toContact` |
| `setNote` | `note` (une note vide efface la note) |
| `confirmDuplicate` / `dismissDuplicate` | `otherKey` |
| `detach` | Aucun champ. Sépare l'annonce de son bien. |

Réponse :

```json
{
  "serverTime": 1791217391000,
  "appliedActionIds": ["mfx1-ab12cd34", "mfx2-ef56gh78"],
  "rejectedActionIds": [],
  "listings": {
    "bienici:hektor-sia-immo-2342": {
      "key": "bienici:hektor-sia-immo-2342",
      "propertyId": "p_051f29fa072d",
      "status": "rejected",
      "note": "Trop loin du métro",
      "siblings": [{ "key": "seloger:999", "site": "seloger", "title": "3 pièces 88 m²", "price": 1801 }],
      "suggestions": [],
      "webUrl": "http://localhost:5080/biens/p_051f29fa072d"
    }
  }
}
```

`listings` contient une entrée pour chaque annonce de ces trois types :

- annonce observée.
- annonce visée par une action.
- annonce demandée dans `want`.

`contactStage` est présent uniquement si `status = toContact`.

Si le serveur a déjà traité l'`id` d'une action, il met cet `id` dans `appliedActionIds`. Il
n'applique pas l'action une deuxième fois.

Si une action vise une annonce inconnue, le serveur met son `id` dans `rejectedActionIds`. Le plugin
retire alors l'action de sa file.

## `GET /api/listings/{key}`

Retourne la `ListingView` d'une annonce. Retourne `404` si l'annonce est inconnue.

## `GET /api/export`

Retourne une sauvegarde complète (annonces, biens, liens, historique).

## Hors API

- `GET /health` (sans token) retourne `{"status":"ok"}`.
- Les pages web du serveur (`/`, `/biens`, `/a-contacter`, `/doublons`, `/parametres`) n'utilisent
  pas l'API. Elles appellent directement le service métier sur le serveur (Blazor).
