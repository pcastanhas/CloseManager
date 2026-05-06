# Phase 1 — Getting started

## Prerequisites

- .NET 8 SDK (`dotnet --version` should show 8.x)
- SQL Server (local or Docker: `docker run -e ACCEPT_EULA=Y -e SA_PASSWORD=YourPass123! -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest`)
- An Entra ID app registration (see below)

> **Note:** The tech stack doc specifies .NET 9. The solution targets `net8.0` (LTS) because .NET 9 wasn't available in the build environment. Upgrade the `<TargetFramework>` in both `.csproj` files to `net9.0` once you're ready — no other changes needed.

---

## 1. Entra app registration

In the Azure Portal:

1. **App registrations → New registration**
   - Name: `CloseManager`
   - Redirect URI: `https://localhost:5001/signin-oidc` (Web platform)

2. **Authentication tab**
   - Front-channel logout URL: `https://localhost:5001/signout-oidc`
   - Check: ID tokens, Access tokens

3. **Certificates & secrets → New client secret** — copy the value immediately

4. **Token configuration → Add groups claim** → Security groups → Group ID
   (This puts group membership into the token so the admin check works)

5. Copy from the Overview page:
   - **Application (client) ID** → `AzureAd:ClientId`
   - **Directory (tenant) ID** → `AzureAd:TenantId`

6. Create an Entra security group for admins. Copy its **Object ID** → `AdminGroupId`

---

## 2. Configure appsettings

Edit `CloseManager.Web/appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=CloseManager_Dev;Trusted_Connection=True;TrustServerCertificate=True"
  },
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "Domain": "yourdomain.onmicrosoft.com",
    "TenantId": "paste-your-tenant-id",
    "ClientId": "paste-your-client-id",
    "ClientSecret": "paste-your-client-secret",
    "CallbackPath": "/signin-oidc"
  },
  "AdminGroupId": "paste-your-admin-group-object-id"
}
```

> **Never commit real secrets.** Add `appsettings.Development.json` to `.gitignore` or use user secrets:
> ```
> dotnet user-secrets init --project CloseManager.Web
> dotnet user-secrets set "AzureAd:ClientSecret" "your-secret" --project CloseManager.Web
> ```

---

## 3. Create the database and Phase 1 table

Phase 2 adds the full EF Core migration. For Phase 1, just the `User` table is needed.
Run this against your SQL Server:

```sql
CREATE DATABASE CloseManager_Dev;
GO

USE CloseManager_Dev;
GO

CREATE TABLE [User] (
    UserId          bigint IDENTITY PRIMARY KEY,
    EntraObjectId   uniqueidentifier NOT NULL UNIQUE,
    Upn             nvarchar(256) NOT NULL,
    DisplayName     nvarchar(200) NOT NULL,
    IsActive        bit NOT NULL DEFAULT 1,
    LastSeenUtc     datetime2(3) NULL,
    CreatedAtUtc    datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    IsDeleted       bit NOT NULL DEFAULT 0,
    DeletedAtUtc    datetime2(3) NULL,
    DeletedByUserId bigint NULL,
    RowVersion      rowversion NOT NULL
);
GO
```

> Phase 2 replaces this manual step with `dotnet ef database update`.

---

## 4. Restore and run

```bash
cd CloseManager
dotnet restore
dotnet run --project CloseManager.Web
```

Navigate to `https://localhost:5001`. You should be redirected to the Entra login page.

---

## 5. Verify Phase 1 is done

After signing in:

- [ ] Your display name appears in the sidebar identity card (bottom left)
- [ ] Your UPN appears below your name
- [ ] Admin users see "Admin" chip and the Configuration + Operations nav sections
- [ ] Non-admin users see only Dashboard, Work items, My history, and Settings
- [ ] All nav links route to the correct placeholder pages (no 404s)
- [ ] After sign-in, a `User` row exists in the DB with your `EntraObjectId` and `DisplayName`
- [ ] Serilog writes structured logs to `logs/closemanager-YYYYMMDD.log`

---

## 6. Run the tests

```bash
dotnet test CloseManager.Tests
```

Phase 1 tests are unit tests only (no DB required). All 4 should pass.

---

## What's next

Phase 2 adds the full EF Core schema, all migrations, and seed data. You'll run `dotnet ef database update` instead of the manual SQL above.
