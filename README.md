# adaptive-observability

Internal analytics, error-tracking, and session-timeline platform that onboards Adaptive's internal apps under strict PHI/PII rules. First tenant is the Wound Management System (WMSSite + WMSAPI); the event/identity/privacy contracts trace their shape to the original PostHog Phase 1 catalog.

## Status

Phase 0 / Phase 1 scaffolding. See [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md) for the full roadmap.

## Quick start

```bash
docker compose up
```

Brings up SQL Server, the Observability API (`http://localhost:8080`), and the dashboard (`http://localhost:5173`).

Health check: `GET http://localhost:8080/health`

## Layout

```
backend/   ASP.NET Core 8 solution (Api, Application, Domain, Infrastructure, Worker)
frontend/  React + Vite + TS + Tailwind dashboard (see frontend/README.md)
packages/  observability-client-js, observability-client-dotnet (Phase 4)
docs/      architecture, privacy, event catalog, identity, route normalization
```

## Documentation

- [Architecture](docs/architecture.md)
- [Privacy rules](docs/privacy-rules.md) — forbidden vs. allowed fields
- [Event catalog](docs/event-catalog.md)
- [Identity rules](docs/identity-rules.md)
- [Route normalization](docs/route-normalization.md)
- [API contract](docs/api-contract.md)

## Development

See [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md). Phase 0 (foundation) and Phase 1 (backend ingestion MVP) are the current focus.

The dashboard runs with built-in **demo data** by default in dev (no backend required) — see
[frontend/README.md](frontend/README.md) for the mock-mode toggle and conventions.
