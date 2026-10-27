# ALDevToolbox.Tests

xUnit test project for AL Workbench. Established in Milestone 12 (see
`.design/completed-milestones.md`); the patterns here are the bar M13 onward
should copy rather than reinvent.

## Stack

- **xUnit v3** for the test runner. `[Fact]` for single cases, `[Theory]` +
  `[InlineData]` when a parameterised shape would otherwise duplicate setup.
  v3 runs on Microsoft.Testing.Platform rather than VSTest, which has two
  consequences worth knowing before you fight them:
  - The project is an `Exe` (`<OutputType>`) and `global.json` carries a
    `test.runner` section. Both are load-bearing — without them `dotnet test`
    fails with "Testing with VSTest target is no longer supported".
  - VSTest command-line options are gone. `dotnet test` with no flags still
    runs everything, but `--filter "FullyQualifiedName~X"` and
    `--logger "console;..."` now exit 5 having run nothing. To filter, build
    first and run the test executable directly — it takes xUnit's own
    `-namespace` / `-class` / `-method` options, which accept a leading and/or
    trailing `*`:

    ```
    dotnet build ALDevToolbox.slnx -c Debug
    ALDevToolbox.Tests/bin/Debug/net10.0/ALDevToolbox.Tests -namespace ALDevToolbox.Tests.Al -noLogo
    ALDevToolbox.Tests/bin/Debug/net10.0/ALDevToolbox.Tests -class "*SnapshotTests*" -noLogo
    ALDevToolbox.Tests/bin/Debug/net10.0/ALDevToolbox.Tests -method "*renders_the_form*" -noLogo
    ```

    (The `dotnet test -- --filter-class ...` form this file used to recommend
    does not work with the runner version we're on: it also exits 5 having run
    nothing.)
- **AwesomeAssertions** for the assertion DSL. Prefer `.Should().Be(...)`,
  `.Should().Contain(...)`, etc. over raw `Assert.Equal` so failures read like
  prose. It is the community fork of FluentAssertions, which moved to a
  non-commercial licence at v8; the fork stays on Apache-2.0 and keeps the same
  DSL, so anything written for FluentAssertions applies here unchanged.
- **Npgsql.EntityFrameworkCore.PostgreSQL** with a real Postgres instance for
  any test that touches the DB (Milestone P4.16). The test fixture
  (`TestDb`) creates a unique database per test class against a process-wide
  shared Postgres host. The host is either:
  - a runner-provided service container — when
    `ALDT_TEST_POSTGRES_CONNECTION` is set, `TestDb` reuses it. CI takes this
    path against a `postgres:18` service container.
  - a Testcontainers `postgres:18-alpine` started on first use — local-dev
    path. Requires Docker on the developer's machine.

  We deliberately do *not* use `Microsoft.EntityFrameworkCore.InMemory` — the
  SQL semantics diverge from real Postgres (constraint enforcement, jsonb
  conversions, timestamptz handling) and the milestone calls out matching
  production.

## Layout

| Folder            | What goes there                                                    |
|-------------------|--------------------------------------------------------------------|
| `Builders/`       | Small static builders that return entities pre-populated with sane |
|                   | defaults. Tests override only the fields they care about.          |
| `Infrastructure/` | Reusable test plumbing — currently just `TestDb`.                  |
| `Generation/`     | `GenerationService` tests (ID-range allocation, mustache).         |
| `Audit/`          | `AuditInterceptor` snapshot tests.                                 |
| `Toml/`           | `TemplateTomlMapper` round-trip tests.                             |
| `Validation/`     | `PlanValidationException` field-key surface tests.                 |
| `Dashboard/`      | `DashboardService` — the counts and queues behind `/admin` and `/`. |
| `Routing/`        | End-to-end endpoint inspection — boots the app via                  |
|                   | `WebApplicationFactory<Program>` and asserts the endpoint map is    |
|                   | unambiguous (catches `MapPost` / `@page` collisions at build time). |
| `Endpoints/`      | HTTP-layer behaviour for the minimal-API handlers in `Program.cs`   |
|                   | (antiforgery enforcement, anonymous → redirect, /site-admin 404).   |
|                   | Boots via the shared `EndpointFactory` in `Infrastructure/`.        |
| `Components/`     | bUnit smoke tests for the highest-risk `.razor` pages. Use          |
|                   | `Bunit.TestContext`; stub auth via `AddTestAuthorization()` and     |
|                   | register real services against `TestDb` rather than mocking a       |
|                   | single method (CLAUDE.md: no interfaces just for tests).            |

Those are the folders with a pattern worth spelling out. There are many more
— one per subsystem, mirroring the app's own layering — and the rest are what
their names say:

