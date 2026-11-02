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
| `Infrastructure/` | Reusable test plumbing — `TestDb`, `ManualClock`, `StillTrue`.     |
| `Generation/`     | `GenerationService` tests (ID-range allocation, mustache).         |
| `Audit/`          | `AuditInterceptor` snapshot tests.                                 |
| `Toml/`           | `TemplateTomlMapper` round-trip tests.                             |
| `Validation/`     | `PlanValidationException` field-key surface tests.                 |
| `Dashboard/`      | `DashboardService` — the counts and queues behind `/admin` and `/`. |
| `Routing/`        | End-to-end endpoint inspection — boots the app via                  |
|                   | `EndpointFactory` and asserts the endpoint map is                   |
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

**Seeded templates.** When most tests in a class start from the same costly
seed (the Object Explorer `.app` imports are the example), build it once with
`TestDb.FromSeed(key, seed, out value)`. The seed runs once per test process
on a fresh clone; every later call gets its own clone of the result, and
`value` is what the seed returned (ids survive the clone). The same key must
always mean the same seed, and the seed runs as the default organisation. A
test that writes anything before it seeds can't use the clone, so create the
`TestDb` lazily and fall back to seeding the slow way when it already exists
(`ObjectExplorer/DkReleaseSeed` and `ExplorerTreeTests`):

```csharp
private TestDb? _database;
private TestDb _db => _database ??= new TestDb();
public void Dispose() => _database?.Dispose();

private Task<int> SeedAsync()
{
    if (_database is not null) return DkReleaseSeed.ImportAsync(_database);
    _database = DkReleaseSeed.NewDatabase(out var releaseId);
    return Task.FromResult(releaseId);
}
```

**Seeded users.** Give a user you seed to sign in with
`PasswordHash = TestPasswords.Hash(password)`. It hashes at bcrypt's minimum
work factor and caches per password, and since bcrypt reads the factor back
from the hash, the sign-in is cheap too. Call `AuthService.HashPassword` only
in a test about hashing itself, or one that should start from the hash the app
makes.

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

The pages' `DisposeAsync` rests on `PageCalls` (`Components/Shared/`): a page
tracks the calls it starts off its own handlers (a poll tick, a timer's
re-read), and leaving cancels them and returns only once the last has ended.
`PageCallsTests` pins that; a page that polls should track its ticks the same
way rather than fire and forget them.

### Waiting and time

**Signal, not sleep.** A test never sleeps to let something happen (the one
exception is `StillTrue`, below, which polls on purpose to prove an absence:
there is no event to wait for when the point is that nothing happens). A sleep
followed by an assertion either fails when the machine is busy or passes
without the thing having happened. Wait for the event itself instead: a
`TaskCompletionSource` (with `RunContinuationsAsynchronously`) the code under
test completes, the task under test awaited with `WaitAsync`, bUnit's
`WaitForAssertion`, or `SpinWait.SpinUntil` on a counter, with a timeout.

**Clocks.** Pick by whether the code reads the time or waits on it:

- `ManualClock` (`Infrastructure/`) when the code waits on the clock: a timer,
  `Task.Delay(..., timeProvider)`, the search-box `Debouncer`, a page's poll.
  Time moves only on `Advance`, which fires every timer that falls due on the
  way; `PendingTimers` says whether a wait is set. Only a timer's own callback
  runs inside `Advance`: code awaiting a delay may resume on the thread pool
  after `Advance` returns, so wait for its effect rather than reading it
  straight after the call.
- `FakeTimeProvider` (`Auth/AccountServiceTests.cs`), and the per-class
  `TestClock`, `MutableClock` and `FixedClock`, when the code only reads the
  time (a token's expiry, a cache's lifetime). They move `GetUtcNow` and
  nothing else: a timer made on one is a real timer on real time, so once the
  code waits, switch to `ManualClock`.

