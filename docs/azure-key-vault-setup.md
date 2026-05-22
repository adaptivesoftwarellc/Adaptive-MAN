# Azure Key Vault Setup

Phase 2 of [DEVELOPMENT_PLAN.md](../DEVELOPMENT_PLAN.md). The Observability API loads its secrets
from a per-environment Key Vault via Managed Identity in deployed environments. No
secrets live in committed files.

## Vaults

One dedicated vault per environment to limit blast radius:

| Vault                       | Environment   | Soft-delete | Purge protection |
|-----------------------------|---------------|-------------|------------------|
| `kv-observability-dev`      | Development   | yes         | not enabled      |
| `kv-observability-uat`      | UAT           | yes         | recommended      |
| `kv-observability-prod`     | Production    | yes         | **required**     |

> Earlier in Phase 2 the placeholder secrets temporarily lived in the shared
> `AdaptiveToolsKeyVault`. The dedicated `kv-observability-dev` replaces that
> arrangement; see [`docs/azure-provisioning-runbook.md`](azure-provisioning-runbook.md)
> for the actual provisioning commands.

## Required secrets per vault

| Secret name (in vault)        | Bound config key                      | Purpose                              |
|-------------------------------|---------------------------------------|--------------------------------------|
| `ObservabilityDbConnection`   | `ConnectionStrings:ObservabilityDb`   | Azure SQL connection string          |
| `ApiKeyHashPepper`            | `Observability:ApiKeyHashPepper`      | Pepper for SHA-256 of API keys       |
| `JwtSigningKey`               | `Observability:JwtSigningKey`         | Dashboard auth (Phase 8)             |
| `EncryptionKey`               | `Observability:EncryptionKey`         | Reserved for field-level encryption  |

The mapping is defined in [`backend/src/Observability.Api/Configuration/KeyVaultConfiguration.cs`](../backend/src/Observability.Api/Configuration/KeyVaultConfiguration.cs).
Other secrets follow the standard `Section--Key` → `Section:Key` convention.

In `Development`, secrets fall back to user secrets / `appsettings.Development.json` when
`KeyVault:Uri` is empty. In UAT/Prod the API **fails fast at startup** if `KeyVault:Uri`
is missing or any required secret is unbound.

## Provisioning a fresh environment

End-to-end `az` CLI commands live in [`docs/azure-provisioning-runbook.md`](azure-provisioning-runbook.md). The shape:

1. Create the dedicated vault (region: same as the App Service; SKU `standard`; **RBAC**, not access policies; soft-delete 90d; purge protection on for `prod`).
2. Create a **user-assigned** managed identity (Brandon's decision 2026-05-02 — one shared App Service Plan, per-env App Service instances). Attach it to the App Service via `az webapp identity assign --identities <mi-id>`.
3. Grant the MI `Key Vault Secrets User` on its same-environment vault. Do not grant cross-environment access.
4. Set `AZURE_CLIENT_ID` app setting to the MI's clientId so `DefaultAzureCredential` picks the right identity when multiple are attachable.
5. Add the four required secrets (see table above) using their exact names.
6. Set `KeyVault:Uri` on the App Service as `KeyVault__Uri = https://kv-observability-{env}.vault.azure.net/`.
7. Set `ASPNETCORE_ENVIRONMENT` to `UAT` or `Production` as appropriate.
8. Deploy. On startup the API:
   - Loads the vault into configuration.
   - Validates each required secret resolves to a non-empty string.
   - Logs `Critical` and refuses to start if any are missing in non-Development.

## Identity flow

```
App Service ──(managed identity, MSAL via DefaultAzureCredential)──▶ Key Vault
       │                                                                │
       │   GET /secrets (RBAC: Key Vault Secrets User)                  │
       │◀──────── secret values for ObservabilityDb, pepper, ... ───────┘
       │
       ▼
ASP.NET Core IConfiguration (overlaid on appsettings + env vars)
       │
       ▼
DI: ObservabilityDbContext, ApiKeyHasher, ...
```

`DefaultAzureCredential` resolves to the user-assigned MI in App Service (selected via the
`AZURE_CLIENT_ID` app setting), to the developer's Azure CLI/VS login locally, and to
workload identity in container scenarios. No code change is needed across environments.

## Local development

Either:

- Leave `KeyVault:Uri` empty in `appsettings.Development.json`. Set
  `Observability:ApiKeyHashPepper` and `ConnectionStrings:ObservabilityDb` directly
  (committed defaults already do so for Docker). This is the default path.
- Or set `KeyVault:Uri` to a personal `kv-observability-dev` and `az login` so
  `DefaultAzureCredential` picks up your CLI token.

## Rotation runbook

> Run rotations in **UAT first**, then Prod. Always have a rollback (the prior secret
> version stays available in Key Vault for `soft-delete-retention` days).

### Rotate `ObservabilityDbConnection` (DB password)

1. In Azure SQL: change the SQL login's password (or rotate the AAD-auth credential).
2. In Key Vault: add a **new version** of `ObservabilityDbConnection` with the updated
   password. Do not delete the prior version.
3. Restart the App Service (or hit the `/health` endpoint after waiting one
   `KeyVault` refresh interval). New connections use the new credential immediately.
4. Verify: `/health` is `200`; ingestion endpoints return `202` for a smoke event.
5. After 24h with no errors, disable the prior secret version.

### Rotate `ApiKeyHashPepper`

> ⚠️ Pepper rotation invalidates **every existing API key** because the stored hash
> includes the pepper. Treat this as a key re-issue event.

1. Schedule a maintenance window. Notify all onboarded apps.
2. Generate a new high-entropy pepper (≥256 bits, base64).
3. Add a new version of `ApiKeyHashPepper` in the target vault.
4. Re-issue every active API key (via the dashboard's admin/apps view, Phase 3+):
   - Create a new key per (Application, Environment, KeyType) using the new pepper.
   - Distribute to onboarded apps; have them deploy the new key.
   - Revoke the old keys after a grace window (default 24h in UAT, 72h in Prod).
5. Verify ingestion: zero `401`s after the grace window expires.
6. Disable the prior pepper version in Key Vault.

### Rotate `JwtSigningKey` (Phase 8 dashboard auth)

1. Add a new version of `JwtSigningKey`.
2. Roll the App Service so new tokens sign with the new key.
3. Existing tokens will be rejected on next refresh — users see one re-login.
4. Disable the prior version after the longest valid token TTL has passed.

### Rotate `EncryptionKey`

Out of scope until field-level encryption ships. When it does: the rotation runbook must
include a backfill step that re-encrypts existing rows with the new key before disabling
the prior version.

## Audit + alerting

- Enable **Key Vault diagnostic logs** to a Log Analytics workspace per environment.
- Alert on:
  - Any `Forbidden` or `Unauthorized` response on the vault.
  - Secret reads from a principal other than the App Service MI.
  - Any `Delete` on `kv-observability-prod`.

## Acceptance checklist (Issue 2.5)

- [x] Step-by-step fresh-env setup (see [`azure-provisioning-runbook.md`](azure-provisioning-runbook.md))
- [x] Rotation runbooks (DB password, hash pepper, JWT key)
- [x] Identity + secret flow documented
- [x] IaC tool decision — stay on `az` CLI scripts (Brandon, 2026-04-30)
- [ ] Diagnostic logs + alert rules configured (Phase 8 cross-cut)
