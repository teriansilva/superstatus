# Contributing to SuperStatus

Thanks for your interest in SuperStatus. This is a short guide; the
architecture lives in [`docs/architecture.md`](docs/architecture.md).

## Stack

.NET 9 · .NET Aspire 9.4 (local orchestration) · Blazor Server + MudBlazor ·
ASP.NET Core minimal APIs · OpenIddict on ASP.NET Identity · EF Core 9 →
PostgreSQL · MSTest.

## Getting set up

```bash
dotnet restore SuperStatus.sln
dotnet build SuperStatus.sln
dotnet test SuperStatus.sln                 # full suite

# Run the whole stack locally (Postgres + all services) via Aspire:
dotnet run --project SuperStatus.AppHost
```

To run the container stack instead, see the
[Quickstart](README.md#quickstart-local-trial).

## Tests ship with the change

Anything more than a trivial fix should come with tests in
`SuperStatus.Tests/` — a happy path, a failure path, and a regression guard
where it makes sense.

## Database changes

Schema changes go through EF Core migrations under
`SuperStatus.Data/Migrations/`. Name the migration in your PR description and
call out any data-loss risk (column drops, type changes, non-nullable adds
without a default).

## Pull requests

- Keep PRs focused; describe what changed and why.
- Reference the issue you're closing.
- Make sure the build and tests are green before you open it.
- By contributing, you agree your contributions are licensed under the
  project's [Business Source License 1.1](LICENSE), and you certify the
  [Developer Certificate of Origin](https://developercertificate.org/) by adding
  a `Signed-off-by: Your Name <you@example.com>` line to each commit (`git commit
  -s`). This confirms you wrote the change (or have the right to submit it) and
  lets the maintainer relicense the project as needed (e.g. the BSL Change Date
  conversion to Apache-2.0).

## Reporting security issues

Please **don't** open a public issue for security problems — follow
[`SECURITY.md`](SECURITY.md).
