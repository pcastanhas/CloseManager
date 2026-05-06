# Close Manager

Internal accounting work management system for managing the monthly close cycle across many entities.

## Context

In-house corporate accounting team:

- ~12 staff accountants, each handling 8–12 entities per month (~100+ entity-closes per period)
- Multiple entity types (Real Estate Assets, Investment Funds, Holding Cos, Operating Cos), each with its own workflow
- Review routing varies by workstream type (Treasury, Asset Manager, Senior, CFO, etc.)
- SOX-relevant: every state change must be auditable

## Goals

- Make review bottlenecks visible at a glance across the portfolio
- Replace email/Slack-based review handoffs with structured workflow
- Capture review evidence (checklists, comments, file versions) in one durable system
- Provide an audit trail an external auditor can navigate

## Status

Early design phase. This repo currently contains design documentation and a proposed schema. Implementation has not begun.

## Repository structure

```
docs/
├── design/        UI/UX design notes and rationale
├── schema/        SQL Server schema and lifecycle walkthroughs
└── mockups/       Notes on the visual mockups produced during design
```

## Tech stack (planned)

- ASP.NET Core 9 + Blazor Server
- SQL Server (with EF Core 9)
- Microsoft Graph (SharePoint file storage)
- Entra ID for SSO
- Hangfire for background jobs
- MudBlazor for UI components

See `docs/design/06-tech-stack.md` for rationale.

## Design principles

A few principles that guided the design and should be preserved as the system evolves:

- **The close is the organizing concept.** Not tasks, not tickets — entity-period-workstream is the central unit.
- **Approval is structural.** The Approve button is locked until all checklist items are resolved. Approval is the consequence of completing verification, not a separate act of trust.
- **Comments live inside checklist items.** No floating discussion thread. Conversation stays attached to the specific check it's about.
- **Aging is passive.** The system surfaces stuck items by color and position; reviewers don't have to hunt for bottlenecks.
- **Templates are versioned.** Workflow changes are prospective; in-flight closes keep the version they started with.
- **Anyone with the right role can claim a workstream.** No explicit claim — just a lock for "I'm editing now."
- **Every state change writes an audit event.** The audit trail is non-bypassable.