To give a page a clock, register it as the `TimeProvider` before the first
render (`EnvironmentsListTests`), or swap it in behind a clock that delegates
(`EnvironmentDetailTests`' `SwitchableClock`).

**Nothing happens.** "Dismissing the dialog changes nothing" passes straight
after the click whether or not the page ignored the answer, because a page
that carried on writes a moment later. Check with `StillTrue.For` /
`ForAsync`, which keeps checking for a while, so the late write is caught.
"A while" is 750 ms by default: it proves nothing happened within that window,
not that nothing ever will. A write that would land later still passes, so
when the code under test waits on something slower, pass a longer window or
wait for the event that would have caused the write.

**A positive marker before an absence check.** "Another organisation's row is
not shown" also passes on a page that has not loaded yet. Assert something
that proves the page got there, such as the acting organisation's own row, in
the same `WaitForAssertion` as the absence.

**Gates.** To show a call reached a `KeyedGate` the test holds, wait until
`WaitingCount(key)` is 1. The gates are process-wide statics, shared by every
test class running alongside, and every test database numbers its
organisations 1 to 3 and its repositories from 1. So the key must be unique to
the test: an organisation or repository seeded with an id no other class
uses (`DependencyDriftServiceTests`, `GitHubReleaseServiceTests`). Counting
"more than were already waiting" on a shared key lets another class's caller
satisfy the wait.

**Statics the pages read.** `UpgradesPage.SearchDebounce` and
`EnvironmentsList.SearchDebounce` are statics a page reads when it first
filters. The classes that test the search box set them to zero in the
constructor (the one test about the wait sets its own) and back to zero on
dispose. The classes that
render `UpgradesPage` share `[Collection("UpgradesPage.SearchDebounce")]` so
that one class's setting never reaches another's page mid-test; a new class
that renders it joins that collection.

**Culture.** `CultureDefaults` is a module initializer that applies the app's
own culture before any test runs, so numbers and dates format the way the app
formats them whatever the machine's locale. Don't set a culture in a test.

### Endpoint tests

Boot the app with `EndpointFactory`, never with a hand-built
`WebApplicationFactory<Program>`. The factory gives its host the fixture's
connection string through configuration, so endpoint test classes run in
parallel with each other and with everything else. Don't put them in a
collection to serialise them.

- **Configuration.** Anything the app reads through `IConfiguration`
  (`Auth:WebAuthn:RpId`, say) goes in the `settings:` argument. It reaches
  that host only.
- **Environment.** A knob startup reads straight off the environment
  (`PUBLIC_BASE_URL`, `SINGLE_TENANT_MODE`) goes in `environment:`. That sets
  a process-wide variable, so the class must carry
  `[Collection(ProcessEnvironmentCollection.Name)]`, which runs it on its own
  after the parallel collections. Keep that list short: if a knob can move to
  `settings:`, move it.
- **Schedulers.** The factory takes the polled schedulers out of the host
  (backups, the BCQuality mirror, the Microsoft feed sync and the rest), so a
  host that lives past their startup delay doesn't clone repositories or call
  out to Microsoft and GitHub. Test a scheduler by driving it directly.
  `keepSchedulers: true` keeps them for the one test that checks they are
  registered. Those are the real schedulers: a host built that way that
  lives past their 20-second startup delay starts ticking them, network calls
  included. Keep such a host short-lived.
- **One host per class.** Booting a host costs about a second and its first
  page render most of another; every request after that takes milliseconds
  (#1536). So a class whose tests can share a host takes
  `IClassFixture<SharedEndpointHost>` and gets one `TestDb` and one
  `EndpointFactory` for all its tests, through the constructor. The tests of
  one class run one after another, so sharing costs isolation only between
  them, and these rules keep that safe:
  - Don't rely on an empty database. Seed what the test needs and assert only
    on what it seeded: a user from `SharedEndpointHost.UniqueEmail(...)`, rows
    looked up by the id the test got back. Sign-in is rate limited per email
    address, so a constant address signed in from every test locks the class
    out part way through. `HashPassword` returns `TestPasswords.Hash` (see
    "Seeded users" below).
  - Keep a host per test (an `EndpointFactory` in the test, or in the class
    constructor as before) when a test changes state the host or the whole
    database shares: a `system_settings` value the tests set differently
    (every webhook test stores the same secret, so that class shares; its
    one test without a secret boots a host of its own), maintenance mode, an
    organisation-wide setting another test asserts on, a singleton the test
    configures, or a rate limiter or slot gate it drains on purpose. When only
    one or two tests in a class do, give just those a host of their own
    (`CompareEndpointTests` does).
  - State an organisation shares (the tools it switched off or marked for
    step-up, a connected GitHub installation, its translation memory) does
    not need a host per test: give each test an organisation of its own with
    `SharedEndpointHost.SeedOrganization()`, seed its users there, and assert
    on that organisation only (`McpRequestFilterTests`,
    `GitHubAppEndpointsTests`). Site-wide state (`system_settings`, the
    site-wide tool switches read at startup) still takes a host per test
    (`ToolAccessGateTests`, `McpEnabledToggleTests`).
  - Don't leave background work running in a shared host. A test that queues
    a job (an import, a backfill, a build) runs on a fixture that takes that
    queue's worker out of the host, the way
    `ObjectExplorerLowsEndpointTests.Host` removes the release import worker,
    or gives that test a host of its own. Otherwise the job keeps writing to
    the shared database while later tests run.
  - Per-test state that lives in a singleton (a queue, a slot gate, an
    allow-list) can still be shared safely. Register it as a transient that
    reads the running test's instance off the fixture, and install fresh ones
    in the test class constructor (`GitHubWebhookEndpointTests.Host`). A
    per-caller rate limiter most of the tests hit gets a caller address per
    test instead of a host per test (`OAuthRedirectUriTests.Host`,
    `GitHubWebhookEndpointTests.Host`). A pooled HTTP handler outlives a
    test, so a fake behind one is reached through a forwarding handler that
    reads the running test's fake (`GitHubAppEndpointsTests.Host`).
  - Fakes a class swaps in go through a fixture that derives from
    `SharedEndpointHost` and passes `configureServices` or `settings` to its
    protected constructor. Fakes with per-test state are read through the
    fixture at resolve time, so each test can install fresh ones
    (`PaletteEndpointTests.Host`).
- **Key files.** `KeyDirectoryDefaults` points every host at a key folder of
  the test process's own and creates the OAuth keys there before any host
  boots, so parallel first boots only read them. Don't set
  `DATA_PROTECTION_KEY_DIR` or `OAUTH_KEY_DIR` in a test.

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

That rule is for field-keyed validation only. A refusal whose wording is
itself the contract is asserted word for word on purpose: a permission
refusal that has to read the same as the answer to an id that does not
exist, so the message cannot tell a caller which ids are real, is pinned to
the exact text (#1474). A loose match there ("any exception", a
"permission" substring) is the bug.

## Bar for new code

After Milestone 12, every service method added in M13–M15 ships with tests
for the happy path and for any validation rule it introduces. This isn't a
coverage metric — it's a posture: if the code has a rule, the rule has a
test. When you add a new service, put its tests in the matching folder from
the Layout table above (or add a sibling folder here when nothing fits) and
follow the patterns above.
