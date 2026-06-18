# Rapport d'analyse — DerivedOutputDownloader3DX

> Date : juin 2026  
> Source de vérité connexion : `CONNEXION_3DEXPERIENCE_ET_API.md` (projet Reconciliation_3DX_local)  
> Statut Derived Outputs API : **endpoints identifiés au niveau documentation officielle, implémentation HTTP reportée**

---

## 1. Objectif du nouvel outil

Télécharger les **formats dérivés** (PDF, STEP, DXF, etc.) d'un objet 3DEXPERIENCE, en réutilisant **exactement** la couche de connexion validée sur le tenant réel (CAS → cookies → FedSearch / 3DSpace).

Ce n'est **pas** un nouveau client 3DEXPERIENCE générique : c'est un outil spécialisé qui compose :

1. La connexion existante (documentée)
2. Une future couche `DerivedOutputClient` (REST 3DSpace `dsdo`)

---

## 2. Classes réutilisables (copiées depuis Reconciliation_3DX_local)

| Classe | Projet source | Rôle | Copiée dans |
|--------|---------------|------|-------------|
| `AppOptions` | Assembly3DxMatchProto | `DryRun`, `UseMockSearch`, `OutputDirectory` | `Configuration/AppOptions.cs` |
| `ThreeExperienceOptions` | Assembly3DxMatchProto | URLs, SecurityContext, variables env | `Configuration/ThreeExperienceOptions.cs` |
| `ThreeExperienceTenantDefaults` | Assembly3DxMatchProto | Dérivation URLs depuis tenant | `Configuration/ThreeExperienceTenantDefaults.cs` |
| `ThreeDxSearchRuntimeBuilder` | Assembly3DxMatchProto | Factory Mock / Bearer / CAS | `Services/Connection/` |
| `ThreeDxCasPassportClient` | Assembly3DxMatchProto | Login CAS 3DPassport | `Services/Connection/` |
| `FedSearchThreeDExperienceSearchService` | Assembly3DxMatchProto | Recherche par titre | `Services/Connection/` |
| `IThreeDExperienceSearchService` | Assembly3DxMatchProto | Interface recherche | `Services/Connection/` |
| `MockThreeDExperienceSearchService` | Assembly3DxMatchProto | Mode test sans HTTP | `Services/Connection/` |
| `ThreeDxSearchCandidate` | Assembly3DxMatchProto | Résultat FedSearch | `Models/` |
| `ThreeDxSearchQuery` | Assembly3DxMatchProto | Paramètres recherche | `Models/` |
| `ThreeDxSearchConnectivityResult` | Assembly3DxMatchProto | Sonde connexion | `Models/` |

### Dépendances NuGet minimales

- `Microsoft.Extensions.Configuration.*` (JSON + env + binder)
- `Microsoft.Extensions.Logging.*` (console)

**Pas de** référence projet vers `Assembly3DxMatchProto` — code copié et adapté (namespace `DerivedOutputDownloader3DX`).

### Classes **non** copiées (hors périmètre)

- Tout ce qui touche SOLIDWORKS COM (`SolidWorksSessionService`, `OpenDoc7`, `ReplaceTestRunner`…)
- Document Manager, Excel, CSV Save Details, batch replacement

---

## 3. Flux de connexion (inchangé — source de vérité)

```
appsettings.json + THREE_DX_USERNAME / THREE_DX_PASSWORD
        │
        ▼
ThreeDxSearchRuntimeBuilder.BuildAsync()
        │
   ┌────┴────┐
 Mock    CAS + FedSearch (tenant réel)
        │
        ▼
FedSearchThreeDExperienceSearchService.SearchByTitleAsync()
        │
        ▼
ThreeDxSearchCandidate (Id, Title, resourceid…)
        │
        ▼
[À IMPLÉMENTER] DerivedOutputClient → REST dsdo
```

**Règles impératives** (document CONNEXION_3DEXPERIENCE_ET_API.md) :

- Ne pas réinventer les URLs → `ThreeExperienceTenantDefaults.BuildInteractiveCloudOptions`
- Ne pas remplacer FedSearch pour la résolution d'objet par titre
- Credentials via variables d'environnement, jamais dans le JSON versionné
- `ForceCasAuthentication = true` sur le tenant validé
- Après CAS : `HttpClient` partagé avec `CookieContainer` + header `SecurityContext` sur 3DSpace

---

## 4. Documentation officielle — Derived Outputs (dsdo)

### Références identifiées

| Source | URL / référence |
|--------|-----------------|
| Guide développeur Cloud | `https://media.3ds.com/support/documentation/developer/Cloud/en/DSDoc.htm?show=CAADerivedOutputsWS/dsdo_v1.htm` |
| SDK communautaire DS | NuGet `ds.enovia.dsdo` / repo `3ds-cpe-emed/3dxws-dotnet-core-sdk` |
| Pratiques SOLIDWORKS Derived Output | 3DSwym SolidPractices — section « Web Services and Events 3DSpace Derived Outputs » |
| Postman primer | 3DSwym — flux CAS + CSRF avant POST/PUT/DELETE |

### Préfixe REST attendu (famille dsdo)

