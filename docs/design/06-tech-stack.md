# Tech stack

## Recommended stack

- **ASP.NET Core 9** with **Blazor Server** for the UI
- **SQL Server** with **EF Core 9** (and Dapper for hot-path queries)
- **Microsoft Graph SDK** for SharePoint file operations
- **Microsoft.Identity.Web** for Entra SSO
- **Hangfire** for background jobs (lock expiry, period-open job)
- **MudBlazor** for UI components
- **FluentValidation** for DTO/request validation
- **Serilog** for operational logging (separate from the AuditEvent business audit trail)
- **xUnit** + **bUnit** + **Testcontainers** for testing

## Why Blazor Server

The UI we designed is interactive but fundamentally CRUD-shaped — reading and writing to SQL Server, calling Graph for SharePoint, rendering structured views. There's no graphics-heavy client computation, no offline mode, no public-facing performance pressure. That's the sweet spot for Blazor Server.

The wins:

- **Single language end-to-end.** UI components, validation, SQL access, Graph calls — all C#. No DTO duplication, no separate API surface.
- **SignalR is built in.** Real-time updates ("Maya resubmitted," "Sam locked this") are the default behavior of Blazor Server, not an extra integration.
- **Auth just works.** Microsoft.Identity.Web + Microsoft.Graph handle SSO, role claims, and Graph access in ~50 lines of Program.cs.
- **Performance is irrelevant at this scale.** Server resource cost (one SignalR circuit per user) and round-trip latency (every interaction hits the server) are the criticisms of Blazor Server. With ~12 concurrent users on the internal network, both are non-issues.

## What we're not using

- **Blazor WebAssembly.** WASM downloads the .NET runtime to the browser and can't reach SQL Server directly — you'd need a separate API layer, losing the "everything in one project" advantage. Better for offline apps; not this one.
- **A separate React/Vue front-end with Web API back-end.** Overkill. Adds API contracts, DTO mapping, JS build pipeline, separate deployment — all to render forms and tables. Save SPA architecture for apps that genuinely need it.
- **Razor Pages without component framework.** Workable but requires reinventing UI infrastructure that MudBlazor gives for free.

## Component library

MudBlazor is the recommended starting point. Free, complete (data grids, dialogs, drawers, date pickers, tabs), looks professional out of the box. The mockups translate cleanly to MudBlazor primitives.

Alternatives if budget allows: Telerik or Syncfusion. Both excellent, particularly for advanced data grids. Not necessary for v1.

## SharePoint integration

App-only auth via a registered Entra app. Grant `Sites.Selected` permission on the specific SharePoint site (not all of SharePoint). The site admin uses the Graph API to grant the app access to just that one site, which is the least-privilege configuration.

File operations:

- Upload: `PUT /drives/{driveId}/root:/{path}/{filename}:/content`
- Read URL: stored in `WorkstreamFile.SpWebUrl` for direct user clicks
- Replace = soft-delete the old `WorkstreamFile` row + insert new file in same SharePoint folder under a different filename, both in one DB transaction

Path convention: `/{entity.Code}/{period}/{workstream.Code}/{filename}`. Distinct files (no SharePoint versioning); the app manages versioning via `ReplacesFileId` chains.

## Background jobs (Hangfire)

Stores job state in SQL Server, has a built-in dashboard, integrates with ASP.NET Core DI. Jobs needed in v1:

- **Lock expiry sweep** — every minute or two, null out expired locks; write `LockExpired` audit events
- **Period open** — instantiate ClosePeriod + Workstream + ChecklistItem rows for an entire close. Long-running, transactional, audited.

Jobs likely needed later:

- Daily digest emails for users with stuck items
- Weekly retrospective metrics (avg round count, avg sitting time)
- SharePoint integrity check (file rows with no corresponding SharePoint item)

## Database access pattern

EF Core for most things — straightforward CRUD against the schema. Hot-path queries (heatmap, reviewer queue) drop down to raw SQL or Dapper for speed. Don't enforce one or the other globally; pick per-query.

Migrations: EF Core migrations checked into source control, applied via deployment pipeline. Never apply migrations manually in any environment.

## State change pattern: stored procedures

Every workstream state transition (Submit, OpenForReview, ApproveChecklistItem, Approve, Rebuild, etc.) should be implemented as a stored procedure that does the state change and the audit insert in one transaction. The application calls these procedures rather than issuing UPDATEs directly.

Why: makes the audit trail non-bypassable. A buggy code path that bypasses the procedure can't accidentally mutate state without an audit event. The validation rules ("primary file required," "all items approved before approve") live in SQL where they're enforced regardless of caller.

## Authentication and authorization

Entra ID SSO via Microsoft.Identity.Web. Map Entra group memberships to roles in the `Role` table — store the mapping in the database (not as hard-coded claims) so role assignments are auditable and changeable without touching Entra.

For "is this user an admin?" check: a separate Entra group whose membership is queried at sign-in and cached on the principal. Keep the admin group small and documented.

## Logging and audit

Two separate things, often confused:

- **Operational logging** (Serilog → file + Application Insights or SQL): request rate, error rate, slow queries, exceptions. The dev/ops view of system health.
- **Business audit trail** (the AuditEvent table): who did what to which workstream when. The auditor and forensics view.

Don't conflate these. Operational logs are noisy and rotated; the audit trail is durable and append-only.

## Testing

- **Unit tests** (xUnit) for business logic and validators.
- **Component tests** (bUnit) for Blazor components — render in isolation, simulate user interactions, assert on rendered HTML.
- **Integration tests** (Testcontainers + SQL Server image) for stored procedures and complex queries. Avoid the in-memory EF provider; it doesn't behave like SQL Server in ways that bite you on this kind of audit-tracked app.

## Deployment

Internal app — likely IIS on a Windows server, or a container in your existing Kubernetes/Azure infrastructure. Either way:

- Three environments: dev, staging, production
- Staging uses a copy of production schema (not data) and a small set of test entities
- Deployment is automated (GitHub Actions or Azure DevOps); no manual deploys to production
- Migrations apply automatically as part of deployment
- Rollback strategy documented from day one
