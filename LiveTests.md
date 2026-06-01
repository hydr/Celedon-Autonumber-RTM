# Live Dataverse Tests

This repo runs two test suites:

| Suite              | Where                                | When                                         |
|--------------------|--------------------------------------|----------------------------------------------|
| In-process tests   | `AutoNumber.Tests/IntegrationTests.cs` | every push / PR (workflow `build`)         |
| Live Dataverse tests | `AutoNumber.Tests/LiveDataverseTests.cs` | manual via workflow `live-tests`         |

Live tests connect to a real Dataverse environment, create `cel_autonumber`
records, exercise the deployed plugins (`CreateAutoNumber`, `GetNextAutoNumber`,
`DeleteAutoNumber`), and assert on the resulting `sdkmessageprocessingstep`
filtering attributes plus an end-to-end `account` autonumber assignment. Each
test cleans up after itself.

Authentication is **passwordless** via GitHub OIDC + an Azure AD app
registration with a Federated Credential. No client secret is stored anywhere.

---

## One-time setup

### Prerequisites

- A Dataverse environment with the AutoNumber solution **already imported**
  (managed or unmanaged). This registers the `cel_autonumber` entity and the
  `CreateAutoNumber` / `DeleteAutoNumber` plugin steps. The `live-tests`
  workflow then **redeploys the freshly built `Celedon.AutoNumber` assembly**
  (`scripts/Deploy-PluginAssembly.ps1`) so the org runs exactly the code under
  test — you don't have to manually repackage the solution per branch.
- Owner / Application Administrator rights in the Azure AD tenant.
- System Administrator rights in the Dataverse environment.
- Repo Admin rights to set repository Variables and create Environments.

### 1. Register an Azure AD application

1. Azure Portal → **Microsoft Entra ID** → **App registrations** → **New registration**
2. Name: e.g. `celedon-autonumber-ci`
3. Supported account types: *Accounts in this organizational directory only*
4. Redirect URI: leave empty
5. After creation, note **Application (client) ID** and **Directory (tenant) ID**

### 2. Add a Federated Credential mapping GitHub → the app

In the App registration: **Certificates & secrets** → **Federated credentials**
→ **Add credential**.

Recommended setup uses a **GitHub Environment** so you can tightly control which
branches are allowed to run live tests:

- Federated credential scenario: **GitHub Actions deploying Azure resources**
- Organization: `hydr`
- Repository: `Celedon-Autonumber-RTM`
- Entity type: **Environment**
- Environment name: `live-tests`
- Name: `github-live-tests`

Resulting subject:

```
repo:hydr/Celedon-Autonumber-RTM:environment:live-tests
```

If you'd rather not use environments, you can pick `Branch` instead and create
one credential per branch you want to allow (e.g. `main`).

### 3. Create the GitHub Environment

GitHub repo → **Settings** → **Environments** → **New environment** →
`live-tests`.

Recommended:

- **Required reviewers**: yourself (so live runs always require a manual click)
- **Deployment branches**: only protected branches (or specific patterns)

### 4. Create an Application User in Dataverse

1. **Power Platform Admin Center** → select environment → **Settings** →
   **Users + permissions** → **Application users** → **+ New app user**
2. **App**: pick the registration from step 1 (search by client ID)
3. **Business unit**: root
4. **Security roles**: assign roles that allow:
   - Read / Create / Write / Delete on `cel_autonumber`
   - Read / Create / Write / Delete on `account`
   - Read on `sdkmessageprocessingstep`, `sdkmessageprocessingstepimage`,
     `sdkmessage`, `sdkmessagefilter`
   - **Create / Write on `pluginassembly` and `plugintype`** — the workflow
     redeploys the built assembly via `scripts/Deploy-PluginAssembly.ps1`.

   The built-in **System Administrator** role is the simplest catch-all (plugin
   assembly registration needs the ISV-extension privileges it grants).
   **System Customizer** + Account privileges works only if you don't auto-deploy
   the assembly. For a hardened setup, create a dedicated role with exactly the
   privileges above.

### 5. Configure repository variables

GitHub repo → **Settings** → **Secrets and variables** → **Actions** →
**Variables** tab → **New repository variable**:

| Name              | Value (example)                                      |
|-------------------|------------------------------------------------------|
| `AZURE_CLIENT_ID` | `00000000-0000-0000-0000-000000000000` (from step 1) |
| `AZURE_TENANT_ID` | `11111111-1111-1111-1111-111111111111` (from step 1) |
| `DATAVERSE_URL`   | `https://orgxxxxxxxx.crm4.dynamics.com`              |

These are **variables**, not secrets — none of them is sensitive on its own.
The actual authentication comes from the OIDC token GitHub mints at runtime,
which the Federated Credential validates.

---

## Running

### From GitHub Actions

GitHub repo → **Actions** → **live-tests** → **Run workflow** → pick the branch.
You'll be prompted to approve the deployment to the `live-tests` environment.

The workflow will:

1. Build the solution.
2. Acquire an OIDC JWT from GitHub.
3. Use `azure/login@v2` with the Federated Credential to obtain an Azure AD token.
4. Use `az account get-access-token --resource $DATAVERSE_URL` to exchange that
   token for a Dataverse-scoped bearer token.
5. Deploy the freshly built `Celedon.AutoNumber` assembly into the org via
   `scripts/Deploy-PluginAssembly.ps1` (updates the existing registration in
   place), so the tests run against this build's code.
6. Run only `[Category("Live")]` NUnit tests against your environment.
7. Post a summary to the run's job summary, attach `TestResult.xml` as artifact,
   fail the job if any tests failed.

### Locally

```pwsh
az login
$env:DATAVERSE_URL   = "https://orgxxxxxxxx.crm4.dynamics.com"
$env:DATAVERSE_TOKEN = (az account get-access-token --resource $env:DATAVERSE_URL --query accessToken -o tsv)

# from the repo root, after a Release build — deploy this build's assembly first:
.\scripts\Deploy-PluginAssembly.ps1 `
    -AssemblyPath "AutoNumber\bin\Release\Celedon.AutoNumber.dll" `
    -CrmUrl $env:DATAVERSE_URL -AccessToken $env:DATAVERSE_TOKEN

nunit3-console AutoNumber.Tests\bin\Release\AutoNumber.Tests.dll --where "cat == Live"
```

If `DATAVERSE_URL` or `DATAVERSE_TOKEN` is unset, all live tests are marked
*Inconclusive* and the rest of the suite continues normally.

---

## Troubleshooting

| Symptom                                                                | Likely cause                                                                  |
|------------------------------------------------------------------------|-------------------------------------------------------------------------------|
| `AADSTS70021` *No matching federated identity record found*            | Federated Credential subject doesn't match the workflow context (env / branch).|
| `Could not connect to Dataverse: 0x80048306` / *user is not enabled*   | Application User wasn't created in Dataverse, or has no security role.        |
| `Insufficient privileges` on `sdkmessageprocessingstep`                | Application User's role lacks read on plugin-step entities.                   |
| `Plugin step ... did not appear within 90s`                            | AutoNumber solution isn't deployed in the env, or `CreateAutoNumber` is unregistered.|
| `Token validation failed`                                              | Token was acquired for the wrong resource. Double-check `DATAVERSE_URL`.      |

The Federated Credential subject must match the workflow exactly. Reference:
<https://docs.github.com/en/actions/deployment/security-hardening-your-deployments/about-security-hardening-with-openid-connect#example-subject-claims>.
