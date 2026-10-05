# API plugin ↔ serveur

Base : `http://localhost:5080`. Toutes les routes `/api/*` exigent :

```
Authorization: Bearer <token>
```

Réponse `401 {"error": "Token API manquant ou invalide"}` sinon. JSON en camelCase, énumérations
en camelCase (`toContact`, `visitScheduled`…). Dates : millisecondes Unix.

Contrat côté plugin : `plugin/src/core/types.ts` ; côté serveur :
`server/src/RechercheLogement.Core/Contracts/SyncContracts.cs`. Les deux doivent rester alignés.

## `GET /api/ping`

```json
{ "name": "recherche-logement", "version": "0.2.0", "webUrl": "http://localhost:5080" }
```

## `POST /api/sync`

Requête (max 1000 observations et 1000 actions par lot) :

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
| `card` | données lues sur la carte HTML (ne remplacent pas des données `api` déjà connues) |
| `api` | JSON du site (plus précis) |
| `detail` | fiche d'annonce (réservé) |

| `type` d'action | champs |
| --- | --- |
| `setStatus` | `status` : `none` \| `seen` \| `rejected` \| `toContact` |
| `setNote` | `note` (vide = effacer) |
| `confirmDuplicate` / `dismissDuplicate` | `otherKey` |
| `detach` | — (sort l'annonce de son bien) |

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

`listings` contient une entrée par annonce observée, visée par une action ou demandée dans `want`.
`contactStage` n'est présent que pour `status = toContact`. Une action dont l'`id` a déjà été
traité est renvoyée dans `appliedActionIds` sans être réappliquée ; une action sur une annonce
inconnue est dans `rejectedActionIds` (le plugin la retire de sa file).

## `GET /api/listings/{key}`

`ListingView` d'une annonce, `404` si inconnue.

## `GET /api/export`

Sauvegarde complète (annonces, biens, liens, historique).

## Hors API

- `GET /health` (sans token) : `{"status":"ok"}`.
- Le site web (`/`, `/biens`, `/a-contacter`, `/doublons`, `/parametres`) n'utilise pas l'API : il
  appelle directement le service métier côté serveur (Blazor).
