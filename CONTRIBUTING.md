# Contributing

## Set up your environment

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and, for the integration tests,
[Docker](https://docs.docker.com/get-docker/) with Docker Compose. Check the SDK:

```bash
dotnet --version
```

Clone the repository, then restore and build:

```bash
dotnet restore
dotnet build
```

The libraries target `net8.0`, `net9.0` and `net10.0`; the tests and the sample target `net10.0` only.
Building for the older frameworks needs no extra SDKs — the reference packs come from NuGet.

## Run the tests

**Do not use `dotnet test`.** The test project runs on Microsoft Testing Platform, and the .NET 10 SDK's
`dotnet test` still routes through VSTest, which refuses it. The project is an executable, so run it:

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -trait- "Category=Integration"
```

Filters are xunit's own, not VSTest's:

| Intent | Argument |
|---|---|
| Everything except integration tests | `-trait- "Category=Integration"` |
| Only integration tests | `-trait "Category=Integration"` |
| One test class | `-class "Xrpl.PaymentGateway.Tests.LedgerRangeSetTests"` |
| One test method | `-method "Xrpl.PaymentGateway.Tests.NodePoolTests.AnEmptyPoolIsRejected"` |

### Integration tests

Integration tests need a standalone `rippled` node on `ws://localhost:6006` and, for the Postgres store's
contract tests, a database on `localhost:55432`. Both come up from one Compose file:

```bash
docker compose -p xrplpg-ci -f .ci-config/docker-compose.ci.yml up -d
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -trait "Category=Integration"
docker compose -p xrplpg-ci -f .ci-config/docker-compose.ci.yml down
```

The `-p xrplpg-ci` project name matters: the stand publishes the same ports as the XrplCSharp CI stand, so
only one of the two can run at a time. If a healthy node is already listening, the tests use it and you can
skip the Compose step. When a dependency is missing, each test skips itself rather than failing — the suite
must be runnable without Docker.

The ledger tests build a small economy — a receiving account, two token issuers, two buyers — and pay it in
XRP and in an issued currency. Expect about a minute and a half, most of it setup.

## Add a payment store

`IPaymentStore` is small, and two of its requirements are easy to get wrong: `GetOrAssignTagAsync` must be
atomic, and `TryAddPaymentAsync` must enforce uniqueness of the transaction hash and return `false` rather
than throw on a duplicate.

Prove a new store by deriving its test class from `PaymentStoreContract` and implementing `CreateAsync`.
Override `ReopenAsync` when the store survives a restart; leave it alone when it cannot, and the durability
cases skip themselves instead of passing vacuously.

```csharp
public class MyStoreTests : PaymentStoreContract
{
    protected override Task<IPaymentStore> CreateAsync(uint firstDestinationTag = 1) =>
        Task.FromResult<IPaymentStore>(new MyStore(firstDestinationTag));
}
```

## Code style

Follow what the surrounding code already does:

- Explicit types instead of `var`.
- `async`/`await` for I/O, with a `CancellationToken` threaded through.
- `ConfigureAwait(false)` in library code.
- `decimal` for amounts, and `StringComparison.Ordinal` for address and hash comparisons.
- Comments explain **why**, not what. A comment restating the line above it is noise; one explaining a
  decision that took thought is the point.

All artifacts in this repository are written in English: code, comments, commit messages, pull requests and
documentation.

Commits follow [Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `test:`,
`docs:`, `chore:`.

## Submit a change

1. Branch from `dev`.
2. Add tests. A behavior change with no failing-then-passing test is hard to review and easy to undo.
3. Run the unit tests, and the integration tests when you touched anything that talks to a ledger or a
   store.
4. Open a pull request against `dev` and let CI finish. Both jobs must pass: `unit` and `integration`.
5. Get a review from a maintainer.

## Release

Publishing is irreversible: a version number on nuget.org is spent the moment it is accepted, deletion is
not offered, and unlisting only hides it. Pushing to `release` publishes.

1. Confirm CI is green on `dev`.
2. Bump `PackageVersion` in `Directory.Build.props`. All three packages share it.
3. Update `CHANGES.md`.
4. Merge `dev` into `release`. The push triggers `.github/workflows/nuget.release.yml`, which builds, runs
   the unit tests, packs, and publishes to GitHub Packages and then nuget.org.
5. Create a GitHub release with the matching tag.

Publishing authenticates through NuGet Trusted Publishing rather than a long-lived API key. The policy on
nuget.org is bound to this repository **and to the workflow's file name** — renaming
`nuget.release.yml` breaks publishing. The `NUGET_USER` secret must name the same package owner the policy
was created for.
