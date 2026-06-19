# 3DX Download Derived Format

Application WPF (.NET 8) pour télécharger les **formats dérivés** (STEP, PDF, ACIS…) depuis la plateforme **3DEXPERIENCE** de Dassault Systèmes.

---

## Fonctionnalités

### Mode objet unique
- Authentification CAS / 3DPassport
- Recherche d'objets 3DEXPERIENCE par **titre** ou par **nom**
- Affichage des formats dérivés disponibles pour l'objet sélectionné
- Sélection et téléchargement des formats souhaités vers un dossier local

### Mode assemblage
- Saisie d'un **assemblage VPMReference** dans la barre de recherche
- Développement automatique de la **nomenclature complète** (BOM) :
  - Sous-assemblages récursifs
  - Pièces feuilles
- Pour chaque pièce :
  - Formats dérivés 3D (STEP, ACIS, PDF…)
  - Mises en plan CAD associées (`dsxcad:Drawing`) et leurs formats (PDF…)
- Arborescence avec **cases à cocher** par fichier
- **Filtres rapides par format** : `PDF`, `STEP_AP214`, `ACIS`… (bascule tout/aucun)
- Boutons **Tout sélectionner / Tout désélectionner**
- Téléchargement en masse avec suivi de progression

---

## Architecture

```
src/
├── DerivedOutputDownloader3DX/          ← Bibliothèque cœur (.NET 8)
│   ├── Configuration/                   ← ThreeExperienceOptions, AppOptions
│   ├── Models/                          ← BomNode, DerivedOutputDescriptor…
│   ├── Services/
│   │   ├── Bom/                         ← BomExpander, AssemblyDownloadOrchestrator
│   │   ├── Connection/                  ← CAS, FedSearch, ThreeDxCasPassportClient
│   │   ├── DerivedOutput/               ← endpoints dsdo, CSRF helper
│   │   └── Drawing/                     ← DrawingFinder (dsxcad:Drawing/Locate)
│   ├── DerivedOutputClient.cs           ← Client principal dsdo (list + download)
│   └── Program.cs                       ← CLI de test / diagnostic
│
├── DerivedOutputDownloader3DX.UI/       ← Application WPF
│   ├── Views/
│   │   ├── LoginWindow.xaml             ← Authentification CAS
│   │   ├── MainWindow.xaml              ← Recherche + téléchargement unitaire
│   │   ├── AssemblyBrowserWindow.xaml   ← Navigateur BOM assemblage
│   │   └── DownloadProgressWindow.xaml  ← Suivi de progression
│   ├── ViewModels/                      ← MVVM (INotifyPropertyChanged, RelayCommand)
│   ├── Models/                          ← DownloadJobItem, DerivedOutputItem
│   └── Converters/                      ← InverseBoolToVisibility, StringToVisibility
│
└── BomValidator/                        ← Outil console de validation (dev)
    └── Program.cs                       ← Valide BOM + formats + mises en plan

samples/
└── appsettings.sample.json              ← Modèle de configuration (sans secrets)
```

---

## Prérequis

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Compte 3DEXPERIENCE avec droits FedSearch et accès aux derived outputs

---

## Configuration

1. Copier `samples/appsettings.sample.json` à côté de l'exe sous le nom `appsettings.json`
2. Remplir **uniquement 2 champs** — tout le reste est calculé automatiquement
3. Ne **jamais** committer `appsettings.json` (déjà dans `.gitignore`)

```json
{
  "ThreeExperience": {
    "Tenant":             "R1132XXXXXXXXXX",
    "CollaborativeSpace": "Common Space"
  }
}
```

| Champ | Description | Exemple |
|-------|-------------|---------|
| `Tenant` | Numéro de plateforme 3DEXPERIENCE | `R1132102597931` |
| `CollaborativeSpace` | Nom de l'espace collaboratif | `Common Space` |

Toutes les URLs (`PlatformUrl`, `ThreeDSpaceUrl`, `FedSearchUrl`, `PassportBaseUrl`) ainsi que le `SecurityContext` (`ctx::VPLMProjectLeader.Company Name.{CollaborativeSpace}`) sont construits automatiquement au démarrage.

> Le login et le mot de passe sont saisis dans l'interface — rien à stocker dans le fichier.

---

## Build & Exécution

### Application WPF

```powershell
# Build
dotnet build src\DerivedOutputDownloader3DX.UI

# Publier (framework-dependent, léger)
dotnet publish src\DerivedOutputDownloader3DX.UI -c Release -o publish\UI
.\publish\UI\DerivedOutputDownloader3DX.UI.exe
```

### CLI de diagnostic

```powershell
# Test de connexion CAS + FedSearch
dotnet run --project src\DerivedOutputDownloader3DX -- --connection-only

# Recherche par titre
dotnet run --project src\DerivedOutputDownloader3DX -- --search-title "Manivelle_Pion"

# Lister les SecurityContexts disponibles
dotnet run --project src\DerivedOutputDownloader3DX -- --list-contexts
```

### Outil de validation BOM (dev)

```powershell
dotnet run --project src\BomValidator -- --title "Cardan_Manivelle"
```

---

## APIs 3DEXPERIENCE utilisées

| Service | Endpoint | Usage |
|---------|----------|-------|
| FedSearch | `GET /federated/search` | Recherche d'objets par titre/nom |
| dsdo | `GET /dsdo/DerivedOutputs/{id}` | Liste des formats dérivés |
| dsdo | `GET /dsdo/...DownloadTicket` + FCS | Téléchargement des fichiers |
| dseng | `POST /dseng:EngItem/{id}/expand` | Développement BOM (récursif) |
| dsxcad | `POST /dsxcad:Drawing/Locate` | Mises en plan associées à une pièce |

---

## Variables d'environnement (optionnel)

| Variable | Rôle |
|----------|------|
| `THREE_DX_USERNAME` | Identifiant CAS (mode non-interactif) |
| `THREE_DX_PASSWORD` | Mot de passe CAS (mode non-interactif) |
| `THREE_DX_PASSPORT_URL` | URL IAM si absente du JSON |
| `THREE_DX_BEARER_TOKEN` | Jeton Bearer (alternatif au flux CAS) |