| Group                | Folders                                                              |
|----------------------|----------------------------------------------------------------------|
| Plumbing             | `Builders/`, `Infrastructure/`, `Fixtures/` (sample data files)       |
| Generation           | `Generation/`, `Templates/`, `Extensions/`, `Catalogue/`, `Toml/`, `Configuration/`, `Validation/` |
| Accounts and tenancy | `Auth/`, `Account/`, `OAuth/`, `Teams/`, `SiteAdmin/`, `Admin/`, `Audit/`, `Schema/` (tenant-filter and data-integrity invariants) |
| Object Explorer      | `ObjectExplorer/`, `Al/`, `Cal/`, `Diff/`                             |
| Translator           | `Translator/` (memory, suggestions, XLIFF writing), `Translations/` (XLIFF parsing and import), `Translation/` (machine-translation providers) |
| Other tools          | `Cookbook/`, `BcQuality/`, `Mcp/`, `Tools/`, `Dashboard/`, `GitHub/`  |
| UI and shell         | `Components/`, `Assets/` (stylesheet and rendered-markup invariants), `Icons/`, `Routing/`, `Endpoints/`, `Palette/` (the command palette's query parsing, ranking, endpoint, and the visibility harness every source must pass) |
| Operations           | `Migrations/`, `Storage/`, `Services/` (`BuildInfo`, `WorkerHeartbeat`), `Piper/` |

When you add a new test file, match the folder. Resist creating new
top-level folders for one-off tests — pick the closest existing bucket from
the full list above, not from the handful documented in detail. (The three
Translat* folders are what happens when you pick from a partial list: they
are a wart, not a pattern to copy.)

## Patterns

### Database tests

Use `TestDb`. It creates a fresh Postgres database for the test class against
the shared host, applies migrations, and hands out `AppDbContext` instances
bound to that database. The fixture drops the database on `Dispose`, so
each test class is isolated from every other.

```csharp
public sealed class MyServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Something_works()
    {
        await using var ctx = _db.NewContext();
        // ... arrange, act, assert
    }
}
```

For audit-interceptor tests use `TestDb.NewContextWithAudit(interceptor)`
so the same write path the application uses runs in the test.

Every fixture database holds three organisations, and `TestDb.OrgContext`
acts as an ordinary tenant unless a test says otherwise (#1441):

| Constant      | Id | What it is                                                        |
|---------------|----|-------------------------------------------------------------------|
| `DefaultOrgId`| 3  | An ordinary tenant, seeded the way signup creates one (platform files included). What tests act as by default. |
| `OtherOrgId`  | 2  | A second, bare tenant for cross-organisation isolation tests.     |
| `SystemOrgId` | 1  | The singleton system org the migration stamps. Act as it only to test system-org behaviour. |

The system org keeps the migration's name "Default" and slug `default`; the
default tenant is named "Tenant" with slug `tenant` (`TestDb.DefaultOrgSlug`).

To act as the system org, set `_db.OrgContext.CurrentOrganizationId =
TestDb.SystemOrgId` and seed its rows with that id. `OrgContext` has no
settable `IsSystemOrganization`: it is true exactly when the acting org is
`SystemOrgId`, a constant that stands in for the org row a real sign-in reads
the flag from, and `TestDbOrganizationsTests` pins that the two agree. So a
test cannot claim the system org is a tenant or the other way round.
Hand-built identities (`AmbientOrganizationContext`, `OrganizationIdentity`)
in a test that has a database should keep the same pairing.

`OrgContext` ignores `AmbientOrganizationScope`, unlike production code off a
request, so a worker or scheduler test that loops over organisations should
give it an off-request `HttpOrganizationContext` instead.

### Component tests over a database

A bUnit test whose page reads the database through a scoped `AppDbContext`
has to stop the page before the fixture drops the database, and a page that
polls or chains several queries is usually still busy when the test method
returns. Two things make that safe (`EnvironmentDetailTests` is the worked
example, from #895, #905 and #924):

- Tear the renderer down with `await _ctx.Renderer.DisposeAsync()`, not
  `DisposeComponentsAsync()`. bUnit's component-only dispose clears its list
  of root components right after posting the detach to the dispatcher, so
  when the dispatcher is busy with one of the page's loads at that moment the
  posted work finds nothing to detach and the page lives on. The renderer's
  own dispose walks every component on the dispatcher, behind whatever the
  page is doing, and waits for each component's `DisposeAsync`.
- Give the page a `DisposeAsync` that cancels its reads and waits for the
  last call to finish, so the renderer's dispose returning means nothing of
  the page's is still on the wire. `EnvironmentDetail.razor`'s `Read` /
  `Write` helpers are the shape.

Then `_db.WaitForQueriesToSettle()`, dispose the context's services, and only
then `_db.Dispose()`. The settle (`InFlightCommandTracker`) is the second
line, not the first: it covers a command a dying call is still disposing.

### Builders

Builders return entities ready for `_db.NewContext().Add(...)`. They set
required fields (timestamps, defaults, JSON columns) and leave optional
fields at their entity defaults so the test signal stays focused on the
fields the test sets.

```csharp
var template = TemplateBuilder.Default("runtime-15")
    .WithCoreFolder("Source", ("Sample.al", "// content"));
```

If a test needs a shape no builder covers, add a fluent extension to the
existing builder rather than spinning up a new helper class. Keep the
builders small.

### Service tests

Construct services directly with their constructor dependencies — no DI
container in tests. `GenerationService` takes
`(AppDbContext, WorkspaceConfigService, ILogger<GenerationService>)`;
construct each from `_db.NewContext()` and `NullLogger<T>.Instance`.

### Verifying generated artefacts

Open the `MemoryStream` returned by `GenerationService` as a
`ZipArchive`, then read entries by path with `archive.GetEntry("…")` and
parse the contents. Tests should assert against the file contents rather
than poking private helpers; that way a refactor of the internals can't
silently regress the contract.

### Validation tests

Validation tests live in `Validation/`. They throw a
`PlanValidationException` and assert the dictionary contains the expected
field key — never assert against the message string. The form layer keys
errors by field name to render them inline; that contract is what these
tests guard.

## Bar for new code

After Milestone 12, every service method added in M13–M15 ships with tests
for the happy path and for any validation rule it introduces. This isn't a
coverage metric — it's a posture: if the code has a rule, the rule has a
test. When you add a new service, put its tests in the matching folder from
the Layout table above (or add a sibling folder here when nothing fits) and
follow the patterns above.
