# Recherche logement — plugin + site local

Outil personnel pour suivre une recherche de logement sur plusieurs sites d'annonces.

| Partie | Rôle | Techno |
| --- | --- | --- |
| [`plugin/`](./plugin) | Script Tampermonkey sur les sites d'annonces : boutons 👁 vue · ✕ pas intéressé (masquée) · 📞 me plaît / à contacter · note ; masquage automatique ; badges de doublons | TypeScript → bundle esbuild → `@require` jsDelivr |
| [`server/`](./server) | Site web local : stocke toutes les annonces, regroupe les doublons en « biens », suivi des contacts (tableau par étape), historique, sauvegarde | ASP.NET Core 10 (API + Blazor) + SQLite, Docker |

Le plugin envoie ce qu'il voit et ce que tu fais au site local (`POST /api/sync`, protégé par un
token) ; le site répond avec l'état de chaque annonce. Si le site n'est pas lancé, le plugin
continue de masquer / étiqueter grâce à son cache et envoie les actions en attente au retour du
serveur.

Sites supportés : **Bien'ici**. Voir [`ARCHITECTURE.md`](./ARCHITECTURE.md) pour ajouter un site.

## Démarrage

### 1. Le site local (Docker)

```bash
docker compose up -d --build
```

Ouvrir <http://localhost:5080> → **Paramètres** : le token du plugin et le script Tampermonkey
complet, prêt à copier. (Le token est aussi écrit dans `docker compose logs server`.)

Les données sont dans le volume Docker `recherche-logement-data` (fichier SQLite). Le port est lié à
`127.0.0.1` : le site n'est pas accessible depuis le réseau.

Sans Docker : `cd server && dotnet run --project src/RechercheLogement.Server` (base dans
`src/RechercheLogement.Server/data/`).

### 2. Le plugin (Tampermonkey)

1. Tampermonkey → nouveau script → coller le script copié depuis **Paramètres**.
2. Remplacer `plugin-vX.Y.Z` dans `@require` par le dernier tag
   `plugin-v*` publié par la CI (en local : `@require file:///…/plugin/dist/recherche-logement.js`,
   avec « Autoriser l'accès aux URL de fichiers » activé pour Tampermonkey).
3. Ouvrir une recherche sur bienici.com : la barre d'actions apparaît sur chaque annonce et le
   bouton 🏠 (en bas à droite) indique l'état de la connexion.

**Un seul script Tampermonkey pour tous les sites** (un `@match` par site). Les lignes
`@connect localhost` / `@connect 127.0.0.1` autorisent le plugin à joindre le site local.

## Utilisation

Dans le plugin, sur chaque annonce :

| Bouton | Effet |
| --- | --- |
| 👁 | Vue : reste visible, atténuée |
| ✕ | Pas intéressé : masquée (sur tous les sites si le doublon est reconnu) |
| 📞 | Me plaît, à contacter : contour vert + étiquette d'étape (« À contacter », « Visite prévue »…) |
| ⋯ / 📝 | Note, annonces associées, lien vers la fiche sur le site local |
| 🔗 n | Même bien publié dans n autres annonces |
| ≈ déjà vue ? 60 % | Doublon probable à confirmer (« Même bien » / « Pas le même ») |

Sur le site local :

- **Tableau de bord** : compteurs, biens à contacter, vus récemment ;
- **📞 À contacter** : un tableau par étape (à contacter → contactée → visite prévue → visitée →
  dossier envoyé → accepté / refusé), flèches pour avancer ;
- **Tous les biens** : filtres (statut, texte, code postal, prix max, surface min) et tri ;
- **Fiche d'un bien** : toutes ses annonces (tous sites), historique de prix, note, commentaires
  datés (appels, visites…), dissociation d'un doublon erroné ;
- **Doublons à vérifier** : comparaison côte à côte ;
- **Paramètres** : token (copier / régénérer), script Tampermonkey, export JSON.

## Développement

```bash
# Plugin
cd plugin && npm install && npm test && npm run build      # -> plugin/dist/recherche-logement.js

# Serveur
cd server && dotnet test                                    # tests xUnit (métier + API)
dotnet run --project src/RechercheLogement.Server           # http://localhost:5080
```

## CI/CD (GitHub Actions)

- [`plugin.yml`](./.github/workflows/plugin.yml) : tests + build ; sur `main`, publie le bundle sur un
  tag `plugin-vX.Y.Z` servi par jsDelivr (`[minor]` / `[major]` dans un message de commit pour monter
  la version).
- [`server.yml`](./.github/workflows/server.yml) : build + tests .NET ; sur `main`, publie l'image
  `ghcr.io/nicolas-goyon/outilrecherchelocation-server:latest`.

## Limites connues

- Bien'ici : le plugin relit le JSON de la liste (une requête de plus par page) ; les deux annonces
  « mises en avant » n'ont souvent que les données de la carte.
- Pages de résultats uniquement (pas encore la fiche d'une annonce sur le site d'origine).
- Le site local n'a pas de connexion utilisateur : il est prévu pour tourner sur ta machine, lié à
  `127.0.0.1`.
