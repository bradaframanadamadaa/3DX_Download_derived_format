# DerivedOutputDownloader3DX

Outil console autonome pour télécharger les **formats dérivés** (PDF, STEP, DXF, etc.) d'objets 3DEXPERIENCE.

La couche de connexion réutilise l'architecture documentée dans `CONNEXION_3DEXPERIENCE_ET_API.md` (projet Reconciliation_3DX_local) : CAS 3DPassport → cookies de session → FedSearch → `SecurityContext` sur 3DSpace.

## Prérequis

- .NET 8 SDK
- Compte 3DEXPERIENCE avec accès FedSearch et droits sur les derived outputs ciblés
- Variables d'environnement (ne jamais committer les secrets) :

| Variable | Rôle |
|----------|------|
| `THREE_DX_USERNAME` | Identifiant CAS |
| `THREE_DX_PASSWORD` | Mot de passe CAS |
| `THREE_DX_PASSPORT_URL` | URL IAM (si absente du JSON) |

## Configuration

1. Copier `samples/appsettings.sample.json` vers `src/DerivedOutputDownloader3DX/appsettings.json`
2. Renseigner `ThreeExperience:SecurityContext` et les URLs (ou utiliser `ThreeExperienceTenantDefaults.BuildInteractiveCloudOptions` dans un futur mode CLI)
3. Optionnel : définir `App:OutputDirectory` (défaut : `output/` à la racine du dépôt)

## Build et exécution

```powershell
cd C:\git\DerivedOutputDowload
dotnet build
dotnet run --project src\DerivedOutputDownloader3DX -- --connection-only
dotnet run --project src\DerivedOutputDownloader3DX -- --search-title "Mon_Piece"
```

## Commandes disponibles

| Option | Description |
|--------|-------------|
| `--connection-only` | Test CAS + sonde FedSearch (`ConnectionProbeTitle`) |
| `--search-title <titre>` | Recherche FedSearch par titre |
| `--settings <chemin>` | Fichier de configuration JSON |
| `--help` | Aide |

Les options `--object-id` et `--format` sont réservées à la future couche REST **dsdo** (voir `docs/ANALYSE.md`).

## Structure

```
C:\git\DerivedOutputDowload
├── DerivedOutputDownloader3DX.sln
├── src\DerivedOutputDownloader3DX\
│   ├── Program.cs
│   ├── DerivedOutputClient.cs          ← stub API derived outputs
│   ├── Configuration\                  ← ThreeExperienceOptions, tenant defaults
│   ├── Models\
│   ├── Services\Connection\              ← CAS, FedSearch, runtime builder
│   ├── Services\DerivedOutput\         ← endpoints dsdo (patterns), CSRF helper
│   └── Logging\
├── docs\
│   ├── ANALYSE.md                      ← rapport d'analyse (endpoints, décisions)
│   └── README.md                       ← ce fichier
├── samples\appsettings.sample.json
└── output\                             ← fichiers téléchargés (futur)
```

## État d'implémentation

| Composant | Statut |
|-----------|--------|
| Connexion CAS + FedSearch | ✅ Copié depuis Reconciliation |
| CLI `--connection-only`, `--search-title` | ✅ |
| REST dsdo (list / download / export job) | ⏸ Stub — validation OpenAPI requise |

Voir `docs/ANALYSE.md` pour les prochaines étapes (Postman, `CAADerivedOutputsWS/dsdo_v1.htm`).

## Références

- Source de vérité connexion : `C:\git\Reconciliation_3DX_local\CONNEXION_3DEXPERIENCE_ET_API.md`
- Doc Derived Outputs : `CAADerivedOutputsWS/dsdo_v1.htm` (3DEXPERIENCE ID requis)
