# Phase 1 — Setup instructions

## Prerequisites

- .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8)
- SQL Server or LocalDB (for Phase 2+; Phase 1 tests use in-memory)
- An Entra ID (Azure AD) app registration

## Entra app registration

1. In the Azure portal, register a new app under **App registrations**
2. Set the redirect URI to `https://localhost:7200/signin-oidc` (Web platform)
3. Under **Certificates & secrets**, create a client secret
4. Note the **Tenant ID**, **Client ID**, and **Client secret**
5. Create a security group for admins; note its **Object ID**
6. Under **Token configuration**, add the `groups` optional claim (ID token)

## Configuration

Copy the values into `appsettings.Development.json`:

```json
{
  "AzureAd": {
    "TenantId": "your-tenant-id",
    "ClientId": "your-client-id",
    "ClientSecret": "your-client-secret",
    "Domain": "yourdomain.onmicrosoft.com",
    "AdminGroupObjectId": "your-admin-group-object-id"
  }
}
```

**Never commit secrets to git.** Use `dotnet user-secrets` for local development:

```bash
cd CloseManager.Web
dotnet user-secrets set "AzureAd:ClientSecret" "your-secret"
```

## Run

```bash
dotnet restore
dotnet run --project CloseManager.Web
```

Navigate to https://localhost:7200. You should be redirected to the Entra login page.
After signing in, you should see the app shell with the sidebar and your display name.

## Tests

```bash
dotnet test CloseManager.Tests
```

Phase 1 tests use an in-memory EF provider — no database needed.

## Done when

- [ ] Sign in with an Entra account → app shell appears with your display name in the top bar
- [ ] Sign out → redirected to Entra
- [ ] Unauthenticated request to `/` → redirected to Entra login
- [ ] `dotnet test` → 4 UserSyncServiceTests pass
- [ ] Database: after first sign-in, a `[User]` row exists with your `EntraObjectId` and `DisplayName`

## Note on .NET version

The tech stack doc targets .NET 9. This solution targets .NET 8 LTS (the highest version
available at scaffold time). Upgrading to .NET 9 when it is available requires only changing
`<TargetFramework>net8.0</TargetFramework>` to `net9.0` in both `.csproj` files and updating
package versions — no code changes expected.
