# Migrations

## How to apply the schema

Since EF Core migrations are generated against a live database, run these commands locally after `dotnet restore`:

```bash
# From the solution root
dotnet ef migrations add InitialSchema --project CloseManager.Web --output-dir Data/Migrations
dotnet ef migrations add SeedData --project CloseManager.Web --output-dir Data/Migrations
dotnet ef database update --project CloseManager.Web
```

## What the migrations cover

**InitialSchema** — all tables from `docs/schema/schema.sql`:
- User, EntityType, Entity, Role, EntityRoleAssignment
- WorkflowTemplate, WorkstreamDef, WorkstreamDefStage, WorkstreamDefChecklistItem
- ClosePeriod, Workstream, WorkstreamStage
- WorkstreamFile, ChecklistItem, Comment, CommentAttachment
- AuditEvent, AppSetting

Two indexes that EF can't express natively are added as raw SQL in the migration — 
add them manually after `ef migrations add InitialSchema` by editing the generated migration file:

```csharp
// In the Up() method, add after the table creates:
migrationBuilder.Sql(@"
    CREATE UNIQUE INDEX UX_WorkflowTemplate_Current
        ON WorkflowTemplate(EntityTypeId)
        WHERE IsCurrent = 1 AND IsDeleted = 0;
");
```

**SeedData** — reference data required before the app is usable:

```csharp
// EntityType seed
migrationBuilder.InsertData("EntityType", 
    new[] { "Code", "Name", "IsActive", "CreatedAtUtc" },
    new object[] { "RealEstateAsset", "Real estate asset", true, DateTime.UtcNow });
// ... InvestmentFund, HoldingCo, OperatingCo

// Role seed
migrationBuilder.InsertData("Role",
    new[] { "Code", "Name", "IsActive", "CreatedAtUtc" },
    new object[] { "Preparer", "Preparer", true, DateTime.UtcNow });
// ... TreasuryRE, TreasuryInv, AssetMgr, Senior, CFO

// AppSetting seed — see schema.sql INSERT statements for all 8 rows
migrationBuilder.InsertData("AppSetting",
    new[] { "Key", "Value", "Description", "IsSecret", "UpdatedAtUtc" },
    new object[] { "StuckThreshold.Default", "24", "...", false, DateTime.UtcNow });
// ... 7 more rows
```

## Phase 2 test

After `dotnet ef database update`:

```sql
-- Verify table count
SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES 
WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_SCHEMA = 'dbo';
-- Expected: 19

-- Verify AppSetting seed
SELECT [Key], [Value] FROM AppSetting ORDER BY [Key];
-- Expected: 8 rows

-- Verify EntityType seed  
SELECT Code FROM EntityType;
-- Expected: HoldingCo, InvestmentFund, OperatingCo, RealEstateAsset

-- Verify Role seed
SELECT Code FROM [Role];
-- Expected: AssetMgr, CFO, Preparer, Senior, TreasuryInv, TreasuryRE
```

## Rollback

```bash
dotnet ef database update 0 --project CloseManager.Web  # drops all tables
dotnet ef database update --project CloseManager.Web    # re-applies
```
