# Settings page

Route: `/settings`
Sidebar section: Footer (all users, but split into user preferences and admin settings)

## What this page is for

Two distinct audiences share this route:

- **All users** — personal preferences: theme, sidebar collapse default, display density
- **Admins only** — system-wide key/value settings that configure application behavior (stuck thresholds, lock duration, SharePoint credentials, etc.)

The page renders both sections for admins. Non-admins see only the personal preferences section; the system settings section is hidden entirely (not greyed out).

## Schema

System settings live in the `AppSetting` table — a simple key/value store with a description column so each key is self-documenting in the UI.

```
AppSetting
  AppSettingId    int IDENTITY PK
  Key             nvarchar(100) UNIQUE   -- e.g. 'StuckThreshold.Default'
  Value           nvarchar(1000) NULL    -- all values stored as strings; app layer parses
  Description     nvarchar(500) NULL     -- shown as helper text in the settings UI
  IsSecret        bit                    -- if 1, value is masked (••••••) in display
  UpdatedAtUtc    datetime2(3)
  UpdatedByUserId bigint FK User
  RowVersion      rowversion
```

User preferences (theme, density, sidebar state) are stored in a separate `UserPreference` table (Key/Value per UserId) or as a JSON blob column on `User`. Either works; the JSON blob on `User` is simpler for v1 since the preference set is small and doesn't need to be queried independently.

## Seeded system settings

These rows are inserted at deployment and must exist for the application to function:

| Key | Default value | Description |
|---|---|---|
| `StuckThreshold.Default` | `24` | Hours before a workstream at any stage is flagged stuck in Active Workflows. Per-stage `StuckThresholdHours` overrides take precedence. |
| `Lock.DurationMinutes` | `15` | Minutes before an inactive lock auto-expires. The Hangfire sweep runs every 2 minutes. |
| `Period.CloseConfirmPhrase` | `close period` | Phrase admin must type verbatim to confirm closing a period. Case-insensitive match. |
| `SharePoint.TenantId` | *(null)* | Entra tenant ID (GUID) for Microsoft Graph authentication. |
| `SharePoint.ClientId` | *(null)* | App registration client ID (GUID). Requires `Sites.Selected` on the target site. |
| `SharePoint.ClientSecret` | *(null)* | App registration client secret. `IsSecret = 1` — masked in UI. |
| `SharePoint.SiteId` | *(null)* | SharePoint site ID. Obtain via Graph: `GET /sites/{hostname}:/{path}` |
| `SharePoint.DriveId` | *(null)* | Document library drive ID. Obtain via Graph: `GET /sites/{siteId}/drives` |

SharePoint settings are null at deployment until an admin configures them. File upload operations will fail gracefully with a clear error ("SharePoint not configured — contact your admin") until all four SharePoint keys are set.

The only value that lives outside this table is the database connection string, which is in `appsettings.json` (or an environment variable / Azure Key Vault reference). Everything else the app needs to run is in `AppSetting`.

## Layout

### Personal preferences section (all users)

A simple card with a handful of toggles and dropdowns. No save button — changes apply immediately (optimistic update + background persist).

Fields:
- **Theme** — System / Light / Dark (segmented control)
- **Sidebar default** — Expanded / Collapsed (segmented control; the sidebar also remembers its last-used state per session, but this sets the default on first load)
- **Display density** — Comfortable / Compact (affects table row height and card padding throughout the app)

These preferences are stored per-user and applied on every page load.

### System settings section (admin only)

A grouped key/value editor. Keys are grouped by prefix:

**General**
- `StuckThreshold.Default`
- `Lock.DurationMinutes`
- `Period.CloseConfirmPhrase`

**SharePoint / Microsoft Graph**
- `SharePoint.TenantId`
- `SharePoint.ClientId`
- `SharePoint.ClientSecret` (masked)
- `SharePoint.SiteId`
- `SharePoint.DriveId`

Each row shows:
- Key name (monospace, read-only — keys are not user-editable, only values are)
- Description (muted, smaller text — pulled from `AppSetting.Description`)
- Value input (text field; secret fields show a password-style input with a show/hide toggle)
- Last updated (relative time + user name)

A **Save** button per group (not per row) — changes within a group are batched and saved together. This prevents half-configured states (e.g., saving TenantId but not ClientId).

On save, all changed rows in the group write `AuditEvent` rows:

```
Action: AppSettingUpdated
TargetTable: AppSetting
TargetId: AppSettingId
BeforeJson: { "Key": "SharePoint.ClientId", "Value": "<prior value>" }
AfterJson:  { "Key": "SharePoint.ClientId", "Value": "<new value>" }
Notes: (null)
```

Secret values (`IsSecret = 1`) have their actual value replaced with `"[secret]"` in `BeforeJson`/`AfterJson` — the audit log records that a change happened, but not what the secret was.

### SharePoint connection test

Below the SharePoint group, a **Test connection** button. It triggers a server-side call that:
1. Reads the four SharePoint settings from `AppSetting`
2. Attempts `GET /sites/{SiteId}/drives/{DriveId}` via Microsoft Graph
3. Returns success ("Connected — Contoso Accounting / Close Documents") or failure with the Graph error message

If any SharePoint setting is null, the button is disabled with a tooltip "Configure all SharePoint settings first."

This is the fastest feedback loop for credential issues during initial setup and after credential rotation.

## Audit events

| Action | Notes |
|---|---|
| `AppSettingUpdated` | One event per changed key per save. Secret values masked in JSON. |

User preference changes are not audited (low-stakes personal settings).

## Visual details

- The two sections are separated by a clear visual divider with a section label ("System settings — admin only" in a small, muted header)
- Secret fields use `<input type="password">` with a show/hide eye icon button (Tabler `ti-eye` / `ti-eye-off`)
- The "Test connection" button is secondary style with a plug icon (`ti-plug`); success state shows a green checkmark inline; failure shows the error message in a red inline block below the button
- Keys are never shown with edit controls — the set of keys is fixed at deployment. Adding new keys requires a migration, not a UI action. This prevents accidental key proliferation.
- Null values display as an empty input with placeholder "Not configured" in muted italic