D'après la documentation publique et les SDK DS (pattern identique à `dseng`, `dsmfg`, etc.) :

```
{ThreeDSpaceUrl}/resources/v1/modeler/dsdo/...
```

Exemple analogique pour Engineering Items :

```
/resources/v1/modeler/dseng/dseng:EngItem/{ID}
```

→ Pour Derived Outputs, la famille est **`dsdo`** (Derived Outputs Web Services).

### Opérations à confirmer sur le tenant (OpenAPI dsdo_v1)

Les noms exacts des ressources doivent être lus dans **CAADerivedOutputsWS/dsdo_v1.htm** (accès 3DEXPERIENCE ID requis). D'après la doc produit et les SDK :

| Besoin fonctionnel | Opération REST (à valider) | Méthode HTTP probable | Notes |
|--------------------|----------------------------|----------------------|-------|
| **Lister** les derived outputs d'un objet | `GET …/dsdo:DerivedOutputs?…` ou `GET …/dsdo:DerivedOutput/{parentId}` | GET | Retourne formats Exchange (PDF, STEP…) vs Internal |
| **Télécharger** un derived output existant | `GET …/dsdo:DerivedOutput/{id}/…/download` ou lien FCS | GET | Fichier binaire ; peut passer par **Distributed File Store (FCS)** |
| **Créer un job d'export** (génération async) | `POST …/dsdo:DerivedOutputJob` ou Exchange Job | POST | Nécessite **CSRF token** 3DSpace |
| **Suivre / récupérer** un job | `GET …/dsdo:DerivedOutputJob/{jobId}` | GET | Polling jusqu'à statut `Complete` |
| **Export As / CAD File Download** (UI) | Job « CAD Data Processor » | — | Flux UI ZIP ; distinct du REST dsdo pur |

### Prérequis HTTP pour appels 3DSpace (POST/PUT/DELETE)

Documenté dans le Postman Primer et COExperience :

1. **CAS login** (déjà implémenté via `ThreeDxCasPassportClient`)
2. **GET** `{ThreeDSpaceUrl}/resources/v1/application/CSRF` → token CSRF
3. Header **`SecurityContext`** sur chaque appel 3DSpace
4. Cookies de session (même `HttpClient` que CAS)

### Formats dérivés (Exchange vs Internal)

| Type | Téléchargeable | Exemples |
|------|----------------|----------|
| **Exchange Format** | Oui (avec rôle + responsabilité collab.) | PDF, STEP, STEP_AP203, STEP_AP214, DWG, DXF… |
| **Internal Format** | Non | XCADPivot, certains formats système |

Paramétrage côté admin : **Derived Format Management** (Collaborative Spaces Configuration Center).

### Distinction : Derived Output REST vs EIF / Export As

| Mécanisme | Usage |
|-----------|--------|
| **REST dsdo** | Lister / télécharger / générer derived outputs par API |
| **EIF (Enterprise Integration Framework)** | Intégration événementielle (promote, create…) — export asynchrone vers systèmes externes |
| **Export As (UI SOLIDWORKS)** | Job « CAD File Download » → ZIP avec natifs + derived |

Pour cet outil, le périmètre cible est **REST dsdo** (+ FedSearch pour résoudre l'objet par titre).

---

## 5. Décision d'implémentation

| Composant | Statut |
|-----------|--------|
| Solution + structure dossiers | ✅ Fait |
| Couche connexion (copie conforme au README) | ✅ Fait |
| CLI `--connection-only`, `--search-title` | ✅ Fait |
| `DerivedOutputClient` — list / download / export job | ⏸ **Stub** — en attente validation OpenAPI sur tenant |
| `ThreeDSpaceCsrfClient` (prêt pour POST dsdo) | ✅ Helper minimal |
| `dotnet build` sans erreur | ✅ |
| Tests intégration derived outputs | ⏸ Après accès doc `dsdo_v1` + Postman |

### Prochaines étapes recommandées

1. Se connecter à `media.3ds.com` avec un 3DEXPERIENCE ID
2. Ouvrir **CAADerivedOutputsWS/dsdo_v1.htm** et exporter l'OpenAPI JSON
3. Valider sur Postman (collection Cloud REST Services) :
   - Lister derived outputs d'un `physicalid` / `resourceid` connu
   - Télécharger un PDF/STEP existant
   - Créer un job de génération si absent
4. Implémenter `DerivedOutputClient` avec les chemins exacts
5. Option : wrapper NuGet `ds.enovia.dsdo` si compatible .NET 8 (à évaluer)

---

## 6. Structure du projet

```
C:\git\DerivedOutputDowload
├── DerivedOutputDownloader3DX.sln
├── src\DerivedOutputDownloader3DX\
│   ├── Program.cs
│   ├── DerivedOutputClient.cs          ← stub API derived outputs
│   ├── Configuration\
│   ├── Models\
│   ├── Services\Connection\          ← copie logique Reconciliation
│   └── Services\DerivedOutput\
├── docs\
│   ├── ANALYSE.md                      ← ce fichier
│   └── README.md
├── samples\appsettings.sample.json
└── output\                             ← fichiers téléchargés (futur)
```
