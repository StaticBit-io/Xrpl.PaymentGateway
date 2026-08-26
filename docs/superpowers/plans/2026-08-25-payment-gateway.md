# Xrpl.PaymentGateway Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a storage-agnostic .NET library that accepts XRPL payments on one receiving account, allocates destination tags per buyer, records every incoming payment exactly once, and delivers it to host code.

**Architecture:** Two packages. `Xrpl.PaymentGateway.Abstractions` holds models and interfaces with no SDK dependency, so a host implementing storage does not pull in crypto libraries. `Xrpl.PaymentGateway` holds a `BackgroundService` monitor that subscribes to the receiving account over WebSocket, catches up missed ledgers through `account_tx` on every (re)connect, computes amounts from transaction metadata via `Xrpl.Utils.BalanceChanges`, and writes through `IPaymentStore` keyed by transaction hash. A persisted ledger cursor marks the boundary below which completeness is proven.

**Tech Stack:** .NET 8/9/10 multi-target, `Xrpl` 11.0.0 NuGet package, `Microsoft.Extensions.*` 9.0.0 (Hosting.Abstractions, Options, Logging.Abstractions, DependencyInjection.Abstractions), `System.Threading.Channels`, xUnit v3 4.0.0 for tests, ASP.NET Core 10 for the sample API, docker compose standalone `rippled` for integration tests.

**Spec:** `docs/specs/2026-08-25-payment-gateway-design.md`

---

## Verified SDK Facts

These were checked against the published `Xrpl` 11.0.0 source (branch `origin/release` of StaticBit-io/XrplCSharp). Do not re-derive them; do not substitute the names you might expect.

| What you need | Real name | Where |
|---|---|---|
| Client class | `Xrpl.Client.XrplClient`, ctor `XrplClient(string server, ClientOptions? options)` | `Xrpl/Client/IXrplClient.cs:856` |
| Live transaction event | `event OnTransaction` → `Xrpl.Models.Subscriptions.TransactionStream` | `Xrpl/Models/Subscriptions/TransactionStream.cs:20` |
| Ledger close event | `event OnLedgerClosed` → `Xrpl.Models.Methods.LedgerStream` (`.LedgerIndex` is `ulong`) | `Xrpl/Models/Methods/Subscribe.cs:178` |
| Session end event | `event OnSessionEnded(SessionEndReason reason, string description)` | `Xrpl/Client/IXrplClient.cs:71` |
| Subscribe | `Task<XrplResponse<object>> Subscribe(SubscribeRequest, CancellationToken)` | `Xrpl/Client/IXrplClient.cs:283` |
| account_tx | `Task<XrplResponse<AccountTransactions>> AccountTransactions(AccountTransactionsRequest, CancellationToken)` — response type is `AccountTransactions`, **not** `AccountTransactionsResponse` | `Xrpl/Models/Methods/AccountTransactions.cs:14` |
| account_tx list item | `TransactionSummary` (`tx_json` + `meta`) | `Xrpl/Models/Methods/AccountTransactions.cs:93` |
| Common contract over live + history | `interface IAccountTransaction { DateTime? CloseTimeIso; string Hash; string LedgerHash; ulong? LedgerIndex; Meta Meta; TransactionResponse Transaction; bool Validated; }` — implemented by **both** `TransactionStream` and `TransactionSummary` | `Xrpl/Models/Methods/AccountTransactions.cs:55` |
| server_info | `Task<XrplResponse<ServerInfo>> ServerInfo(ServerInfoRequest, CancellationToken)`; `ServerInfo.Info.CompleteLedgers`, `.ServerState`, `.ValidatedLedger?.Sequence` (an `int`) | `Xrpl/Models/Methods/ServerInfo.cs:99,169,191,497` |
| Metadata | `Xrpl.Models.Transactions.Meta : ITransactionMetadata`; `.TransactionResult` (string), `.AffectedNodes` | `Xrpl/Models/Transactions/Common.cs:477` |
| Balance changes | `Xrpl.Utils.BalanceChanges.GetBalanceChanges(ITransactionMetadata) → Dictionary<string, List<Currency>>` | `Xrpl/Utils/GetBalanceChanges.cs:158` |
| Amount | `Xrpl.Models.Common.Currency`; **`ValueAsNumber` is drops for XRP**, `ValueAsXrp` is XRP and returns `null` when `CurrencyCode != "XRP"`; extension `IsXrp()` requires `CurrencyCode == "XRP" && Issuer == null` | `Xrpl/Models/Common/Currency.cs:85,143,256` |
| Destination tag | **Not** on the base transaction. Cast: `tx is IPayment p → p.DestinationTag` | `Xrpl/Models/Transactions/Payment.cs:192` |
| JSON options for fixtures | `Xrpl.Client.Json.XrplJsonOptions.Default` — required when deserializing `Meta` / `TransactionStream` by hand | used in `Tests/Xrpl.Tests/Utils/GetBalanceChangesTests.cs` |
| Standalone master account | `rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh` / seed `snoPBrXtMeMyMHUVTgbuqAfg1SUTb` | `Tests/Xrpl.Tests/Integration/Utils.cs:76` |
| Submit + sign | `Task<Submit> Submit(ITransactionRequest tx, XrplWallet wallet, bool autoFill = true, ...)`; result `.EngineResult`, and `.Transaction` (an `ITransactionResponse`) for the hash — `.TxJson` is typed `object` and has no `Hash` | `Xrpl/Client/IXrplClient.cs:489,1021`; `Xrpl/Models/Transactions/Submit.cs:69,75` |
| Balance helper | `GetXrpFreeBalance(this IXrplClient, string address, ...)` is an extension in namespace **`Xrpl.Sugar`** (class `BalancesSugar`) | `Xrpl/Sugar/Balances.cs:14,31` |
| Name collision | `Xrpl.Models.Methods` declares a type named `Channel`, which is ambiguous with `System.Threading.Channels.Channel` wherever both are imported (CS0104). Alias it. | verified by compilation |

Two consequences that shape the code:

1. **Subscribe with `accounts`, not the `transactions` stream.** `SubscribeRequest { Accounts = [address], Streams = [StreamType.Ledger] }` delivers only our account's transactions plus ledger closes. Subscribing to `StreamType.Transactions` would deliver every transaction on the network.
2. **`IAccountTransaction` lets one processor serve both paths.** The stream handler and the catch-up runner feed the same `TransactionProcessor.Process(IAccountTransaction)`.

---

## File Structure

```
Xrpl.PaymentGateway/
├── Directory.Build.props
├── Xrpl.PaymentGateway.sln
├── .ci-config/                                  # copied standalone rippled stand
├── src/
│   ├── Xrpl.PaymentGateway.Abstractions/
│   │   ├── PaymentRecord.cs                     # the persisted payment
│   │   ├── PaymentInstructions.cs               # address + tag handed to a buyer
│   │   ├── IPaymentStore.cs                     # the one interface a host must implement
│   │   ├── IPaymentReceivedHandler.cs           # host callback
│   │   ├── IPaymentGateway.cs                   # tag issuance entry point
│   │   ├── IPaymentMonitorHealth.cs             # health + reconciliation entry point
│   │   ├── PaymentMonitorState.cs
│   │   ├── PaymentMonitorHealthReport.cs
│   │   └── ReconciliationResult.cs
│   └── Xrpl.PaymentGateway/
│       ├── PaymentGatewayOptions.cs
│       ├── PaymentGatewayOptionsValidator.cs
│       ├── ServiceCollectionExtensions.cs
│       ├── XrplPaymentGateway.cs                # IPaymentGateway impl
│       ├── XrplPaymentMonitor.cs                # BackgroundService, the orchestrator
│       ├── PaymentMonitorHealth.cs              # IPaymentMonitorHealth impl
│       ├── InMemoryPaymentStore.cs              # reference IPaymentStore
│       └── Internal/
│           ├── IXrplNodeConnection.cs           # what the monitor needs from a node
│           ├── XrplNodeConnection.cs            # real impl over XrplClient
│           ├── IXrplNodeConnectionFactory.cs
│           ├── XrplNodeConnectionFactory.cs
│           ├── NodeStatus.cs
│           ├── AccountTransactionQuery.cs
│           ├── AccountTransactionPage.cs
│           ├── NodePool.cs
│           ├── LedgerRangeSet.cs                # complete_ledgers parser
│           ├── TransactionProcessor.cs          # tx + meta → PaymentRecord
│           ├── ProcessingResult.cs
│           ├── PaymentDispatcher.cs             # record → store → handler
│           ├── StoreRetryPolicy.cs
│           ├── CatchUpRunner.cs
│           ├── CatchUpResult.cs
│           ├── MonitorEvent.cs
│           └── MonitorSnapshot.cs               # health state shared with the monitor
├── tests/
│   └── Xrpl.PaymentGateway.Tests/
│       ├── Fakes/FakeXrplNodeConnection.cs
│       ├── Fakes/FixedTimeProvider.cs
│       ├── Fakes/RecordingHandler.cs
│       ├── Fixtures/TransactionFixtures.cs      # canned rippled JSON
│       └── <one test file per unit>
└── samples/
    └── Xrpl.PaymentGateway.SampleApi/
```

Boundaries worth stating: the monitor is the only component that touches the connection lifecycle; the processor is pure (transaction in, result out, no I/O); the dispatcher is the only place that talks to both the store and the host handler; health/reconciliation reads the monitor's snapshot but never drives it.

---

### Task 1: Repository scaffolding and Abstractions contracts

**Files:**
- Create: `Directory.Build.props`
- Create: `.gitignore`
- Create: `src/Xrpl.PaymentGateway.Abstractions/Xrpl.PaymentGateway.Abstractions.csproj`
- Create: `src/Xrpl.PaymentGateway.Abstractions/{PaymentRecord,PaymentInstructions,IPaymentStore,IPaymentReceivedHandler,IPaymentGateway,IPaymentMonitorHealth,PaymentMonitorState,PaymentMonitorHealthReport,ReconciliationResult}.cs`
- Create: `src/Xrpl.PaymentGateway/Xrpl.PaymentGateway.csproj`
- Create: `tests/Xrpl.PaymentGateway.Tests/Xrpl.PaymentGateway.Tests.csproj`
- Create: `tests/Xrpl.PaymentGateway.Tests/ScaffoldingTests.cs`
- Create: `Xrpl.PaymentGateway.sln`

- [ ] **Step 1: Create `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
    <Authors>StaticBit</Authors>
    <Company>StaticBit</Company>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <RepositoryUrl>https://github.com/StaticBit-io/Xrpl.PaymentGateway</RepositoryUrl>
    <PackageVersion>0.1.0</PackageVersion>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Create `.gitignore`**

```gitignore
bin/
obj/
.vs/
*.user
artifacts/
TestResults/
```

- [ ] **Step 3: Create the Abstractions project file**

`src/Xrpl.PaymentGateway.Abstractions/Xrpl.PaymentGateway.Abstractions.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
    <PackageId>Xrpl.PaymentGateway.Abstractions</PackageId>
    <Description>Models and interfaces for accepting XRP Ledger payments. Storage-agnostic: no dependency on the XRPL SDK.</Description>
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: Write `PaymentRecord.cs`**

```csharp
namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// One incoming payment credited to the receiving account. <see cref="TransactionHash"/> is the unique key.
/// </summary>
public sealed class PaymentRecord
{
    /// <summary>Transaction hash. Unique key of the record.</summary>
    public required string TransactionHash { get; init; }

    /// <summary>XRPL transaction type that produced the credit, e.g. "Payment".</summary>
    public required string TransactionType { get; init; }

    /// <summary>Sending account. Never equal to the receiving address.</summary>
    public required string Sender { get; init; }

    /// <summary>Destination tag carried by the transaction, or null when it carried none.</summary>
    public uint? DestinationTag { get; init; }

    /// <summary>"XRP", or the issued currency code (3 characters or 40 hex characters).</summary>
    public required string Currency { get; init; }

    /// <summary>Issuer of the received token. Null for XRP.</summary>
    public string? Issuer { get; init; }

    /// <summary>Amount in human units: XRP in XRP (not drops), tokens in their own units.</summary>
    public required decimal Value { get; init; }

    /// <summary>Index of the validated ledger the transaction was included in.</summary>
    public required uint LedgerIndex { get; init; }

    /// <summary>When the library recorded the payment.</summary>
    public required DateTimeOffset ProcessedAt { get; init; }
}
```

- [ ] **Step 5: Write `PaymentInstructions.cs`**

```csharp
namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>Where a buyer must send funds, and under which destination tag.</summary>
public sealed class PaymentInstructions
{
    /// <summary>The receiving r-address.</summary>
    public required string Address { get; init; }

    /// <summary>The tag assigned to this buyer. Stable across calls.</summary>
    public required uint DestinationTag { get; init; }
}
```

- [ ] **Step 6: Write `IPaymentStore.cs`**

```csharp
namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// Persistence the host provides. Postgres, a file, anything — the library never assumes a database.
/// </summary>
/// <remarks>
/// Two hard requirements on implementations:
/// <list type="bullet">
/// <item><description><see cref="GetOrAssignTagAsync"/> must be atomic. Two concurrent calls for the same new
/// buyer must return the same tag, and one tag must never reach two buyers.</description></item>
/// <item><description><see cref="TryAddPaymentAsync"/> must enforce uniqueness of
/// <see cref="PaymentRecord.TransactionHash"/> and return false on a duplicate rather than throwing.</description></item>
/// </list>
/// No transactionality across methods is required: the library writes the payment first and advances the
/// cursor afterwards, so a crash between the two causes an idempotent replay, never a loss.
/// </remarks>
public interface IPaymentStore
{
    /// <summary>Returns the buyer's existing tag, or atomically assigns the next one.</summary>
    Task<uint> GetOrAssignTagAsync(string buyerId, CancellationToken cancellationToken);

    /// <summary>Resolves a destination tag back to a buyer, or null when the tag was never issued.</summary>
    Task<string?> FindBuyerByTagAsync(uint tag, CancellationToken cancellationToken);

    /// <summary>Inserts the record as unhandled. Returns false when the hash is already stored.</summary>
    Task<bool> TryAddPaymentAsync(PaymentRecord record, CancellationToken cancellationToken);

    /// <summary>Marks a stored payment as delivered to the host handler.</summary>
    Task MarkHandledAsync(string transactionHash, CancellationToken cancellationToken);

    /// <summary>Returns up to <paramref name="limit"/> payments not yet marked handled, oldest first.</summary>
    Task<IReadOnlyList<PaymentRecord>> GetUnhandledPaymentsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>The ledger boundary below which record completeness is proven, or null on a fresh store.</summary>
    Task<uint?> GetLastProcessedLedgerAsync(CancellationToken cancellationToken);

    /// <summary>Persists the completeness boundary.</summary>
    Task SetLastProcessedLedgerAsync(uint ledgerIndex, CancellationToken cancellationToken);
}
```

- [ ] **Step 7: Write the three remaining host-facing interfaces**

`IPaymentReceivedHandler.cs`:

```csharp
namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// Host code that reacts to a recorded payment. Called after the record is persisted, at least once —
/// implementations must be idempotent. An exception here never blocks recording; the record stays
/// unhandled and reconciliation redelivers it.
/// </summary>
public interface IPaymentReceivedHandler
{
    /// <param name="payment">The recorded payment.</param>
    /// <param name="buyerId">The buyer the destination tag resolved to, or null when it resolved to nobody.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task OnPaymentReceivedAsync(PaymentRecord payment, string? buyerId, CancellationToken cancellationToken);
}
```

`IPaymentGateway.cs`:

```csharp
namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>Issues payment instructions to buyers.</summary>
public interface IPaymentGateway
{
    /// <summary>
    /// Returns the receiving address and the buyer's destination tag. A returning buyer always receives
    /// the tag assigned earlier.
    /// </summary>
    Task<PaymentInstructions> GetPaymentInstructionsAsync(string buyerId, CancellationToken cancellationToken);
}
```

`IPaymentMonitorHealth.cs`:

```csharp
namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// Liveness and repair. Drive both from whatever scheduler the host already runs — Hangfire, Quartz,
/// a timer. The library takes no scheduler dependency.
/// </summary>
public interface IPaymentMonitorHealth
{
    /// <summary>Cheap read-only snapshot. Safe to call every few seconds.</summary>
    Task<PaymentMonitorHealthReport> CheckAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Redelivers unhandled records and re-verifies a window of ledgers below the cursor.
    /// Long-running; concurrent calls return immediately with <see cref="ReconciliationResult.Skipped"/> set.
    /// </summary>
    Task<ReconciliationResult> ReconcileAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 8: Write the three report types**

`PaymentMonitorState.cs`:

```csharp
namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>What the background monitor is doing right now.</summary>
public enum PaymentMonitorState
{
    /// <summary>Not started, or shut down.</summary>
    Stopped,

    /// <summary>Opening a connection to a node.</summary>
    Connecting,

    /// <summary>Replaying ledgers between the cursor and the current validated ledger.</summary>
    CatchingUp,

    /// <summary>Connected and consuming the live stream. The healthy state.</summary>
    Streaming,

    /// <summary>Backing off before the next connection attempt.</summary>
    Reconnecting,

    /// <summary>Nodes are synced but the network is not validating ledgers. Not a local fault.</summary>
    NetworkStalled,

    /// <summary>The store is failing; processing is paused and the cursor is frozen.</summary>
    StoreUnavailable,

    /// <summary>No node in the pool holds the ledger range needed to prove completeness.</summary>
    HistoryGap,
}
```

`PaymentMonitorHealthReport.cs`:

```csharp
namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>A point-in-time view of the monitor, for dashboards, health endpoints and alerting.</summary>
public sealed class PaymentMonitorHealthReport
{
    public required PaymentMonitorState State { get; init; }

    /// <summary>The node currently in use, or null when not connected.</summary>
    public string? CurrentNode { get; init; }

    /// <summary>Highest validated ledger the monitor has seen.</summary>
    public uint? LastValidatedLedger { get; init; }

    /// <summary>The persisted completeness boundary.</summary>
    public uint? Cursor { get; init; }

    /// <summary>Ledgers between the cursor and the last validated ledger.</summary>
    public uint LedgerLag { get; init; }

    /// <summary>Payments recorded but not yet delivered to the host handler, capped at the sample size.</summary>
    public int UnhandledPaymentCount { get; init; }

    /// <summary>Transactions that credited the account in a shape the gateway does not expect.</summary>
    public long AnomalyCount { get; init; }

    /// <summary>Last error the monitor recorded, or null.</summary>
    public string? LastError { get; init; }

    /// <summary>When the monitor last saw a ledger close.</summary>
    public DateTimeOffset? LastLedgerAt { get; init; }

    /// <summary>True when streaming, within the acceptable ledger lag, and with nothing stuck undelivered.</summary>
    public required bool IsHealthy { get; init; }
}
```

`ReconciliationResult.cs`:

```csharp
namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>Outcome of a reconciliation run.</summary>
public sealed class ReconciliationResult
{
    /// <summary>Records that were unhandled and reached the host handler on this run.</summary>
    public required int RedeliveredCount { get; init; }

    /// <summary>Payments found on the ledger that were missing from the store. Any value above zero is a defect.</summary>
    public required int RecoveredCount { get; init; }

    /// <summary>Errors encountered. A non-empty list means the run did not fully complete.</summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>True when another reconciliation was already running and this call did nothing.</summary>
    public bool Skipped { get; init; }
}
```

- [ ] **Step 9: Create the implementation project file**

`src/Xrpl.PaymentGateway/Xrpl.PaymentGateway.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
    <PackageId>Xrpl.PaymentGateway</PackageId>
    <Description>Background XRP Ledger payment monitor: destination tag allocation, exactly-once recording, catch-up after disconnects, health and reconciliation.</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Xrpl.PaymentGateway.Abstractions\Xrpl.PaymentGateway.Abstractions.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Xrpl" Version="11.0.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="9.0.0" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Xrpl.PaymentGateway.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 10: Create the test project file**

`tests/Xrpl.PaymentGateway.Tests/Xrpl.PaymentGateway.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Xrpl.PaymentGateway\Xrpl.PaymentGateway.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.0" />
    <!-- The tests build a real DI container and a real host, which the Xrpl package does not bring in. -->
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.0" />
  </ItemGroup>
</Project>
```

**Do not add `Microsoft.NET.Test.Sdk` or `xunit.runner.visualstudio`, and do not run these tests with `dotnet test`.** This was verified empirically on this machine: xunit.v3 4.0.0 runs on Microsoft.Testing.Platform, and on the .NET 10 SDK `dotnet test` still routes through VSTest, which fails with

```
error : Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later.
```

Neither a `dotnet.config` with `[dotnet.test.runner]` nor the `TestingPlatformDotnetTestSupport` / `UseMicrosoftTestingPlatformRunner` properties changed that. The test project is an executable, so run it directly:

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests
```

Filters are xunit's own, not VSTest's — `--filter "FullyQualifiedName~X"` is not understood here:

| Intent | Argument |
|---|---|
| One test class | `-class "Xrpl.PaymentGateway.Tests.LedgerRangeSetTests"` |
| Only integration tests | `-trait "Category=Integration"` |
| Everything except integration | `-trait- "Category=Integration"` |

Arguments go after `--`, for example `dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -trait- "Category=Integration"`.

- [ ] **Step 11: Write a scaffolding test that proves the harness runs**

`tests/Xrpl.PaymentGateway.Tests/ScaffoldingTests.cs`:

```csharp
using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class ScaffoldingTests
{
    [Fact]
    public void PaymentRecordCarriesTheFieldsTheSpecRequires()
    {
        PaymentRecord record = new PaymentRecord
        {
            TransactionHash = "F00D",
            TransactionType = "Payment",
            Sender = "rSender",
            DestinationTag = 42,
            Currency = "XRP",
            Issuer = null,
            Value = 1.5m,
            LedgerIndex = 100,
            ProcessedAt = DateTimeOffset.UnixEpoch,
        };

        Assert.Equal("F00D", record.TransactionHash);
        Assert.Equal(42u, record.DestinationTag);
        Assert.Equal(1.5m, record.Value);
    }
}
```

- [ ] **Step 12: Create the solution and add all three projects**

```bash
dotnet new sln --name Xrpl.PaymentGateway
```

Then:

```bash
dotnet sln add src/Xrpl.PaymentGateway.Abstractions/Xrpl.PaymentGateway.Abstractions.csproj src/Xrpl.PaymentGateway/Xrpl.PaymentGateway.csproj tests/Xrpl.PaymentGateway.Tests/Xrpl.PaymentGateway.Tests.csproj
```

- [ ] **Step 13: Restore, build and run the test**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests
```

Expected output ends with a summary line from the xUnit v3 in-process runner:

```
=== TEST EXECUTION SUMMARY ===
   Xrpl.PaymentGateway.Tests  Total: 1, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0
```

Do not proceed to Task 2 until a test actually executes. This exact combination — `xunit.v3` 4.0.0, .NET 10
SDK, `net8.0;net9.0;net10.0` libraries, `Xrpl` 11.0.0 — was verified working on this machine, so a failure
here means something diverged from the plan rather than a package incompatibility to work around.

- [ ] **Step 14: Commit**

```bash
git add -A && git commit -m "feat: scaffold solution and payment gateway abstractions"
```

---

### Task 2: InMemoryPaymentStore

The reference implementation. It documents the semantics every other store must match, and every later test uses it.

**Files:**
- Create: `src/Xrpl.PaymentGateway/InMemoryPaymentStore.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/InMemoryPaymentStoreTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Xrpl.PaymentGateway.Tests/InMemoryPaymentStoreTests.cs`:

```csharp
using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class InMemoryPaymentStoreTests
{
    private static PaymentRecord Record(string hash, uint? tag = null) => new PaymentRecord
    {
        TransactionHash = hash,
        TransactionType = "Payment",
        Sender = "rSender",
        DestinationTag = tag,
        Currency = "XRP",
        Value = 1m,
        LedgerIndex = 10,
        ProcessedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task FirstBuyerGetsTheConfiguredFirstTag()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore(firstDestinationTag: 7);

        uint tag = await store.GetOrAssignTagAsync("buyer-1", TestContext.Current.CancellationToken);

        Assert.Equal(7u, tag);
    }

    [Fact]
    public async Task AReturningBuyerGetsTheSameTag()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();

        uint first = await store.GetOrAssignTagAsync("buyer-1", TestContext.Current.CancellationToken);
        await store.GetOrAssignTagAsync("buyer-2", TestContext.Current.CancellationToken);
        uint again = await store.GetOrAssignTagAsync("buyer-1", TestContext.Current.CancellationToken);

        Assert.Equal(first, again);
    }

    [Fact]
    public async Task TagsAreSequentialAndNeverShared()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();

        uint one = await store.GetOrAssignTagAsync("a", TestContext.Current.CancellationToken);
        uint two = await store.GetOrAssignTagAsync("b", TestContext.Current.CancellationToken);

        Assert.Equal(1u, one);
        Assert.Equal(2u, two);
        Assert.Equal("a", await store.FindBuyerByTagAsync(one, TestContext.Current.CancellationToken));
        Assert.Equal("b", await store.FindBuyerByTagAsync(two, TestContext.Current.CancellationToken));
        Assert.Null(await store.FindBuyerByTagAsync(999u, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentAllocationForOneBuyerYieldsOneTag()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();

        Task<uint>[] calls = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => store.GetOrAssignTagAsync("buyer-1", TestContext.Current.CancellationToken)))
            .ToArray();
        uint[] tags = await Task.WhenAll(calls);

        Assert.Single(tags.Distinct());
    }

    [Fact]
    public async Task AddingTheSameHashTwiceReturnsFalseTheSecondTime()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();

        Assert.True(await store.TryAddPaymentAsync(Record("HASH-1"), TestContext.Current.CancellationToken));
        Assert.False(await store.TryAddPaymentAsync(Record("HASH-1"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnhandledPaymentsComeBackOldestFirstAndDisappearWhenMarked()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();
        await store.TryAddPaymentAsync(Record("A"), TestContext.Current.CancellationToken);
        await store.TryAddPaymentAsync(Record("B"), TestContext.Current.CancellationToken);

        IReadOnlyList<PaymentRecord> before = await store.GetUnhandledPaymentsAsync(10, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "A", "B" }, before.Select(p => p.TransactionHash));

        await store.MarkHandledAsync("A", TestContext.Current.CancellationToken);

        IReadOnlyList<PaymentRecord> after = await store.GetUnhandledPaymentsAsync(10, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "B" }, after.Select(p => p.TransactionHash));
    }

    [Fact]
    public async Task TheCursorStartsEmptyAndRoundTrips()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();

        Assert.Null(await store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));

        await store.SetLastProcessedLedgerAsync(4242u, TestContext.Current.CancellationToken);

        Assert.Equal(4242u, await store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.InMemoryPaymentStoreTests"
```

Expected: compile error, `InMemoryPaymentStore` does not exist.

- [ ] **Step 3: Write the implementation**

`src/Xrpl.PaymentGateway/InMemoryPaymentStore.cs`:

```csharp
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway;

/// <summary>
/// A thread-safe in-process <see cref="IPaymentStore"/>. It ships as the reference for the interface
/// contract and backs tests and samples. Everything is lost on restart, so a production host that must
/// survive a restart supplies its own store.
/// </summary>
public sealed class InMemoryPaymentStore : IPaymentStore
{
    private readonly object _gate = new object();
    private readonly Dictionary<string, uint> _tagsByBuyer = new Dictionary<string, uint>(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _buyersByTag = new Dictionary<uint, string>();
    private readonly Dictionary<string, PaymentEntry> _payments = new Dictionary<string, PaymentEntry>(StringComparer.Ordinal);
    private readonly List<string> _insertionOrder = new List<string>();
    private uint _nextTag;
    private uint? _cursor;

    /// <param name="firstDestinationTag">The tag handed to the first buyer. Zero is rejected: many wallets treat it as "no tag".</param>
    public InMemoryPaymentStore(uint firstDestinationTag = 1)
    {
        if (firstDestinationTag == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstDestinationTag), "destination tag 0 is not issued");
        }

        _nextTag = firstDestinationTag;
    }

    /// <summary>Every payment ever recorded, oldest first. Not part of <see cref="IPaymentStore"/>; samples use it.</summary>
    public IReadOnlyList<PaymentRecord> Snapshot()
    {
        lock (_gate)
        {
            return _insertionOrder.Select(hash => _payments[hash].Record).ToList();
        }
    }

    public Task<uint> GetOrAssignTagAsync(string buyerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);

        lock (_gate)
        {
            if (_tagsByBuyer.TryGetValue(buyerId, out uint existing))
            {
                return Task.FromResult(existing);
            }

            if (_nextTag == uint.MaxValue)
            {
                throw new InvalidOperationException("the destination tag space is exhausted");
            }

            uint assigned = _nextTag;
            _nextTag++;
            _tagsByBuyer[buyerId] = assigned;
            _buyersByTag[assigned] = buyerId;
            return Task.FromResult(assigned);
        }
    }

    public Task<string?> FindBuyerByTagAsync(uint tag, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_buyersByTag.TryGetValue(tag, out string? buyer) ? buyer : null);
        }
    }

    public Task<bool> TryAddPaymentAsync(PaymentRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            if (_payments.ContainsKey(record.TransactionHash))
            {
                return Task.FromResult(false);
            }

            _payments[record.TransactionHash] = new PaymentEntry(record);
            _insertionOrder.Add(record.TransactionHash);
            return Task.FromResult(true);
        }
    }

    public Task MarkHandledAsync(string transactionHash, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_payments.TryGetValue(transactionHash, out PaymentEntry? entry))
            {
                entry.Handled = true;
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PaymentRecord>> GetUnhandledPaymentsAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        lock (_gate)
        {
            List<PaymentRecord> result = new List<PaymentRecord>();
            foreach (string hash in _insertionOrder)
            {
                PaymentEntry entry = _payments[hash];
                if (entry.Handled)
                {
                    continue;
                }

                result.Add(entry.Record);
                if (result.Count == limit)
                {
                    break;
                }
            }

            return Task.FromResult<IReadOnlyList<PaymentRecord>>(result);
        }
    }

    public Task<uint?> GetLastProcessedLedgerAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_cursor);
        }
    }

    public Task SetLastProcessedLedgerAsync(uint ledgerIndex, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _cursor = ledgerIndex;
        }

        return Task.CompletedTask;
    }

    private sealed class PaymentEntry
    {
        public PaymentEntry(PaymentRecord record) => Record = record;

        public PaymentRecord Record { get; }

        public bool Handled { get; set; }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.InMemoryPaymentStoreTests"
```

Expected: 7 tests pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add in-memory payment store reference implementation"
```

---

### Task 3: LedgerRangeSet — the complete_ledgers parser

This is what keeps a node with a hole in its history from silently returning a partial catch-up.

**Files:**
- Create: `src/Xrpl.PaymentGateway/Internal/LedgerRangeSet.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/LedgerRangeSetTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Xrpl.PaymentGateway.Tests/LedgerRangeSetTests.cs`:

```csharp
using Xrpl.PaymentGateway.Internal;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class LedgerRangeSetTests
{
    [Fact]
    public void ASingleRangeCoversLedgersInsideIt()
    {
        Assert.True(LedgerRangeSet.TryParse("32570-99383752", out LedgerRangeSet set));

        Assert.True(set.Covers(40000, 50000));
        Assert.True(set.Covers(32570, 99383752));
    }

    [Fact]
    public void ASingleRangeDoesNotCoverLedgersBelowOrAboveIt()
    {
        Assert.True(LedgerRangeSet.TryParse("100-200", out LedgerRangeSet set));

        Assert.False(set.Covers(99, 150));
        Assert.False(set.Covers(150, 201));
    }

    [Fact]
    public void AGappedListIsParsedAndTheGapIsNotCovered()
    {
        Assert.True(LedgerRangeSet.TryParse("24900901-24900984,24901116-24901158", out LedgerRangeSet set));

        Assert.True(set.Covers(24900902, 24900950));
        Assert.True(set.Covers(24901116, 24901158));
        Assert.False(set.Covers(24900950, 24901120));
    }

    [Fact]
    public void ASingleLedgerEntryIsARangeOfOne()
    {
        Assert.True(LedgerRangeSet.TryParse("500", out LedgerRangeSet set));

        Assert.True(set.Covers(500, 500));
        Assert.False(set.Covers(500, 501));
    }

    [Fact]
    public void TheEmptyMarkerParsesToASetThatCoversNothing()
    {
        Assert.True(LedgerRangeSet.TryParse("empty", out LedgerRangeSet set));

        Assert.False(set.Covers(1, 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-range")]
    [InlineData("200-100")]
    [InlineData("100-")]
    public void UnparseableInputFailsClosed(string? input)
    {
        Assert.False(LedgerRangeSet.TryParse(input, out LedgerRangeSet set));
        Assert.False(set.Covers(1, 1));
    }

    [Fact]
    public void AnEmptyRequestedSpanIsTriviallyCovered()
    {
        Assert.True(LedgerRangeSet.TryParse("100-200", out LedgerRangeSet set));

        Assert.True(set.Covers(300, 299));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.LedgerRangeSetTests"
```

Expected: compile error, `LedgerRangeSet` does not exist.

- [ ] **Step 3: Write the implementation**

`src/Xrpl.PaymentGateway/Internal/LedgerRangeSet.cs`:

```csharp
using System.Globalization;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// The <c>complete_ledgers</c> string a node reports in <c>server_info</c>, parsed into ranges.
/// A node that does not hold the whole span we intend to replay would answer <c>account_tx</c> with a
/// silently partial result, so we ask before we trust it.
/// </summary>
internal sealed class LedgerRangeSet
{
    private readonly IReadOnlyList<LedgerRange> _ranges;

    private LedgerRangeSet(IReadOnlyList<LedgerRange> ranges) => _ranges = ranges;

    /// <summary>A set that covers nothing.</summary>
    public static LedgerRangeSet Empty { get; } = new LedgerRangeSet(Array.Empty<LedgerRange>());

    /// <summary>
    /// Parses forms rippled emits: "empty", "32570-99383752", "24900901-24900984,24901116-24901158",
    /// and a bare single index. Returns false on anything else, leaving <paramref name="result"/> covering nothing.
    /// </summary>
    public static bool TryParse(string? completeLedgers, out LedgerRangeSet result)
    {
        result = Empty;

        if (string.IsNullOrWhiteSpace(completeLedgers))
        {
            return false;
        }

        string trimmed = completeLedgers.Trim();
        if (trimmed.Equals("empty", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        List<LedgerRange> ranges = new List<LedgerRange>();
        foreach (string part in trimmed.Split(','))
        {
            string chunk = part.Trim();
            if (chunk.Length == 0)
            {
                continue;
            }

            int dash = chunk.IndexOf('-');
            if (dash < 0)
            {
                if (!uint.TryParse(chunk, NumberStyles.None, CultureInfo.InvariantCulture, out uint single))
                {
                    return false;
                }

                ranges.Add(new LedgerRange(single, single));
                continue;
            }

            if (!uint.TryParse(chunk.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out uint from)
                || !uint.TryParse(chunk.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint to)
                || to < from)
            {
                return false;
            }

            ranges.Add(new LedgerRange(from, to));
        }

        if (ranges.Count == 0)
        {
            return false;
        }

        result = new LedgerRangeSet(ranges);
        return true;
    }

    /// <summary>
    /// True when one contiguous reported range contains the whole span. Adjacent ranges are deliberately not
    /// merged: rippled reports contiguous history as one range, so two ranges mean a real gap between them.
    /// </summary>
    public bool Covers(uint from, uint to)
    {
        if (to < from)
        {
            return true;
        }

        foreach (LedgerRange range in _ranges)
        {
            if (range.From <= from && range.To >= to)
            {
                return true;
            }
        }

        return false;
    }

    private readonly struct LedgerRange
    {
        public LedgerRange(uint from, uint to)
        {
            From = from;
            To = to;
        }

        public uint From { get; }

        public uint To { get; }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.LedgerRangeSetTests"
```

Expected: 12 tests pass (the `Theory` contributes 6).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: parse complete_ledgers so gappy nodes cannot fake a catch-up"
```

---

### Task 4: TransactionProcessor

The one place that decides whether a transaction is a payment and what was received. Pure: transaction in, result out, no I/O.

**Files:**
- Create: `src/Xrpl.PaymentGateway/Internal/ProcessingResult.cs`
- Create: `src/Xrpl.PaymentGateway/Internal/TransactionProcessor.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/Fakes/FixedTimeProvider.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/Fixtures/TransactionFixtures.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/TransactionProcessorTests.cs`

- [ ] **Step 1: Write the test time provider**

`tests/Xrpl.PaymentGateway.Tests/Fakes/FixedTimeProvider.cs`:

```csharp
namespace Xrpl.PaymentGateway.Tests.Fakes;

/// <summary>A clock frozen at a chosen instant, so assertions on timestamps are exact.</summary>
public sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
```

- [ ] **Step 2: Write the JSON fixtures**

These are the wire frames rippled pushes on an `accounts` subscription. Deserializing them exercises exactly the production path.

`tests/Xrpl.PaymentGateway.Tests/Fixtures/TransactionFixtures.cs`:

```csharp
using System.Text.Json;
using Xrpl.Client.Json;
using Xrpl.Models.Methods;
using Xrpl.Models.Subscriptions;

namespace Xrpl.PaymentGateway.Tests.Fixtures;

/// <summary>Canned rippled stream frames. Addresses are real-shaped so nothing chokes on address parsing.</summary>
public static class TransactionFixtures
{
    public const string Receiver = "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p";
    public const string Sender = "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF";
    public const string Issuer = "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa";

    /// <summary>Deserializes a frame the way the SDK's own stream pipeline does.</summary>
    public static IAccountTransaction Parse(string json) =>
        JsonSerializer.Deserialize<TransactionStream>(json, XrplJsonOptions.Default)
        ?? throw new InvalidOperationException("fixture did not deserialize");

    /// <summary>1 XRP delivered to the receiver, destination tag 42.</summary>
    public const string XrpPayment = """
    {
      "type": "transaction",
      "engine_result": "tesSUCCESS",
      "ledger_index": 100,
      "hash": "1111111111111111111111111111111111111111111111111111111111111111",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "delivered_amount": "1000000",
        "AffectedNodes": [
          {
            "ModifiedNode": {
              "LedgerEntryType": "AccountRoot",
              "LedgerIndex": "AAAA",
              "FinalFields": { "Account": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "Balance": "21000000" },
              "PreviousFields": { "Balance": "20000000" }
            }
          },
          {
            "ModifiedNode": {
              "LedgerEntryType": "AccountRoot",
              "LedgerIndex": "BBBB",
              "FinalFields": { "Account": "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF", "Balance": "29000000" },
              "PreviousFields": { "Balance": "30000012" }
            }
          }
        ]
      },
      "tx_json": {
        "Account": "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        "TransactionType": "Payment",
        "Destination": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p",
        "DestinationTag": 42,
        "Amount": "1000000",
        "Fee": "12",
        "Sequence": 7
      }
    }
    """;

    /// <summary>A partial payment: the Amount field says 1 XRP, the ledger delivered 100 drops.</summary>
    public const string PartialXrpPayment = """
    {
      "type": "transaction",
      "ledger_index": 101,
      "hash": "2222222222222222222222222222222222222222222222222222222222222222",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "delivered_amount": "100",
        "AffectedNodes": [
          {
            "ModifiedNode": {
              "LedgerEntryType": "AccountRoot",
              "LedgerIndex": "AAAA",
              "FinalFields": { "Account": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "Balance": "20000100" },
              "PreviousFields": { "Balance": "20000000" }
            }
          }
        ]
      },
      "tx_json": {
        "Account": "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        "TransactionType": "Payment",
        "Destination": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p",
        "DestinationTag": 7,
        "Amount": "1000000",
        "Flags": 131072,
        "Fee": "12",
        "Sequence": 8
      }
    }
    """;

    /// <summary>100 USD delivered to the receiver, who is the low account on the trust line.</summary>
    public const string IouPayment = """
    {
      "type": "transaction",
      "ledger_index": 102,
      "hash": "3333333333333333333333333333333333333333333333333333333333333333",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "AffectedNodes": [
          {
            "ModifiedNode": {
              "LedgerEntryType": "RippleState",
              "LedgerIndex": "CCCC",
              "FinalFields": {
                "Balance": { "currency": "USD", "issuer": "rrrrrrrrrrrrrrrrrrrrBZbvji", "value": "100" },
                "LowLimit": { "currency": "USD", "issuer": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "value": "1000000" },
                "HighLimit": { "currency": "USD", "issuer": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa", "value": "0" }
              },
              "PreviousFields": {
                "Balance": { "currency": "USD", "issuer": "rrrrrrrrrrrrrrrrrrrrBZbvji", "value": "0" }
              }
            }
          }
        ]
      },
      "tx_json": {
        "Account": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa",
        "TransactionType": "Payment",
        "Destination": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p",
        "DestinationTag": 99,
        "Amount": { "currency": "USD", "issuer": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa", "value": "100" },
        "Fee": "12",
        "Sequence": 3
      }
    }
    """;

    /// <summary>A failed transaction. Nothing moved, but the frame still arrives.</summary>
    public const string FailedPayment = """
    {
      "type": "transaction",
      "ledger_index": 103,
      "hash": "4444444444444444444444444444444444444444444444444444444444444444",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tecPATH_DRY",
        "AffectedNodes": []
      },
      "tx_json": {
        "Account": "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        "TransactionType": "Payment",
        "Destination": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p",
        "Amount": "1000000",
        "Fee": "12",
        "Sequence": 9
      }
    }
    """;

    /// <summary>The receiving account sending funds out. Its own action, never an incoming payment.</summary>
    public const string OutgoingPayment = """
    {
      "type": "transaction",
      "ledger_index": 104,
      "hash": "5555555555555555555555555555555555555555555555555555555555555555",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "AffectedNodes": [
          {
            "ModifiedNode": {
              "LedgerEntryType": "AccountRoot",
              "LedgerIndex": "AAAA",
              "FinalFields": { "Account": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "Balance": "19000000" },
              "PreviousFields": { "Balance": "20000000" }
            }
          }
        ]
      },
      "tx_json": {
        "Account": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p",
        "TransactionType": "Payment",
        "Destination": "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        "Amount": "1000000",
        "Fee": "12",
        "Sequence": 4
      }
    }
    """;

    /// <summary>Not yet validated. Provisional results must never be recorded.</summary>
    public const string UnvalidatedPayment = """
    {
      "type": "transaction",
      "ledger_current_index": 105,
      "hash": "6666666666666666666666666666666666666666666666666666666666666666",
      "validated": false,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "AffectedNodes": [
          {
            "ModifiedNode": {
              "LedgerEntryType": "AccountRoot",
              "LedgerIndex": "AAAA",
              "FinalFields": { "Account": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "Balance": "21000000" },
              "PreviousFields": { "Balance": "20000000" }
            }
          }
        ]
      },
      "tx_json": {
        "Account": "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        "TransactionType": "Payment",
        "Destination": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p",
        "Amount": "1000000",
        "Fee": "12",
        "Sequence": 10
      }
    }
    """;

    /// <summary>The receiver gives up XRP and gains USD — an exchange, not a receipt.</summary>
    public const string ExchangeWithDebit = """
    {
      "type": "transaction",
      "ledger_index": 106,
      "hash": "7777777777777777777777777777777777777777777777777777777777777777",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "AffectedNodes": [
          {
            "ModifiedNode": {
              "LedgerEntryType": "AccountRoot",
              "LedgerIndex": "AAAA",
              "FinalFields": { "Account": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "Balance": "19000000" },
              "PreviousFields": { "Balance": "20000000" }
            }
          },
          {
            "ModifiedNode": {
              "LedgerEntryType": "RippleState",
              "LedgerIndex": "CCCC",
              "FinalFields": {
                "Balance": { "currency": "USD", "issuer": "rrrrrrrrrrrrrrrrrrrrBZbvji", "value": "50" },
                "LowLimit": { "currency": "USD", "issuer": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "value": "1000000" },
                "HighLimit": { "currency": "USD", "issuer": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa", "value": "0" }
              },
              "PreviousFields": {
                "Balance": { "currency": "USD", "issuer": "rrrrrrrrrrrrrrrrrrrrBZbvji", "value": "0" }
              }
            }
          }
        ]
      },
      "tx_json": {
        "Account": "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        "TransactionType": "OfferCreate",
        "Fee": "12",
        "Sequence": 11
      }
    }
    """;

    /// <summary>Two assets credited at once. Physically odd for a receiving account, so it is an anomaly.</summary>
    public const string TwoAssetsCredited = """
    {
      "type": "transaction",
      "ledger_index": 107,
      "hash": "8888888888888888888888888888888888888888888888888888888888888888",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "AffectedNodes": [
          {
            "ModifiedNode": {
              "LedgerEntryType": "AccountRoot",
              "LedgerIndex": "AAAA",
              "FinalFields": { "Account": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "Balance": "20500000" },
              "PreviousFields": { "Balance": "20000000" }
            }
          },
          {
            "ModifiedNode": {
              "LedgerEntryType": "RippleState",
              "LedgerIndex": "CCCC",
              "FinalFields": {
                "Balance": { "currency": "USD", "issuer": "rrrrrrrrrrrrrrrrrrrrBZbvji", "value": "100" },
                "LowLimit": { "currency": "USD", "issuer": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "value": "1000000" },
                "HighLimit": { "currency": "USD", "issuer": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa", "value": "0" }
              },
              "PreviousFields": {
                "Balance": { "currency": "USD", "issuer": "rrrrrrrrrrrrrrrrrrrrBZbvji", "value": "0" }
              }
            }
          }
        ]
      },
      "tx_json": {
        "Account": "rnFApzSsKwXyTZtci4Z6nLVL8E1nLZzSBF",
        "TransactionType": "Payment",
        "Destination": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p",
        "Amount": "500000",
        "Fee": "12",
        "Sequence": 12
      }
    }
    """;

    /// <summary>Someone opens a trust line towards us. No balance moved.</summary>
    public const string TrustSetOnly = """
    {
      "type": "transaction",
      "ledger_index": 108,
      "hash": "9999999999999999999999999999999999999999999999999999999999999999",
      "validated": true,
      "meta": {
        "TransactionIndex": 0,
        "TransactionResult": "tesSUCCESS",
        "AffectedNodes": [
          {
            "CreatedNode": {
              "LedgerEntryType": "RippleState",
              "LedgerIndex": "DDDD",
              "NewFields": {
                "Balance": { "currency": "USD", "issuer": "rrrrrrrrrrrrrrrrrrrrBZbvji", "value": "0" },
                "LowLimit": { "currency": "USD", "issuer": "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p", "value": "0" },
                "HighLimit": { "currency": "USD", "issuer": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa", "value": "100" }
              }
            }
          }
        ]
      },
      "tx_json": {
        "Account": "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa",
        "TransactionType": "TrustSet",
        "Fee": "12",
        "Sequence": 5
      }
    }
    """;
}
```

- [ ] **Step 3: Write the failing tests**

`tests/Xrpl.PaymentGateway.Tests/TransactionProcessorTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Xrpl.Models.Methods;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xrpl.PaymentGateway.Tests.Fixtures;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class TransactionProcessorTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private static TransactionProcessor CreateProcessor() =>
        new TransactionProcessor(TransactionFixtures.Receiver, new FixedTimeProvider(Now), NullLogger.Instance);

    private static ProcessingResult Process(string fixture) =>
        CreateProcessor().Process(TransactionFixtures.Parse(fixture));

    [Fact]
    public void AnXrpPaymentIsRecordedInXrpNotDrops()
    {
        ProcessingResult result = Process(TransactionFixtures.XrpPayment);

        Assert.Equal(ProcessingResultKind.Recorded, result.Kind);
        PaymentRecord record = Assert.IsType<PaymentRecord>(result.Record);
        Assert.Equal("XRP", record.Currency);
        Assert.Null(record.Issuer);
        Assert.Equal(1m, record.Value);
        Assert.Equal(TransactionFixtures.Sender, record.Sender);
        Assert.Equal(42u, record.DestinationTag);
        Assert.Equal(100u, record.LedgerIndex);
        Assert.Equal("Payment", record.TransactionType);
        Assert.Equal(Now, record.ProcessedAt);
    }

    [Fact]
    public void APartialPaymentIsRecordedAtWhatTheLedgerDeliveredNotWhatAmountClaimed()
    {
        ProcessingResult result = Process(TransactionFixtures.PartialXrpPayment);

        Assert.Equal(ProcessingResultKind.Recorded, result.Kind);
        Assert.Equal(0.0001m, result.Record!.Value);
    }

    [Fact]
    public void AnIouPaymentCarriesTheCurrencyAndIssuer()
    {
        ProcessingResult result = Process(TransactionFixtures.IouPayment);

        Assert.Equal(ProcessingResultKind.Recorded, result.Kind);
        PaymentRecord record = result.Record!;
        Assert.Equal("USD", record.Currency);
        Assert.Equal(TransactionFixtures.Issuer, record.Issuer);
        Assert.Equal(100m, record.Value);
        Assert.Equal(99u, record.DestinationTag);
    }

    [Fact]
    public void AFailedTransactionIsSkipped()
    {
        ProcessingResult result = Process(TransactionFixtures.FailedPayment);

        Assert.Equal(ProcessingResultKind.Skipped, result.Kind);
        Assert.Null(result.Record);
    }

    [Fact]
    public void OurOwnOutgoingTransactionIsSkipped()
    {
        ProcessingResult result = Process(TransactionFixtures.OutgoingPayment);

        Assert.Equal(ProcessingResultKind.Skipped, result.Kind);
    }

    [Fact]
    public void AnUnvalidatedTransactionIsSkipped()
    {
        ProcessingResult result = Process(TransactionFixtures.UnvalidatedPayment);

        Assert.Equal(ProcessingResultKind.Skipped, result.Kind);
    }

    [Fact]
    public void ATrustSetThatMovesNoBalanceIsSkipped()
    {
        ProcessingResult result = Process(TransactionFixtures.TrustSetOnly);

        Assert.Equal(ProcessingResultKind.Skipped, result.Kind);
    }

    [Fact]
    public void ATransactionThatDebitsUsIsAnAnomalyAndIsNotRecorded()
    {
        ProcessingResult result = Process(TransactionFixtures.ExchangeWithDebit);

        Assert.Equal(ProcessingResultKind.Anomaly, result.Kind);
        Assert.Null(result.Record);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void TwoCreditedAssetsAreAnAnomalyAndTheLargestIsStillRecorded()
    {
        ProcessingResult result = Process(TransactionFixtures.TwoAssetsCredited);

        Assert.Equal(ProcessingResultKind.Anomaly, result.Kind);
        PaymentRecord record = Assert.IsType<PaymentRecord>(result.Record);
        Assert.Equal("USD", record.Currency);
        Assert.Equal(100m, record.Value);
    }

    [Fact]
    public void AProcessorForADifferentAddressSeesNothing()
    {
        TransactionProcessor processor = new TransactionProcessor(
            TransactionFixtures.Issuer, new FixedTimeProvider(Now), NullLogger.Instance);

        ProcessingResult result = processor.Process(TransactionFixtures.Parse(TransactionFixtures.XrpPayment));

        Assert.Equal(ProcessingResultKind.Skipped, result.Kind);
    }

    [Fact]
    public void ANullTransactionIsSkippedRatherThanThrowing()
    {
        ProcessingResult result = CreateProcessor().Process(null!);

        Assert.Equal(ProcessingResultKind.Skipped, result.Kind);
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.TransactionProcessorTests"
```

Expected: compile error, `TransactionProcessor` and `ProcessingResult` do not exist.

- [ ] **Step 5: Write `ProcessingResult`**

`src/Xrpl.PaymentGateway/Internal/ProcessingResult.cs`:

```csharp
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>What the processor decided about one transaction.</summary>
internal enum ProcessingResultKind
{
    /// <summary>Not a payment to us. Nothing to do.</summary>
    Skipped,

    /// <summary>A clean incoming payment.</summary>
    Recorded,

    /// <summary>Credited us in a shape a receiving account should not see. Always logged and counted.</summary>
    Anomaly,
}

/// <summary>The processor's verdict, plus the record when there is one.</summary>
internal sealed class ProcessingResult
{
    private ProcessingResult(ProcessingResultKind kind, PaymentRecord? record, string? reason)
    {
        Kind = kind;
        Record = record;
        Reason = reason;
    }

    public ProcessingResultKind Kind { get; }

    /// <summary>The payment to persist, or null when there is nothing to persist.</summary>
    public PaymentRecord? Record { get; }

    /// <summary>Why the transaction was skipped or flagged. Null for a clean record.</summary>
    public string? Reason { get; }

    public static ProcessingResult Skip(string reason) => new ProcessingResult(ProcessingResultKind.Skipped, null, reason);

    public static ProcessingResult Recorded(PaymentRecord record) => new ProcessingResult(ProcessingResultKind.Recorded, record, null);

    public static ProcessingResult Anomaly(PaymentRecord? record, string reason) => new ProcessingResult(ProcessingResultKind.Anomaly, record, reason);
}
```

- [ ] **Step 6: Write `TransactionProcessor`**

`src/Xrpl.PaymentGateway/Internal/TransactionProcessor.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xrpl.Models.Common;
using Xrpl.Models.Methods;
using Xrpl.Models.Transactions;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.Utils;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// Turns a validated transaction into a <see cref="PaymentRecord"/>, or explains why it is not one.
/// The amount comes from metadata balance deltas rather than the Amount field, so partial payments are
/// recorded at what actually arrived.
/// </summary>
internal sealed class TransactionProcessor
{
    private const string Success = "tesSUCCESS";

    private readonly string _receivingAddress;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public TransactionProcessor(string receivingAddress, TimeProvider timeProvider, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receivingAddress);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _receivingAddress = receivingAddress;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public ProcessingResult Process(IAccountTransaction? transaction)
    {
        if (transaction is null)
        {
            return ProcessingResult.Skip("no transaction");
        }

        if (!transaction.Validated)
        {
            return ProcessingResult.Skip("not validated");
        }

        TransactionResponse? tx = transaction.Transaction;
        if (tx is null)
        {
            return ProcessingResult.Skip("no transaction body");
        }

        Meta? meta = transaction.Meta;
        if (meta is null)
        {
            return ProcessingResult.Skip("no metadata");
        }

        if (!string.Equals(meta.TransactionResult, Success, StringComparison.Ordinal))
        {
            return ProcessingResult.Skip($"transaction result {meta.TransactionResult}");
        }

        if (string.Equals(tx.Account, _receivingAddress, StringComparison.Ordinal))
        {
            return ProcessingResult.Skip("sent by the receiving account itself");
        }

        string? hash = transaction.Hash;
        if (string.IsNullOrEmpty(hash))
        {
            return ProcessingResult.Skip("no transaction hash");
        }

        if (transaction.LedgerIndex is not { } ledgerIndex || ledgerIndex == 0 || ledgerIndex > uint.MaxValue)
        {
            return ProcessingResult.Skip("no usable ledger index");
        }

        Dictionary<string, List<Currency>> changes = BalanceChanges.GetBalanceChanges(meta);
        if (!changes.TryGetValue(_receivingAddress, out List<Currency>? ours) || ours.Count == 0)
        {
            return ProcessingResult.Skip("no balance change for the receiving account");
        }

        List<(Currency Currency, decimal Value)> credits = new List<(Currency, decimal)>();
        bool debited = false;
        foreach (Currency currency in ours)
        {
            decimal value = ToHumanUnits(currency);
            if (value > 0m)
            {
                credits.Add((currency, value));
            }
            else if (value < 0m)
            {
                debited = true;
            }
        }

        if (debited)
        {
            string reason = $"transaction {hash} both credits and debits the receiving account; it is an exchange or a rippling path, not an incoming payment, and was not recorded";
            _logger.LogError("payment anomaly: {Reason}", reason);
            return ProcessingResult.Anomaly(null, reason);
        }

        if (credits.Count == 0)
        {
            return ProcessingResult.Skip("no positive balance change");
        }

        (Currency currency, decimal value) = credits.OrderByDescending(candidate => candidate.Value).First();
        bool isXrp = currency.IsXrp();

        PaymentRecord record = new PaymentRecord
        {
            TransactionHash = hash,
            TransactionType = tx.TransactionType.ToString(),
            Sender = tx.Account,
            DestinationTag = ReadDestinationTag(tx),
            Currency = isXrp ? "XRP" : currency.CurrencyCode,
            Issuer = isXrp ? null : currency.Issuer,
            Value = value,
            LedgerIndex = (uint)ledgerIndex,
            ProcessedAt = _timeProvider.GetUtcNow(),
        };

        if (credits.Count > 1)
        {
            string reason = $"transaction {hash} credited {credits.Count} assets; recorded the largest ({record.Value} {record.Currency})";
            _logger.LogError("payment anomaly: {Reason}", reason);
            return ProcessingResult.Anomaly(record, reason);
        }

        return ProcessingResult.Recorded(record);
    }

    /// <summary>XRP deltas arrive in drops on <c>ValueAsNumber</c>; <c>ValueAsXrp</c> is the human amount.</summary>
    private static decimal ToHumanUnits(Currency currency) =>
        currency.IsXrp() ? currency.ValueAsXrp ?? 0m : currency.ValueAsNumber;

    /// <summary>
    /// DestinationTag lives on Payment, not on the base transaction. The extension-data fallback keeps
    /// tags readable on transaction types the SDK maps to the generic response.
    /// </summary>
    private static uint? ReadDestinationTag(TransactionResponse tx)
    {
        if (tx is IPayment payment && payment.DestinationTag is { } tag)
        {
            return tag;
        }

        if (tx.UnknownFields is { } unknown
            && unknown.TryGetValue("DestinationTag", out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetUInt32(out uint raw))
        {
            return raw;
        }

        return null;
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.TransactionProcessorTests"
```

Expected: 11 tests pass. If `AnIouPaymentCarriesTheCurrencyAndIssuer` reports the counterparty rather than the issuer, that is correct behaviour and the fixture is wrong, not the code: `BalanceChanges` reports the trust line counterparty as `Issuer`, which for a receiving account that is not itself an issuer is the token issuer.

- [ ] **Step 8: Commit**

```bash
git add -A && git commit -m "feat: derive payments from metadata balance changes"
```

---

### Task 5: PaymentDispatcher and StoreRetryPolicy

Two different failure policies meet here. A store failure must never lose a payment, so it is retried forever. A host handler failure must never block recording, so it is logged and left for reconciliation. Keeping them in separate methods is what makes that possible.

**Files:**
- Create: `src/Xrpl.PaymentGateway/Internal/PaymentDispatcher.cs`
- Create: `src/Xrpl.PaymentGateway/Internal/StoreRetryPolicy.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/Fakes/RecordingHandler.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/Fakes/FlakyPaymentStore.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/PaymentDispatcherTests.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/StoreRetryPolicyTests.cs`

- [ ] **Step 1: Write the test doubles**

`tests/Xrpl.PaymentGateway.Tests/Fakes/RecordingHandler.cs`:

```csharp
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Tests.Fakes;

/// <summary>Captures every delivery, and can be told to throw.</summary>
public sealed class RecordingHandler : IPaymentReceivedHandler
{
    private readonly List<(PaymentRecord Payment, string? BuyerId)> _deliveries = new List<(PaymentRecord, string?)>();

    public IReadOnlyList<(PaymentRecord Payment, string? BuyerId)> Deliveries
    {
        get
        {
            lock (_deliveries)
            {
                return _deliveries.ToList();
            }
        }
    }

    public bool Throws { get; set; }

    public Task OnPaymentReceivedAsync(PaymentRecord payment, string? buyerId, CancellationToken cancellationToken)
    {
        lock (_deliveries)
        {
            _deliveries.Add((payment, buyerId));
        }

        return Throws ? Task.FromException(new InvalidOperationException("handler blew up")) : Task.CompletedTask;
    }
}
```

`tests/Xrpl.PaymentGateway.Tests/Fakes/FlakyPaymentStore.cs`:

```csharp
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Tests.Fakes;

/// <summary>Wraps a real store and fails the first N calls to TryAddPaymentAsync.</summary>
public sealed class FlakyPaymentStore : IPaymentStore
{
    private readonly IPaymentStore _inner;
    private int _remainingFailures;

    public FlakyPaymentStore(IPaymentStore inner, int failures)
    {
        _inner = inner;
        _remainingFailures = failures;
    }

    public int AddAttempts { get; private set; }

    public Task<uint> GetOrAssignTagAsync(string buyerId, CancellationToken cancellationToken) =>
        _inner.GetOrAssignTagAsync(buyerId, cancellationToken);

    public Task<string?> FindBuyerByTagAsync(uint tag, CancellationToken cancellationToken) =>
        _inner.FindBuyerByTagAsync(tag, cancellationToken);

    public Task<bool> TryAddPaymentAsync(PaymentRecord record, CancellationToken cancellationToken)
    {
        AddAttempts++;
        if (_remainingFailures > 0)
        {
            _remainingFailures--;
            return Task.FromException<bool>(new TimeoutException("store unavailable"));
        }

        return _inner.TryAddPaymentAsync(record, cancellationToken);
    }

    public Task MarkHandledAsync(string transactionHash, CancellationToken cancellationToken) =>
        _inner.MarkHandledAsync(transactionHash, cancellationToken);

    public Task<IReadOnlyList<PaymentRecord>> GetUnhandledPaymentsAsync(int limit, CancellationToken cancellationToken) =>
        _inner.GetUnhandledPaymentsAsync(limit, cancellationToken);

    public Task<uint?> GetLastProcessedLedgerAsync(CancellationToken cancellationToken) =>
        _inner.GetLastProcessedLedgerAsync(cancellationToken);

    public Task SetLastProcessedLedgerAsync(uint ledgerIndex, CancellationToken cancellationToken) =>
        _inner.SetLastProcessedLedgerAsync(ledgerIndex, cancellationToken);
}
```

- [ ] **Step 2: Write the failing dispatcher tests**

`tests/Xrpl.PaymentGateway.Tests/PaymentDispatcherTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class PaymentDispatcherTests
{
    private static PaymentRecord Record(string hash = "HASH-1", uint? tag = 1) => new PaymentRecord
    {
        TransactionHash = hash,
        TransactionType = "Payment",
        Sender = "rSender",
        DestinationTag = tag,
        Currency = "XRP",
        Value = 5m,
        LedgerIndex = 10,
        ProcessedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task ANewRecordIsStoredAndReported()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();
        PaymentDispatcher dispatcher = new PaymentDispatcher(store, new RecordingHandler(), NullLogger.Instance);

        bool isNew = await dispatcher.RecordAsync(Record(), TestContext.Current.CancellationToken);

        Assert.True(isNew);
    }

    [Fact]
    public async Task ADuplicateRecordIsReportedAsNotNew()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();
        PaymentDispatcher dispatcher = new PaymentDispatcher(store, new RecordingHandler(), NullLogger.Instance);
        await dispatcher.RecordAsync(Record(), TestContext.Current.CancellationToken);

        bool isNew = await dispatcher.RecordAsync(Record(), TestContext.Current.CancellationToken);

        Assert.False(isNew);
    }

    [Fact]
    public async Task AStoreFailureOnRecordPropagatesSoTheCallerCanRetry()
    {
        FlakyPaymentStore store = new FlakyPaymentStore(new InMemoryPaymentStore(), failures: 1);
        PaymentDispatcher dispatcher = new PaymentDispatcher(store, new RecordingHandler(), NullLogger.Instance);

        await Assert.ThrowsAsync<TimeoutException>(
            () => dispatcher.RecordAsync(Record(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeliveryResolvesTheBuyerFromTheDestinationTagAndMarksTheRecordHandled()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();
        uint tag = await store.GetOrAssignTagAsync("buyer-9", TestContext.Current.CancellationToken);
        RecordingHandler handler = new RecordingHandler();
        PaymentDispatcher dispatcher = new PaymentDispatcher(store, handler, NullLogger.Instance);
        PaymentRecord record = Record(tag: tag);
        await dispatcher.RecordAsync(record, TestContext.Current.CancellationToken);

        await dispatcher.DeliverAsync(record, TestContext.Current.CancellationToken);

        Assert.Equal("buyer-9", Assert.Single(handler.Deliveries).BuyerId);
        Assert.Empty(await store.GetUnhandledPaymentsAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APaymentWithAnUnknownTagIsStillDeliveredWithoutABuyer()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();
        RecordingHandler handler = new RecordingHandler();
        PaymentDispatcher dispatcher = new PaymentDispatcher(store, handler, NullLogger.Instance);
        PaymentRecord record = Record(tag: 555);
        await dispatcher.RecordAsync(record, TestContext.Current.CancellationToken);

        await dispatcher.DeliverAsync(record, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(handler.Deliveries).BuyerId);
    }

    [Fact]
    public async Task AHandlerExceptionLeavesTheRecordUnhandledAndDoesNotEscape()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();
        RecordingHandler handler = new RecordingHandler { Throws = true };
        PaymentDispatcher dispatcher = new PaymentDispatcher(store, handler, NullLogger.Instance);
        PaymentRecord record = Record(tag: null);
        await dispatcher.RecordAsync(record, TestContext.Current.CancellationToken);

        await dispatcher.DeliverAsync(record, TestContext.Current.CancellationToken);

        Assert.Single(await store.GetUnhandledPaymentsAsync(10, TestContext.Current.CancellationToken));
    }
}
```

- [ ] **Step 3: Write the failing retry-policy tests**

`tests/Xrpl.PaymentGateway.Tests/StoreRetryPolicyTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class StoreRetryPolicyTests
{
    private static StoreRetryPolicy CreatePolicy(Action<bool>? onAvailabilityChanged = null) =>
        new StoreRetryPolicy(
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(5),
            TimeProvider.System,
            NullLogger.Instance,
            onAvailabilityChanged);

    [Fact]
    public async Task ASucceedingOperationRunsOnce()
    {
        int calls = 0;
        StoreRetryPolicy policy = CreatePolicy();

        int result = await policy.ExecuteAsync(_ => { calls++; return Task.FromResult(7); }, "op", TestContext.Current.CancellationToken);

        Assert.Equal(7, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AFailingOperationIsRetriedUntilItSucceeds()
    {
        int calls = 0;
        StoreRetryPolicy policy = CreatePolicy();

        int result = await policy.ExecuteAsync(
            _ =>
            {
                calls++;
                return calls < 4 ? Task.FromException<int>(new TimeoutException()) : Task.FromResult(11);
            },
            "op",
            TestContext.Current.CancellationToken);

        Assert.Equal(11, result);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task AvailabilityIsReportedFalseOnTheFirstFailureAndTrueOnRecovery()
    {
        List<bool> availability = new List<bool>();
        int calls = 0;
        StoreRetryPolicy policy = CreatePolicy(availability.Add);

        await policy.ExecuteAsync(
            _ =>
            {
                calls++;
                return calls < 2 ? Task.FromException<int>(new TimeoutException()) : Task.FromResult(1);
            },
            "op",
            TestContext.Current.CancellationToken);

        Assert.Equal(new[] { false, true }, availability);
    }

    [Fact]
    public async Task CancellationStopsTheRetryLoop()
    {
        using CancellationTokenSource cts = new CancellationTokenSource();
        StoreRetryPolicy policy = CreatePolicy();

        Task<int> pending = policy.ExecuteAsync(
            _ => Task.FromException<int>(new TimeoutException()),
            "op",
            cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
```

- [ ] **Step 4: Run both test classes to verify they fail**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.PaymentDispatcherTests" -class "Xrpl.PaymentGateway.Tests.StoreRetryPolicyTests"
```

Expected: compile error, `PaymentDispatcher` and `StoreRetryPolicy` do not exist.

- [ ] **Step 5: Write `StoreRetryPolicy`**

`src/Xrpl.PaymentGateway/Internal/StoreRetryPolicy.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// Retries store operations until they succeed or the token is cancelled. There is no attempt limit on
/// purpose: the store is the source of truth, and giving up means losing a payment. Callers pause while
/// this runs, which is what freezes the ledger cursor during a store outage.
/// </summary>
internal sealed class StoreRetryPolicy
{
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Action<bool>? _onAvailabilityChanged;

    public StoreRetryPolicy(
        TimeSpan baseDelay,
        TimeSpan maxDelay,
        TimeProvider timeProvider,
        ILogger logger,
        Action<bool>? onAvailabilityChanged = null)
    {
        _baseDelay = baseDelay;
        _maxDelay = maxDelay;
        _timeProvider = timeProvider;
        _logger = logger;
        _onAvailabilityChanged = onAvailabilityChanged;
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        int attempt = 0;
        bool reportedUnavailable = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                T result = await operation(cancellationToken).ConfigureAwait(false);
                if (reportedUnavailable)
                {
                    _logger.LogInformation("store recovered on {Operation} after {Attempts} failed attempts", operationName, attempt);
                    _onAvailabilityChanged?.Invoke(true);
                }

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                attempt++;
                if (!reportedUnavailable)
                {
                    reportedUnavailable = true;
                    _onAvailabilityChanged?.Invoke(false);
                }

                _logger.LogError(ex, "store operation {Operation} failed on attempt {Attempt}; retrying", operationName, attempt);
                await Task.Delay(NextDelay(attempt), _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        string operationName,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            operationName,
            cancellationToken);

    private TimeSpan NextDelay(int attempt)
    {
        double exponent = Math.Min(attempt - 1, 16);
        double milliseconds = _baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        double capped = Math.Min(milliseconds, _maxDelay.TotalMilliseconds);
        double jittered = capped * (0.75 + (Random.Shared.NextDouble() * 0.5));
        return TimeSpan.FromMilliseconds(Math.Min(jittered, _maxDelay.TotalMilliseconds));
    }
}
```

- [ ] **Step 6: Write `PaymentDispatcher`**

`src/Xrpl.PaymentGateway/Internal/PaymentDispatcher.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// The only component that talks to both the store and the host handler.
/// <see cref="RecordAsync"/> throws on store failures so the caller can retry them.
/// <see cref="DeliverAsync"/> never throws: a broken handler must not stop the ledger being followed.
/// </summary>
internal sealed class PaymentDispatcher
{
    private readonly IPaymentStore _store;
    private readonly IPaymentReceivedHandler _handler;
    private readonly ILogger _logger;

    public PaymentDispatcher(IPaymentStore store, IPaymentReceivedHandler handler, ILogger logger)
    {
        _store = store;
        _handler = handler;
        _logger = logger;
    }

    /// <summary>Persists the record. Returns false when the hash was already stored.</summary>
    public Task<bool> RecordAsync(PaymentRecord record, CancellationToken cancellationToken) =>
        _store.TryAddPaymentAsync(record, cancellationToken);

    /// <summary>Resolves the buyer, hands the payment to the host, and marks it handled on success.</summary>
    public async Task DeliverAsync(PaymentRecord record, CancellationToken cancellationToken)
    {
        string? buyerId = null;

        try
        {
            if (record.DestinationTag is { } tag)
            {
                buyerId = await _store.FindBuyerByTagAsync(tag, cancellationToken).ConfigureAwait(false);
            }

            await _handler.OnPaymentReceivedAsync(record, buyerId, cancellationToken).ConfigureAwait(false);
            await _store.MarkHandledAsync(record.TransactionHash, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "payment {Hash} of {Value} {Currency} from {Sender} delivered (buyer {Buyer})",
                record.TransactionHash,
                record.Value,
                record.Currency,
                record.Sender,
                buyerId ?? "unknown");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "delivering payment {Hash} failed; it stays unhandled and reconciliation will retry it",
                record.TransactionHash);
        }
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.PaymentDispatcherTests" -class "Xrpl.PaymentGateway.Tests.StoreRetryPolicyTests"
```

Expected: 10 tests pass.

- [ ] **Step 8: Commit**

```bash
git add -A && git commit -m "feat: separate store retries from handler delivery failures"
```

---

### Task 6: Node connection abstraction, pool and fake

Everything the monitor needs from a node, behind an interface small enough to fake. The real implementation over `XrplClient` comes later in Task 11 — the state machine is built and tested against the fake first.

**Files:**
- Create: `src/Xrpl.PaymentGateway/Internal/NodeStatus.cs`
- Create: `src/Xrpl.PaymentGateway/Internal/AccountTransactionQuery.cs`
- Create: `src/Xrpl.PaymentGateway/Internal/AccountTransactionPage.cs`
- Create: `src/Xrpl.PaymentGateway/Internal/IXrplNodeConnection.cs`
- Create: `src/Xrpl.PaymentGateway/Internal/IXrplNodeConnectionFactory.cs`
- Create: `src/Xrpl.PaymentGateway/Internal/NodePool.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/Fakes/FakeXrplNodeConnection.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/NodePoolTests.cs`

- [ ] **Step 1: Write the failing pool tests**

`tests/Xrpl.PaymentGateway.Tests/NodePoolTests.cs`:

```csharp
using Xrpl.PaymentGateway.Internal;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class NodePoolTests
{
    private static readonly Uri NodeA = new Uri("ws://a:6006");
    private static readonly Uri NodeB = new Uri("ws://b:6006");
    private static readonly Uri NodeC = new Uri("ws://c:6006");

    [Fact]
    public void NextWalksTheNodesInOrderAndWrapsAround()
    {
        NodePool pool = new NodePool(new[] { NodeA, NodeB, NodeC });

        Assert.Equal(NodeA, pool.Next());
        Assert.Equal(NodeB, pool.Next());
        Assert.Equal(NodeC, pool.Next());
        Assert.Equal(NodeA, pool.Next());
    }

    [Fact]
    public void PeekShowsTheNodeNextWouldReturnWithoutConsumingIt()
    {
        NodePool pool = new NodePool(new[] { NodeA, NodeB });
        pool.Next();

        Assert.Equal(NodeB, pool.Peek());
        Assert.Equal(NodeB, pool.Next());
    }

    [Fact]
    public void ASingleNodePoolPeeksAtItself()
    {
        NodePool pool = new NodePool(new[] { NodeA });
        pool.Next();

        Assert.Equal(NodeA, pool.Peek());
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void AnEmptyPoolIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new NodePool(Array.Empty<Uri>()));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.NodePoolTests"
```

Expected: compile error, `NodePool` does not exist.

- [ ] **Step 3: Write the four small internal types**

`src/Xrpl.PaymentGateway/Internal/NodeStatus.cs`:

```csharp
namespace Xrpl.PaymentGateway.Internal;

/// <summary>The parts of <c>server_info</c> the monitor reasons about.</summary>
internal sealed class NodeStatus
{
    /// <summary>Lower-cased <c>server_state</c>, e.g. "full", "syncing", "proposing".</summary>
    public required string ServerState { get; init; }

    /// <summary>Sequence of the node's latest validated ledger, or null when it has none.</summary>
    public uint? ValidatedLedgerIndex { get; init; }

    /// <summary>Raw <c>complete_ledgers</c> string, fed to <see cref="LedgerRangeSet"/>.</summary>
    public string? CompleteLedgers { get; init; }

    /// <summary>True when the node is in sync with the network and its view can be trusted.</summary>
    public bool IsSynced =>
        ServerState is "full" or "validating" or "proposing";
}
```

`src/Xrpl.PaymentGateway/Internal/AccountTransactionQuery.cs`:

```csharp
namespace Xrpl.PaymentGateway.Internal;

/// <summary>One page request against <c>account_tx</c>.</summary>
internal sealed class AccountTransactionQuery
{
    public required string Account { get; init; }

    public required uint LedgerIndexMin { get; init; }

    public required uint LedgerIndexMax { get; init; }

    public int Limit { get; init; } = 200;

    /// <summary>Opaque continuation token from the previous page, or null for the first page.</summary>
    public object? Marker { get; init; }
}
```

`src/Xrpl.PaymentGateway/Internal/AccountTransactionPage.cs`:

```csharp
using Xrpl.Models.Methods;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>One page of <c>account_tx</c> results, including the range the node actually searched.</summary>
internal sealed class AccountTransactionPage
{
    public required IReadOnlyList<IAccountTransaction> Transactions { get; init; }

    /// <summary>Continuation token, or null when this was the last page.</summary>
    public object? Marker { get; init; }

    /// <summary>Echoed lower bound. Higher than requested means the node searched less than we asked.</summary>
    public required uint LedgerIndexMin { get; init; }

    /// <summary>Echoed upper bound. Lower than requested means the node searched less than we asked.</summary>
    public required uint LedgerIndexMax { get; init; }
}
```

`src/Xrpl.PaymentGateway/Internal/IXrplNodeConnection.cs`:

```csharp
using Xrpl.Models.Methods;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// One session against one node. Callbacks are single-consumer properties rather than events: the monitor
/// is the only consumer, and a multicast event would hide which subscriber's task is being awaited.
/// </summary>
internal interface IXrplNodeConnection : IAsyncDisposable
{
    Uri Node { get; }

    /// <summary>Raised for every transaction affecting the subscribed account.</summary>
    Func<IAccountTransaction, Task>? OnTransaction { get; set; }

    /// <summary>Raised with the index of each newly closed ledger.</summary>
    Func<ulong, Task>? OnLedgerClosed { get; set; }

    /// <summary>Raised once when the session is over and the subscriptions are gone.</summary>
    Func<string, Task>? OnSessionEnded { get; set; }

    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Subscribes to the account plus the ledger stream.</summary>
    Task SubscribeToAccountAsync(string account, CancellationToken cancellationToken);

    Task<NodeStatus> GetNodeStatusAsync(CancellationToken cancellationToken);

    Task<AccountTransactionPage> GetAccountTransactionsAsync(AccountTransactionQuery query, CancellationToken cancellationToken);
}
```

`src/Xrpl.PaymentGateway/Internal/IXrplNodeConnectionFactory.cs`:

```csharp
namespace Xrpl.PaymentGateway.Internal;

/// <summary>Creates a fresh session per node. The monitor disposes each one it opens.</summary>
internal interface IXrplNodeConnectionFactory
{
    IXrplNodeConnection Create(Uri node);
}
```

- [ ] **Step 4: Write `NodePool`**

`src/Xrpl.PaymentGateway/Internal/NodePool.cs`:

```csharp
namespace Xrpl.PaymentGateway.Internal;

/// <summary>Round-robin over the allowed nodes. Not thread-safe; only the monitor loop touches it.</summary>
internal sealed class NodePool
{
    private readonly IReadOnlyList<Uri> _nodes;
    private int _index = -1;

    public NodePool(IReadOnlyList<Uri> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0)
        {
            throw new ArgumentException("at least one node is required", nameof(nodes));
        }

        _nodes = nodes;
    }

    public int Count => _nodes.Count;

    public IReadOnlyList<Uri> Nodes => _nodes;

    /// <summary>Advances to the next node and returns it.</summary>
    public Uri Next()
    {
        _index = (_index + 1) % _nodes.Count;
        return _nodes[_index];
    }

    /// <summary>The node <see cref="Next"/> would return, without advancing.</summary>
    public Uri Peek() => _nodes[(_index + 1) % _nodes.Count];
}
```

- [ ] **Step 5: Write the fake connection**

`tests/Xrpl.PaymentGateway.Tests/Fakes/FakeXrplNodeConnection.cs`:

```csharp
using Xrpl.Models.Methods;
using Xrpl.PaymentGateway.Internal;

namespace Xrpl.PaymentGateway.Tests.Fakes;

/// <summary>
/// A scriptable stand-in for a node session. Tests drive the callbacks by hand.
/// This must be internal: its members expose <c>NodeStatus</c> and the query types, which are internal,
/// and a public member may not expose a less accessible type (CS0050/CS0053). xUnit discovers internal
/// test classes and fixtures without complaint.
/// </summary>
internal sealed class FakeXrplNodeConnection : IXrplNodeConnection
{
    private readonly Queue<AccountTransactionPage> _pages = new Queue<AccountTransactionPage>();

    public FakeXrplNodeConnection(Uri node) => Node = node;

    public Uri Node { get; }

    public Func<IAccountTransaction, Task>? OnTransaction { get; set; }

    public Func<ulong, Task>? OnLedgerClosed { get; set; }

    public Func<string, Task>? OnSessionEnded { get; set; }

    public NodeStatus Status { get; set; } = new NodeStatus
    {
        ServerState = "full",
        ValidatedLedgerIndex = 1000,
        CompleteLedgers = "1-1000",
    };

    public bool Connected { get; private set; }

    public bool Disposed { get; private set; }

    public string? SubscribedAccount { get; private set; }

    public int StatusCalls { get; private set; }

    public List<AccountTransactionQuery> Queries { get; } = new List<AccountTransactionQuery>();

    public Exception? ConnectFailure { get; set; }

    public void EnqueuePage(AccountTransactionPage page) => _pages.Enqueue(page);

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (ConnectFailure is { } failure)
        {
            return Task.FromException(failure);
        }

        Connected = true;
        return Task.CompletedTask;
    }

    public Task SubscribeToAccountAsync(string account, CancellationToken cancellationToken)
    {
        SubscribedAccount = account;
        return Task.CompletedTask;
    }

    public Task<NodeStatus> GetNodeStatusAsync(CancellationToken cancellationToken)
    {
        StatusCalls++;
        return Task.FromResult(Status);
    }

    public Task<AccountTransactionPage> GetAccountTransactionsAsync(AccountTransactionQuery query, CancellationToken cancellationToken)
    {
        Queries.Add(query);
        if (_pages.Count == 0)
        {
            return Task.FromResult(new AccountTransactionPage
            {
                Transactions = Array.Empty<IAccountTransaction>(),
                Marker = null,
                LedgerIndexMin = query.LedgerIndexMin,
                LedgerIndexMax = query.LedgerIndexMax,
            });
        }

        return Task.FromResult(_pages.Dequeue());
    }

    /// <summary>Pushes a transaction the way a live subscription would.</summary>
    public Task PushTransactionAsync(IAccountTransaction transaction) =>
        OnTransaction?.Invoke(transaction) ?? Task.CompletedTask;

    /// <summary>Pushes a ledger close the way the ledger stream would.</summary>
    public Task PushLedgerAsync(ulong ledgerIndex) =>
        OnLedgerClosed?.Invoke(ledgerIndex) ?? Task.CompletedTask;

    /// <summary>Ends the session the way a dropped socket would.</summary>
    public Task EndSessionAsync(string reason) =>
        OnSessionEnded?.Invoke(reason) ?? Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Connected = false;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Hands out pre-built fakes per node URI, creating one on first request.</summary>
internal sealed class FakeXrplNodeConnectionFactory : IXrplNodeConnectionFactory
{
    private readonly Dictionary<Uri, FakeXrplNodeConnection> _connections = new Dictionary<Uri, FakeXrplNodeConnection>();

    public List<Uri> CreatedFor { get; } = new List<Uri>();

    public FakeXrplNodeConnection For(Uri node)
    {
        if (!_connections.TryGetValue(node, out FakeXrplNodeConnection? connection))
        {
            connection = new FakeXrplNodeConnection(node);
            _connections[node] = connection;
        }

        return connection;
    }

    public IXrplNodeConnection Create(Uri node)
    {
        CreatedFor.Add(node);
        return For(node);
    }
}
```

The fake ignores `DisposeAsync` for reuse across sessions on purpose: a test that rotates nodes and comes back needs the same instance to still answer.

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.NodePoolTests"
```

Expected: 4 tests pass, and the whole solution still builds.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat: add node session abstraction, round-robin pool and test fake"
```

---

### Task 7: CatchUpRunner

The half of the design that closes gaps a stream cannot. It refuses to declare success on a node that did not search the whole range.

**Files:**
- Create: `src/Xrpl.PaymentGateway/Internal/CatchUpResult.cs`
- Create: `src/Xrpl.PaymentGateway/Internal/CatchUpRunner.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/CatchUpRunnerTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Xrpl.PaymentGateway.Tests/CatchUpRunnerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Xrpl.Models.Methods;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xrpl.PaymentGateway.Tests.Fixtures;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class CatchUpRunnerTests
{
    private static readonly Uri Node = new Uri("ws://node:6006");

    private static AccountTransactionPage Page(uint min, uint max, object? marker, params IAccountTransaction[] transactions) =>
        new AccountTransactionPage
        {
            Transactions = transactions,
            Marker = marker,
            LedgerIndexMin = min,
            LedgerIndexMax = max,
        };

    [Fact]
    public async Task AnEmptyRangeCompletesWithoutTouchingTheNode()
    {
        FakeXrplNodeConnection connection = new FakeXrplNodeConnection(Node);
        CatchUpRunner runner = new CatchUpRunner(NullLogger.Instance);

        CatchUpResult result = await runner.RunAsync(
            connection, "rAccount", 200, 100, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.True(result.Completed);
        Assert.Equal(0, result.ProcessedCount);
        Assert.Equal(0, connection.StatusCalls);
    }

    [Fact]
    public async Task EveryTransactionInEveryPageReachesTheSink()
    {
        FakeXrplNodeConnection connection = new FakeXrplNodeConnection(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 500, CompleteLedgers = "1-500" };
        connection.EnqueuePage(Page(100, 200, "marker-1", TransactionFixtures.Parse(TransactionFixtures.XrpPayment)));
        connection.EnqueuePage(Page(100, 200, null, TransactionFixtures.Parse(TransactionFixtures.IouPayment)));
        List<IAccountTransaction> seen = new List<IAccountTransaction>();
        CatchUpRunner runner = new CatchUpRunner(NullLogger.Instance);

        CatchUpResult result = await runner.RunAsync(
            connection, "rAccount", 100, 200, (tx, _) => { seen.Add(tx); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.True(result.Completed);
        Assert.Equal(2, result.ProcessedCount);
        Assert.Equal(2, seen.Count);
        Assert.Equal(2, connection.Queries.Count);
        Assert.Null(connection.Queries[0].Marker);
        Assert.Equal("marker-1", connection.Queries[1].Marker);
    }

    [Fact]
    public async Task ANodeWhoseHistoryDoesNotCoverTheRangeIsRefusedBeforeAnyQuery()
    {
        FakeXrplNodeConnection connection = new FakeXrplNodeConnection(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 500, CompleteLedgers = "300-500" };
        CatchUpRunner runner = new CatchUpRunner(NullLogger.Instance);

        CatchUpResult result = await runner.RunAsync(
            connection, "rAccount", 100, 200, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Contains("300-500", result.Reason);
        Assert.Empty(connection.Queries);
    }

    [Fact]
    public async Task ANodeThatSearchedANarrowerRangeThanAskedIsRefused()
    {
        FakeXrplNodeConnection connection = new FakeXrplNodeConnection(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 500, CompleteLedgers = "1-500" };
        connection.EnqueuePage(Page(150, 200, null));
        CatchUpRunner runner = new CatchUpRunner(NullLogger.Instance);

        CatchUpResult result = await runner.RunAsync(
            connection, "rAccount", 100, 200, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Contains("150", result.Reason);
    }

    [Fact]
    public async Task AnUnparseableCompleteLedgersStringIsRefused()
    {
        FakeXrplNodeConnection connection = new FakeXrplNodeConnection(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 500, CompleteLedgers = "empty" };
        CatchUpRunner runner = new CatchUpRunner(NullLogger.Instance);

        CatchUpResult result = await runner.RunAsync(
            connection, "rAccount", 100, 200, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
    }

    [Fact]
    public async Task TheQueryCarriesTheRequestedBoundsAndTheAccount()
    {
        FakeXrplNodeConnection connection = new FakeXrplNodeConnection(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 500, CompleteLedgers = "1-500" };
        CatchUpRunner runner = new CatchUpRunner(NullLogger.Instance);

        await runner.RunAsync(connection, "rAccount", 100, 200, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        AccountTransactionQuery query = Assert.Single(connection.Queries);
        Assert.Equal("rAccount", query.Account);
        Assert.Equal(100u, query.LedgerIndexMin);
        Assert.Equal(200u, query.LedgerIndexMax);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.CatchUpRunnerTests"
```

Expected: compile error, `CatchUpRunner` does not exist.

- [ ] **Step 3: Write `CatchUpResult`**

`src/Xrpl.PaymentGateway/Internal/CatchUpResult.cs`:

```csharp
namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// Outcome of replaying a ledger range. <see cref="Completed"/> is the only thing that authorises the
/// cursor to move: it means every ledger in the range was genuinely searched.
/// </summary>
internal sealed class CatchUpResult
{
    private CatchUpResult(bool completed, int processedCount, string? reason)
    {
        Completed = completed;
        ProcessedCount = processedCount;
        Reason = reason;
    }

    public bool Completed { get; }

    public int ProcessedCount { get; }

    /// <summary>Why the replay could not be trusted. Null when it completed.</summary>
    public string? Reason { get; }

    public static CatchUpResult Complete(int processedCount) => new CatchUpResult(true, processedCount, null);

    public static CatchUpResult Incomplete(string reason) => new CatchUpResult(false, 0, reason);
}
```

- [ ] **Step 4: Write `CatchUpRunner`**

`src/Xrpl.PaymentGateway/Internal/CatchUpRunner.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Xrpl.Models.Methods;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// Replays <c>account_tx</c> over a ledger range. A node with a hole in its history answers a range query
/// with a silently partial result, so this checks twice: <c>complete_ledgers</c> before asking, and the
/// range the node echoes back after asking.
/// </summary>
internal sealed class CatchUpRunner
{
    private const int PageSize = 200;

    private readonly ILogger _logger;

    public CatchUpRunner(ILogger logger) => _logger = logger;

    public async Task<CatchUpResult> RunAsync(
        IXrplNodeConnection connection,
        string account,
        uint fromLedger,
        uint toLedger,
        Func<IAccountTransaction, CancellationToken, Task> sink,
        CancellationToken cancellationToken)
    {
        if (fromLedger > toLedger)
        {
            return CatchUpResult.Complete(0);
        }

        NodeStatus status = await connection.GetNodeStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!LedgerRangeSet.TryParse(status.CompleteLedgers, out LedgerRangeSet history)
            || !history.Covers(fromLedger, toLedger))
        {
            return CatchUpResult.Incomplete(
                $"node {connection.Node} does not hold ledgers {fromLedger}-{toLedger} (complete_ledgers: {status.CompleteLedgers ?? "none"})");
        }

        object? marker = null;
        int processed = 0;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            AccountTransactionPage page = await connection.GetAccountTransactionsAsync(
                new AccountTransactionQuery
                {
                    Account = account,
                    LedgerIndexMin = fromLedger,
                    LedgerIndexMax = toLedger,
                    Limit = PageSize,
                    Marker = marker,
                },
                cancellationToken).ConfigureAwait(false);

            if (page.LedgerIndexMin > fromLedger || page.LedgerIndexMax < toLedger)
            {
                return CatchUpResult.Incomplete(
                    $"node {connection.Node} searched ledgers {page.LedgerIndexMin}-{page.LedgerIndexMax}, narrower than the requested {fromLedger}-{toLedger}");
            }

            foreach (IAccountTransaction transaction in page.Transactions)
            {
                await sink(transaction, cancellationToken).ConfigureAwait(false);
                processed++;
            }

            marker = page.Marker;
        }
        while (marker is not null);

        _logger.LogInformation(
            "catch-up over ledgers {From}-{To} on {Node} replayed {Count} transactions",
            fromLedger,
            toLedger,
            connection.Node,
            processed);

        return CatchUpResult.Complete(processed);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.CatchUpRunnerTests"
```

Expected: 6 tests pass.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: verify node history before trusting a catch-up"
```

---

### Task 8: Options and monitor state

**Files:**
- Create: `src/Xrpl.PaymentGateway/PaymentGatewayOptions.cs`
- Create: `src/Xrpl.PaymentGateway/PaymentGatewayOptionsValidator.cs`
- Create: `src/Xrpl.PaymentGateway/Internal/MonitorEvent.cs`
- Create: `src/Xrpl.PaymentGateway/Internal/MonitorSnapshot.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/PaymentGatewayOptionsValidatorTests.cs`

- [ ] **Step 1: Write the failing validator tests**

`tests/Xrpl.PaymentGateway.Tests/PaymentGatewayOptionsValidatorTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class PaymentGatewayOptionsValidatorTests
{
    private static PaymentGatewayOptions Valid() => new PaymentGatewayOptions
    {
        Address = "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p",
        Nodes = new[] { new Uri("ws://localhost:6006") },
    };

    private static ValidateOptionsResult Validate(PaymentGatewayOptions options) =>
        new PaymentGatewayOptionsValidator().Validate(Options.DefaultName, options);

    [Fact]
    public void AFullyConfiguredOptionsObjectPasses()
    {
        Assert.True(Validate(Valid()).Succeeded);
    }

    [Fact]
    public void AMissingAddressFails()
    {
        PaymentGatewayOptions options = Valid();
        options.Address = string.Empty;

        Assert.Contains("Address", Validate(options).FailureMessage);
    }

    [Fact]
    public void AnEmptyNodePoolFails()
    {
        PaymentGatewayOptions options = Valid();
        options.Nodes = Array.Empty<Uri>();

        Assert.Contains("Nodes", Validate(options).FailureMessage);
    }

    [Fact]
    public void ANonWebSocketNodeFails()
    {
        PaymentGatewayOptions options = Valid();
        options.Nodes = new[] { new Uri("https://localhost:5005") };

        Assert.Contains("ws", Validate(options).FailureMessage);
    }

    [Fact]
    public void DestinationTagZeroFails()
    {
        PaymentGatewayOptions options = Valid();
        options.FirstDestinationTag = 0;

        Assert.Contains("FirstDestinationTag", Validate(options).FailureMessage);
    }

    [Fact]
    public void ANonPositiveStallTimeoutFails()
    {
        PaymentGatewayOptions options = Valid();
        options.LedgerStallTimeout = TimeSpan.Zero;

        Assert.Contains("LedgerStallTimeout", Validate(options).FailureMessage);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.PaymentGatewayOptionsValidatorTests"
```

Expected: compile error, `PaymentGatewayOptions` does not exist.

- [ ] **Step 3: Write `PaymentGatewayOptions`**

`src/Xrpl.PaymentGateway/PaymentGatewayOptions.cs`:

```csharp
namespace Xrpl.PaymentGateway;

/// <summary>Everything the gateway needs to know. One instance means one receiving account.</summary>
public sealed class PaymentGatewayOptions
{
    /// <summary>The receiving r-address. Payments to it are recorded; its own transactions are ignored.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Allowed WebSocket nodes, tried round-robin on every reconnect.</summary>
    public IReadOnlyList<Uri> Nodes { get; set; } = Array.Empty<Uri>();

    /// <summary>
    /// Optional full-history nodes used only for catch-up and reconciliation, so ordinary streaming can
    /// run against light nodes. Falls back to <see cref="Nodes"/> when unset.
    /// </summary>
    public IReadOnlyList<Uri>? CatchUpNodes { get; set; }

    /// <summary>Where to begin when the store has no cursor. Unset means start at the current validated ledger.</summary>
    public uint? StartLedgerIndex { get; set; }

    /// <summary>The tag handed to the first buyer. Zero is not issued: many wallets treat it as "no tag".</summary>
    public uint FirstDestinationTag { get; set; } = 1;

    /// <summary>How long without a ledger close before the node is suspected. Normal close time is 3-5 seconds.</summary>
    public TimeSpan LedgerStallTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How often to re-check a network-wide stall before doing anything about it.</summary>
    public TimeSpan NetworkStallProbeInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>First reconnect delay. Doubles per attempt, jittered, capped by <see cref="ReconnectMaxDelay"/>.</summary>
    public TimeSpan ReconnectBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling on the reconnect delay.</summary>
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>First store retry delay.</summary>
    public TimeSpan StoreRetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling on the store retry delay. Retries never give up.</summary>
    public TimeSpan StoreRetryMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// In-memory buffer between the socket and processing. Overflowing it drops the connection on purpose:
    /// catch-up recollects everything from the ledger, so the buffer is never the system of record.
    /// </summary>
    public int StreamBufferCapacity { get; set; } = 1000;

    /// <summary>How many ledgers below the cursor reconciliation re-verifies. 2000 is roughly two hours.</summary>
    public uint ReconcileWindow { get; set; } = 2000;

    /// <summary>Cap on the unhandled-payment count a health check reads.</summary>
    public int HealthUnhandledSampleSize { get; set; } = 100;

    /// <summary>Ledger lag above which the health report stops calling itself healthy.</summary>
    public uint MaxAcceptableLedgerLag { get; set; } = 10;

    /// <summary>Nodes used for catch-up: <see cref="CatchUpNodes"/> when set, otherwise <see cref="Nodes"/>.</summary>
    public IReadOnlyList<Uri> EffectiveCatchUpNodes =>
        CatchUpNodes is { Count: > 0 } dedicated ? dedicated : Nodes;
}
```

- [ ] **Step 4: Write `PaymentGatewayOptionsValidator`**

`src/Xrpl.PaymentGateway/PaymentGatewayOptionsValidator.cs`:

```csharp
using Microsoft.Extensions.Options;

namespace Xrpl.PaymentGateway;

/// <summary>Fails fast on a misconfigured gateway rather than at the first payment.</summary>
public sealed class PaymentGatewayOptionsValidator : IValidateOptions<PaymentGatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, PaymentGatewayOptions options)
    {
        List<string> failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Address))
        {
            failures.Add($"{nameof(options.Address)} must be the receiving r-address.");
        }

        if (options.Nodes is null || options.Nodes.Count == 0)
        {
            failures.Add($"{nameof(options.Nodes)} must contain at least one node.");
        }
        else
        {
            foreach (Uri node in options.Nodes.Concat(options.CatchUpNodes ?? Array.Empty<Uri>()))
            {
                if (node.Scheme is not ("ws" or "wss"))
                {
                    failures.Add($"node {node} must use the ws or wss scheme.");
                }
            }
        }

        if (options.FirstDestinationTag == 0)
        {
            failures.Add($"{nameof(options.FirstDestinationTag)} must be greater than zero; tag 0 reads as \"no tag\" in many wallets.");
        }

        if (options.LedgerStallTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.LedgerStallTimeout)} must be positive.");
        }

        if (options.StreamBufferCapacity <= 0)
        {
            failures.Add($"{nameof(options.StreamBufferCapacity)} must be positive.");
        }

        if (options.HealthUnhandledSampleSize <= 0)
        {
            failures.Add($"{nameof(options.HealthUnhandledSampleSize)} must be positive.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
```

- [ ] **Step 5: Write `MonitorEvent` and `MonitorSnapshot`**

`src/Xrpl.PaymentGateway/Internal/MonitorEvent.cs`:

```csharp
using Xrpl.Models.Methods;

namespace Xrpl.PaymentGateway.Internal;

internal enum MonitorEventKind
{
    Transaction,
    Ledger,
}

/// <summary>
/// One item from the socket. Transactions and ledger closes share a queue so that the cursor can only
/// advance past a ledger after everything that arrived before its close has been processed.
/// </summary>
internal readonly struct MonitorEvent
{
    private MonitorEvent(MonitorEventKind kind, IAccountTransaction? transaction, ulong ledgerIndex)
    {
        Kind = kind;
        Transaction = transaction;
        LedgerIndex = ledgerIndex;
    }

    public MonitorEventKind Kind { get; }

    public IAccountTransaction? Transaction { get; }

    public ulong LedgerIndex { get; }

    public static MonitorEvent ForTransaction(IAccountTransaction transaction) =>
        new MonitorEvent(MonitorEventKind.Transaction, transaction, 0);

    public static MonitorEvent ForLedger(ulong ledgerIndex) =>
        new MonitorEvent(MonitorEventKind.Ledger, null, ledgerIndex);
}

/// <summary>Thrown into the event channel when the node session ends, unwinding the session loop.</summary>
internal sealed class SessionEndedException : Exception
{
    public SessionEndedException(string reason) : base(reason)
    {
    }
}

/// <summary>Thrown into the event channel when the buffer overflows and the session must be dropped.</summary>
internal sealed class StreamBufferOverflowException : Exception
{
    public StreamBufferOverflowException(int capacity)
        : base($"the stream buffer of {capacity} events overflowed; dropping the session so catch-up can recollect from the ledger")
    {
    }
}
```

`src/Xrpl.PaymentGateway/Internal/MonitorSnapshot.cs`:

```csharp
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>What the monitor knows about itself, readable by the health service from another thread.</summary>
internal sealed class MonitorSnapshot
{
    private readonly object _gate = new object();
    private PaymentMonitorState _state = PaymentMonitorState.Stopped;
    private string? _node;
    private string? _lastError;
    private uint? _lastValidatedLedger;
    private uint? _cursor;
    private DateTimeOffset? _lastLedgerAt;
    private long _anomalyCount;

    public void SetState(PaymentMonitorState state)
    {
        lock (_gate)
        {
            _state = state;
        }
    }

    public void SetNode(Uri? node)
    {
        lock (_gate)
        {
            _node = node?.ToString();
        }
    }

    public void SetCursor(uint cursor)
    {
        lock (_gate)
        {
            _cursor = cursor;
        }
    }

    public void SetValidatedLedger(uint ledgerIndex, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_lastValidatedLedger is null || ledgerIndex > _lastValidatedLedger)
            {
                _lastValidatedLedger = ledgerIndex;
            }

            _lastLedgerAt = at;
        }
    }

    public void SetError(string error)
    {
        lock (_gate)
        {
            _lastError = error;
        }
    }

    public void IncrementAnomaly() => Interlocked.Increment(ref _anomalyCount);

    public MonitorSnapshotData Read()
    {
        lock (_gate)
        {
            return new MonitorSnapshotData
            {
                State = _state,
                Node = _node,
                LastError = _lastError,
                LastValidatedLedger = _lastValidatedLedger,
                Cursor = _cursor,
                LastLedgerAt = _lastLedgerAt,
                AnomalyCount = Interlocked.Read(ref _anomalyCount),
            };
        }
    }
}

/// <summary>An immutable copy of <see cref="MonitorSnapshot"/>.</summary>
internal sealed class MonitorSnapshotData
{
    public required PaymentMonitorState State { get; init; }

    public string? Node { get; init; }

    public string? LastError { get; init; }

    public uint? LastValidatedLedger { get; init; }

    public uint? Cursor { get; init; }

    public DateTimeOffset? LastLedgerAt { get; init; }

    public required long AnomalyCount { get; init; }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.PaymentGatewayOptionsValidatorTests"
```

Expected: 6 tests pass.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat: add gateway options, validation and monitor state"
```

---

### Task 9: XrplPaymentMonitor

The orchestrator. Subscribe first, then catch up, then stream — so no window exists between the two paths, and the overlap is absorbed by idempotent writes.

**Files:**
- Create: `src/Xrpl.PaymentGateway/XrplPaymentMonitor.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/TestWait.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/XrplPaymentMonitorTests.cs`

- [ ] **Step 1: Write the polling helper**

`tests/Xrpl.PaymentGateway.Tests/TestWait.cs`:

```csharp
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

/// <summary>Waits for a background service to reach a state, instead of sleeping and hoping.</summary>
public static class TestWait
{
    public static async Task UntilAsync(Func<bool> condition, string description, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"timed out after {timeoutMs} ms waiting for: {description}");
    }
}
```

- [ ] **Step 2: Write the failing monitor tests**

`tests/Xrpl.PaymentGateway.Tests/XrplPaymentMonitorTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xrpl.Models.Methods;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xrpl.PaymentGateway.Tests.Fixtures;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class XrplPaymentMonitorTests
{
    private static readonly Uri NodeA = new Uri("ws://a:6006");
    private static readonly Uri NodeB = new Uri("ws://b:6006");

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(Action<PaymentGatewayOptions>? configure = null, uint firstDestinationTag = 1)
        {
            Store = new InMemoryPaymentStore(firstDestinationTag);
            Options = new PaymentGatewayOptions
            {
                Address = TransactionFixtures.Receiver,
                Nodes = new[] { NodeA, NodeB },
                LedgerStallTimeout = TimeSpan.FromMinutes(5),
                ReconnectBaseDelay = TimeSpan.FromMilliseconds(5),
                ReconnectMaxDelay = TimeSpan.FromMilliseconds(20),
                StoreRetryBaseDelay = TimeSpan.FromMilliseconds(5),
                StoreRetryMaxDelay = TimeSpan.FromMilliseconds(20),
            };
            configure?.Invoke(Options);

            Snapshot = new MonitorSnapshot();
            Monitor = new XrplPaymentMonitor(
                Microsoft.Extensions.Options.Options.Create(Options),
                Factory,
                Store,
                Handler,
                Snapshot,
                TimeProvider.System,
                NullLogger<XrplPaymentMonitor>.Instance);
        }

        public PaymentGatewayOptions Options { get; }

        public FakeXrplNodeConnectionFactory Factory { get; } = new FakeXrplNodeConnectionFactory();

        public InMemoryPaymentStore Store { get; }

        public RecordingHandler Handler { get; } = new RecordingHandler();

        public MonitorSnapshot Snapshot { get; }

        public XrplPaymentMonitor Monitor { get; }

        public Task StartAsync() => Monitor.StartAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Monitor.StopAsync(CancellationToken.None);
            Monitor.Dispose();
        }
    }

    [Fact]
    public async Task AFreshStoreStartsAtTheCurrentValidatedLedgerAndDoesNotReplayHistory()
    {
        await using Harness harness = new Harness();
        FakeXrplNodeConnection node = harness.Factory.For(NodeA);
        node.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 900, CompleteLedgers = "1-900" };

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");

        Assert.Equal(TransactionFixtures.Receiver, node.SubscribedAccount);
        Assert.Empty(node.Queries);
        Assert.Equal(900u, harness.Snapshot.Read().Cursor);

        // The starting point must reach the store, not just the in-memory snapshot: a restart before the
        // first ledger close would otherwise pick "current validated" again and skip the interval.
        Assert.Equal(900u, await harness.Store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStoredCursorBehindTheNetworkTriggersACatchUpAndThenAdvances()
    {
        await using Harness harness = new Harness();
        await harness.Store.SetLastProcessedLedgerAsync(800, TestContext.Current.CancellationToken);
        FakeXrplNodeConnection node = harness.Factory.For(NodeA);
        node.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 900, CompleteLedgers = "1-900" };
        node.EnqueuePage(new AccountTransactionPage
        {
            Transactions = new[] { TransactionFixtures.Parse(TransactionFixtures.XrpPayment) },
            Marker = null,
            LedgerIndexMin = 801,
            LedgerIndexMax = 900,
        });

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to finish catching up");

        AccountTransactionQuery query = Assert.Single(node.Queries);
        Assert.Equal(801u, query.LedgerIndexMin);
        Assert.Equal(900u, query.LedgerIndexMax);
        Assert.Equal(900u, await harness.Store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));
        Assert.Single(harness.Store.Snapshot());
    }

    [Fact]
    public async Task ACatchUpTheNodesCannotProveLeavesTheCursorAloneAndReportsAHistoryGap()
    {
        await using Harness harness = new Harness();
        await harness.Store.SetLastProcessedLedgerAsync(100, TestContext.Current.CancellationToken);
        foreach (Uri node in new[] { NodeA, NodeB })
        {
            harness.Factory.For(node).Status = new NodeStatus
            {
                ServerState = "full",
                ValidatedLedgerIndex = 900,
                CompleteLedgers = "800-900",
            };
        }

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.HistoryGap, "the monitor to report a history gap");

        Assert.Equal(100u, await harness.Store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFrozenCursorStaysFrozenEvenAsLedgersKeepClosing()
    {
        await using Harness harness = new Harness();
        await harness.Store.SetLastProcessedLedgerAsync(100, TestContext.Current.CancellationToken);
        foreach (Uri node in new[] { NodeA, NodeB })
        {
            harness.Factory.For(node).Status = new NodeStatus
            {
                ServerState = "full",
                ValidatedLedgerIndex = 900,
                CompleteLedgers = "800-900",
            };
        }

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.HistoryGap, "the monitor to report a history gap");

        // The live stream keeps running. If the cursor followed it, ledgers 101-900 would be written off as
        // searched and the gap would become permanent and invisible.
        await harness.Factory.For(NodeA).PushLedgerAsync(901);
        await harness.Factory.For(NodeA).PushLedgerAsync(902);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(100u, await harness.Store.GetLastProcessedLedgerAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PaymentMonitorState.HistoryGap, harness.Snapshot.Read().State);
    }

    [Fact]
    public async Task ALiveTransactionIsRecordedAndDeliveredToTheBuyerBehindTheTag()
    {
        // The fixture carries DestinationTag 42, so the store must hand tag 42 to the buyer for the two to meet.
        await using Harness harness = new Harness(firstDestinationTag: 42);
        FakeXrplNodeConnection node = harness.Factory.For(NodeA);
        node.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 90, CompleteLedgers = "1-90" };
        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");
        uint tag = await harness.Store.GetOrAssignTagAsync("buyer-42", TestContext.Current.CancellationToken);
        Assert.Equal(42u, tag);

        await node.PushTransactionAsync(TransactionFixtures.Parse(TransactionFixtures.XrpPayment));

        await TestWait.UntilAsync(() => harness.Handler.Deliveries.Count == 1, "the payment to reach the handler");
        Assert.Equal(1m, harness.Handler.Deliveries[0].Payment.Value);
        Assert.Equal("buyer-42", harness.Handler.Deliveries[0].BuyerId);
        Assert.Empty(await harness.Store.GetUnhandledPaymentsAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheSameTransactionArrivingTwiceIsDeliveredOnce()
    {
        await using Harness harness = new Harness();
        FakeXrplNodeConnection node = harness.Factory.For(NodeA);
        node.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 90, CompleteLedgers = "1-90" };
        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");

        await node.PushTransactionAsync(TransactionFixtures.Parse(TransactionFixtures.XrpPayment));
        await node.PushTransactionAsync(TransactionFixtures.Parse(TransactionFixtures.XrpPayment));

        await TestWait.UntilAsync(() => harness.Handler.Deliveries.Count >= 1, "the payment to reach the handler");
        await Task.Delay(100);
        Assert.Single(harness.Handler.Deliveries);
    }

    [Fact]
    public async Task AClosedLedgerAdvancesTheCursorToTheLedgerBeforeIt()
    {
        await using Harness harness = new Harness();
        FakeXrplNodeConnection node = harness.Factory.For(NodeA);
        node.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 90, CompleteLedgers = "1-90" };
        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");

        await node.PushLedgerAsync(120);

        await TestWait.UntilAsync(
            () => harness.Store.GetLastProcessedLedgerAsync(CancellationToken.None).GetAwaiter().GetResult() == 119u,
            "the cursor to reach 119");
    }

    [Fact]
    public async Task ADroppedSessionReconnectsToTheNextNodeInThePool()
    {
        await using Harness harness = new Harness();
        foreach (Uri node in new[] { NodeA, NodeB })
        {
            harness.Factory.For(node).Status = new NodeStatus
            {
                ServerState = "full",
                ValidatedLedgerIndex = 90,
                CompleteLedgers = "1-90",
            };
        }

        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the first session to start");

        await harness.Factory.For(NodeA).EndSessionAsync("socket closed");

        await TestWait.UntilAsync(
            () => harness.Factory.For(NodeB).SubscribedAccount is not null, "the monitor to move to the second node");
    }

    [Fact]
    public async Task AStalledOutOfSyncNodeIsAbandonedForTheNextOne()
    {
        await using Harness harness = new Harness(options => options.LedgerStallTimeout = TimeSpan.FromMilliseconds(100));
        harness.Factory.For(NodeA).Status = new NodeStatus
        {
            ServerState = "syncing",
            ValidatedLedgerIndex = 90,
            CompleteLedgers = "1-90",
        };
        harness.Factory.For(NodeB).Status = new NodeStatus
        {
            ServerState = "full",
            ValidatedLedgerIndex = 90,
            CompleteLedgers = "1-90",
        };

        await harness.StartAsync();

        await TestWait.UntilAsync(
            () => harness.Factory.For(NodeB).SubscribedAccount is not null, "the monitor to rotate off the stalled node");
    }

    [Fact]
    public async Task WhenEverySyncedNodeStopsAdvancingTheStallIsBlamedOnTheNetwork()
    {
        await using Harness harness = new Harness(options => options.LedgerStallTimeout = TimeSpan.FromMilliseconds(100));
        foreach (Uri node in new[] { NodeA, NodeB })
        {
            harness.Factory.For(node).Status = new NodeStatus
            {
                ServerState = "full",
                ValidatedLedgerIndex = 90,
                CompleteLedgers = "1-90",
            };
        }

        await harness.StartAsync();

        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.NetworkStalled, "the monitor to blame the network");
        Assert.NotNull(harness.Factory.For(NodeA).SubscribedAccount);
    }

    [Fact]
    public async Task AnAnomalousTransactionIsCountedAndNotDelivered()
    {
        await using Harness harness = new Harness();
        FakeXrplNodeConnection node = harness.Factory.For(NodeA);
        node.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 90, CompleteLedgers = "1-90" };
        await harness.StartAsync();
        await TestWait.UntilAsync(
            () => harness.Snapshot.Read().State == PaymentMonitorState.Streaming, "the monitor to start streaming");

        await node.PushTransactionAsync(TransactionFixtures.Parse(TransactionFixtures.ExchangeWithDebit));

        await TestWait.UntilAsync(() => harness.Snapshot.Read().AnomalyCount == 1, "the anomaly to be counted");
        Assert.Empty(harness.Handler.Deliveries);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.XrplPaymentMonitorTests"
```

Expected: compile error, `XrplPaymentMonitor` does not exist.

- [ ] **Step 4: Write `XrplPaymentMonitor`**

`src/Xrpl.PaymentGateway/XrplPaymentMonitor.cs`:

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xrpl.Models.Methods;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;

// Both Xrpl.Models.Methods and System.Threading.Channels define a type named Channel, so the bare name
// is ambiguous (CS0104) on the static CreateBounded call. The generic Channel<T> resolves by arity.
using Channel = System.Threading.Channels.Channel;

namespace Xrpl.PaymentGateway;

/// <summary>
/// Follows the receiving account and records what arrives. One session at a time, one node at a time;
/// every session starts by subscribing, then replaying whatever happened while it was away.
/// </summary>
/// <remarks>
/// Internal because its constructor takes internal types (<c>MonitorSnapshot</c>,
/// <c>IXrplNodeConnectionFactory</c>) and a public constructor may not expose less accessible types
/// (CS0051). Hosts reach it through <see cref="IHostedService"/>; tests see it via InternalsVisibleTo.
/// </remarks>
internal sealed class XrplPaymentMonitor : BackgroundService
{
    private readonly PaymentGatewayOptions _options;
    private readonly IXrplNodeConnectionFactory _connectionFactory;
    private readonly IPaymentStore _store;
    private readonly MonitorSnapshot _snapshot;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<XrplPaymentMonitor> _logger;
    private readonly NodePool _pool;
    private readonly TransactionProcessor _processor;
    private readonly PaymentDispatcher _dispatcher;
    private readonly CatchUpRunner _catchUp;
    private readonly StoreRetryPolicy _storeRetry;

    private uint _persistedCursor;

    /// <summary>
    /// Set when a catch-up could not be proven complete. While it holds a value, the cursor is frozen:
    /// advancing it past an unverified range would turn a visible gap into a permanent, invisible one.
    /// </summary>
    private uint? _unprovenFromLedger;

    /// <summary>The state to restore once the store comes back.</summary>
    private PaymentMonitorState _stateBeforeStoreOutage = PaymentMonitorState.Connecting;

    public XrplPaymentMonitor(
        IOptions<PaymentGatewayOptions> options,
        IXrplNodeConnectionFactory connectionFactory,
        IPaymentStore store,
        IPaymentReceivedHandler handler,
        MonitorSnapshot snapshot,
        TimeProvider timeProvider,
        ILogger<XrplPaymentMonitor> logger)
    {
        _options = options.Value;
        _connectionFactory = connectionFactory;
        _store = store;
        _snapshot = snapshot;
        _timeProvider = timeProvider;
        _logger = logger;
        _pool = new NodePool(_options.Nodes);
        _processor = new TransactionProcessor(_options.Address, timeProvider, logger);
        _dispatcher = new PaymentDispatcher(store, handler, logger);
        _catchUp = new CatchUpRunner(logger);
        _storeRetry = new StoreRetryPolicy(
            _options.StoreRetryBaseDelay,
            _options.StoreRetryMaxDelay,
            timeProvider,
            logger,
            available =>
            {
                if (!available)
                {
                    _stateBeforeStoreOutage = _snapshot.Read().State;
                    _snapshot.SetState(PaymentMonitorState.StoreUnavailable);
                }
                else if (_snapshot.Read().State == PaymentMonitorState.StoreUnavailable)
                {
                    // Without this the health report keeps claiming the store is down long after it came back.
                    _snapshot.SetState(_stateBeforeStoreOutage);
                }
            });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(stoppingToken).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (SessionEndedException ex)
            {
                _logger.LogWarning("node session ended: {Reason}", ex.Message);
                attempt = 0;
            }
            catch (Exception ex)
            {
                attempt++;
                _snapshot.SetError(ex.Message);
                _logger.LogError(ex, "payment monitor session failed");
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            _snapshot.SetState(PaymentMonitorState.Reconnecting);

            try
            {
                await Task.Delay(ReconnectDelay(attempt), _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _snapshot.SetState(PaymentMonitorState.Stopped);
        _snapshot.SetNode(null);
    }

    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        Uri node = _pool.Next();
        _snapshot.SetNode(node);
        _snapshot.SetState(PaymentMonitorState.Connecting);

        using CancellationTokenSource sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        CancellationToken sessionToken = sessionCts.Token;

        Channel<MonitorEvent> channel = Channel.CreateBounded<MonitorEvent>(
            new BoundedChannelOptions(_options.StreamBufferCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

        await using IXrplNodeConnection connection = _connectionFactory.Create(node);

        connection.OnTransaction = transaction =>
        {
            Publish(channel, MonitorEvent.ForTransaction(transaction));
            return Task.CompletedTask;
        };
        connection.OnLedgerClosed = ledgerIndex =>
        {
            Publish(channel, MonitorEvent.ForLedger(ledgerIndex));
            return Task.CompletedTask;
        };
        connection.OnSessionEnded = reason =>
        {
            channel.Writer.TryComplete(new SessionEndedException(reason));
            return Task.CompletedTask;
        };

        try
        {
            await connection.ConnectAsync(sessionToken).ConfigureAwait(false);
            await connection.SubscribeToAccountAsync(_options.Address, sessionToken).ConfigureAwait(false);

            uint validated = await PrimeCursorAsync(connection, sessionToken).ConfigureAwait(false);
            await StreamAsync(connection, channel.Reader, validated, sessionToken).ConfigureAwait(false);
        }
        finally
        {
            connection.OnTransaction = null;
            connection.OnLedgerClosed = null;
            connection.OnSessionEnded = null;
            channel.Writer.TryComplete();
            sessionCts.Cancel();
        }
    }

    private void Publish(Channel<MonitorEvent> channel, MonitorEvent monitorEvent)
    {
        if (!channel.Writer.TryWrite(monitorEvent))
        {
            channel.Writer.TryComplete(new StreamBufferOverflowException(_options.StreamBufferCapacity));
        }
    }

    /// <summary>Loads the cursor, replays anything missed, and returns the node's validated ledger.</summary>
    private async Task<uint> PrimeCursorAsync(IXrplNodeConnection connection, CancellationToken cancellationToken)
    {
        uint? storedCursor = await _storeRetry.ExecuteAsync(
            token => _store.GetLastProcessedLedgerAsync(token), "GetLastProcessedLedger", cancellationToken).ConfigureAwait(false);

        NodeStatus status = await connection.GetNodeStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.ValidatedLedgerIndex is not { } validated)
        {
            throw new InvalidOperationException($"node {connection.Node} reports no validated ledger");
        }

        _snapshot.SetValidatedLedger(validated, _timeProvider.GetUtcNow());

        uint cursor = storedCursor ?? _options.StartLedgerIndex ?? validated;
        _persistedCursor = cursor;
        _snapshot.SetCursor(cursor);

        if (storedCursor is null)
        {
            // Written directly rather than through PersistCursorAsync, whose "already at or past this"
            // guard would swallow it. Without this write, a restart before the first ledger close would
            // start from the current validated ledger again and skip whatever arrived in between.
            await _storeRetry.ExecuteAsync(
                token => _store.SetLastProcessedLedgerAsync(cursor, token),
                "SetLastProcessedLedger",
                cancellationToken).ConfigureAwait(false);
        }

        if (cursor >= validated)
        {
            _unprovenFromLedger = null;
            _snapshot.SetState(PaymentMonitorState.Streaming);
            return validated;
        }

        _snapshot.SetState(PaymentMonitorState.CatchingUp);
        CatchUpResult result = await CatchUpAsync(connection, cursor + 1, validated, cancellationToken).ConfigureAwait(false);

        if (result.Completed)
        {
            _unprovenFromLedger = null;
            await PersistCursorAsync(validated, cancellationToken).ConfigureAwait(false);
            _snapshot.SetState(PaymentMonitorState.Streaming);
        }
        else
        {
            // The cursor must stay put. Letting it follow the live stream would move the proven-completeness
            // boundary past ledgers nobody ever searched, and the next restart would never look at them
            // again — a visible gap silently becoming a permanent one.
            _unprovenFromLedger = cursor + 1;
            _logger.LogError(
                "catch-up over ledgers {From}-{To} could not be proven complete on any node: {Reason}. The cursor stays frozen at {Cursor} and new payments are still recorded; add a full-history node to close the gap.",
                cursor + 1,
                validated,
                result.Reason,
                cursor);
            _snapshot.SetState(PaymentMonitorState.HistoryGap);
        }

        return validated;
    }

    /// <summary>Tries the live node first, then any dedicated catch-up nodes, until one proves the range.</summary>
    private async Task<CatchUpResult> CatchUpAsync(
        IXrplNodeConnection primary,
        uint fromLedger,
        uint toLedger,
        CancellationToken cancellationToken)
    {
        CatchUpResult result = await _catchUp
            .RunAsync(primary, _options.Address, fromLedger, toLedger, ProcessTransactionAsync, cancellationToken)
            .ConfigureAwait(false);

        if (result.Completed)
        {
            return result;
        }

        _logger.LogWarning("catch-up on {Node} was not usable: {Reason}", primary.Node, result.Reason);

        foreach (Uri candidate in _options.EffectiveCatchUpNodes)
        {
            if (candidate == primary.Node)
            {
                continue;
            }

            try
            {
                await using IXrplNodeConnection fallback = _connectionFactory.Create(candidate);
                await fallback.ConnectAsync(cancellationToken).ConfigureAwait(false);

                CatchUpResult attempt = await _catchUp
                    .RunAsync(fallback, _options.Address, fromLedger, toLedger, ProcessTransactionAsync, cancellationToken)
                    .ConfigureAwait(false);

                if (attempt.Completed)
                {
                    return attempt;
                }

                _logger.LogWarning("catch-up on {Node} was not usable: {Reason}", candidate, attempt.Reason);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "catch-up attempt against {Node} failed", candidate);
            }
        }

        return result;
    }

    private async Task StreamAsync(
        IXrplNodeConnection connection,
        ChannelReader<MonitorEvent> reader,
        uint validatedAtStart,
        CancellationToken cancellationToken)
    {
        DateTimeOffset lastProgress = _timeProvider.GetUtcNow();
        uint lastSeenValidated = validatedAtStart;

        while (!cancellationToken.IsCancellationRequested)
        {
            Task<bool> wait = reader.WaitToReadAsync(cancellationToken).AsTask();
            TimeSpan idleBudget = _snapshot.Read().State == PaymentMonitorState.NetworkStalled
                ? _options.NetworkStallProbeInterval
                : _options.LedgerStallTimeout;

            Task completed = await Task.WhenAny(
                wait,
                Task.Delay(idleBudget, _timeProvider, cancellationToken)).ConfigureAwait(false);

            if (completed != wait)
            {
                if (_timeProvider.GetUtcNow() - lastProgress < idleBudget)
                {
                    continue;
                }

                bool networkStalled = await IsNetworkStalledAsync(connection, lastSeenValidated, cancellationToken).ConfigureAwait(false);
                if (!networkStalled)
                {
                    _logger.LogWarning(
                        "node {Node} produced no ledger for {Timeout}; rotating to the next node",
                        connection.Node,
                        idleBudget);
                    return;
                }

                _logger.LogWarning(
                    "no ledger for {Timeout} and every reachable node is synced at ledger {Ledger}; treating this as a network-wide stall",
                    idleBudget,
                    lastSeenValidated);
                _snapshot.SetState(PaymentMonitorState.NetworkStalled);
                lastProgress = _timeProvider.GetUtcNow();
                continue;
            }

            if (!await wait.ConfigureAwait(false))
            {
                return;
            }

            while (reader.TryRead(out MonitorEvent monitorEvent))
            {
                if (monitorEvent.Kind == MonitorEventKind.Transaction)
                {
                    await ProcessTransactionAsync(monitorEvent.Transaction!, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (monitorEvent.LedgerIndex is 0 or > uint.MaxValue)
                {
                    continue;
                }

                uint closed = (uint)monitorEvent.LedgerIndex;
                lastSeenValidated = closed;
                lastProgress = _timeProvider.GetUtcNow();
                _snapshot.SetValidatedLedger(closed, lastProgress);

                if (_unprovenFromLedger is not null)
                {
                    // An unproven range is still open behind us. Keep recording live payments, but neither
                    // advance the cursor nor let the state read as healthy.
                    continue;
                }

                _snapshot.SetState(PaymentMonitorState.Streaming);
                await PersistCursorAsync(closed - 1, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A stall is the network's fault only when the nodes themselves are healthy and stuck at the same place.
    /// Anything else — an unsynced node, an unreachable peer — is a local problem and means rotate.
    /// </summary>
    private async Task<bool> IsNetworkStalledAsync(
        IXrplNodeConnection connection,
        uint lastSeenValidated,
        CancellationToken cancellationToken)
    {
        NodeStatus current;

        try
        {
            current = await connection.GetNodeStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "server_info failed on {Node} while classifying a stall", connection.Node);
            return false;
        }

        if (!current.IsSynced || current.ValidatedLedgerIndex > lastSeenValidated)
        {
            return false;
        }

        // Every other node in the pool gets asked, not just the next one. With three or more nodes, two
        // stuck peers would otherwise outvote a third that is still advancing, and the monitor would sit
        // on a lagging node calling it a network outage.
        foreach (Uri candidate in _pool.Nodes)
        {
            if (candidate == connection.Node)
            {
                continue;
            }

            try
            {
                await using IXrplNodeConnection probe = _connectionFactory.Create(candidate);
                await probe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                NodeStatus other = await probe.GetNodeStatusAsync(cancellationToken).ConfigureAwait(false);

                if (other.IsSynced && other.ValidatedLedgerIndex > current.ValidatedLedgerIndex)
                {
                    _logger.LogInformation(
                        "node {Other} is ahead at ledger {Ahead} while {Node} sits at {Behind}; this is a node problem, not a network one",
                        candidate,
                        other.ValidatedLedgerIndex,
                        connection.Node,
                        current.ValidatedLedgerIndex);
                    return false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "probing {Node} for a second opinion failed", candidate);
            }
        }

        return true;
    }

    private async Task ProcessTransactionAsync(IAccountTransaction transaction, CancellationToken cancellationToken)
    {
        ProcessingResult result = _processor.Process(transaction);

        if (result.Kind == ProcessingResultKind.Anomaly)
        {
            _snapshot.IncrementAnomaly();
        }

        if (result.Record is not { } record)
        {
            return;
        }

        bool isNew = await _storeRetry
            .ExecuteAsync(token => _dispatcher.RecordAsync(record, token), "TryAddPayment", cancellationToken)
            .ConfigureAwait(false);

        if (isNew)
        {
            await _dispatcher.DeliverAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PersistCursorAsync(uint cursor, CancellationToken cancellationToken)
    {
        if (cursor <= _persistedCursor)
        {
            return;
        }

        await _storeRetry.ExecuteAsync(
            token => _store.SetLastProcessedLedgerAsync(cursor, token), "SetLastProcessedLedger", cancellationToken).ConfigureAwait(false);

        _persistedCursor = cursor;
        _snapshot.SetCursor(cursor);
    }

    private TimeSpan ReconnectDelay(int attempt)
    {
        if (attempt <= 0)
        {
            return _options.ReconnectBaseDelay;
        }

        double exponent = Math.Min(attempt - 1, 16);
        double milliseconds = Math.Min(
            _options.ReconnectBaseDelay.TotalMilliseconds * Math.Pow(2, exponent),
            _options.ReconnectMaxDelay.TotalMilliseconds);
        double jittered = milliseconds * (0.75 + (Random.Shared.NextDouble() * 0.5));

        return TimeSpan.FromMilliseconds(Math.Min(jittered, _options.ReconnectMaxDelay.TotalMilliseconds));
    }
}
```

Note on the fresh-store path: the cursor is persisted immediately so a restart before the first ledger close does not silently jump forward again.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.XrplPaymentMonitorTests"
```

Expected: 10 tests pass. If `ADroppedSessionReconnectsToTheNextNodeInThePool` is flaky, raise the `TestWait` timeout rather than adding sleeps to the monitor.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: follow the receiving account across nodes, stalls and restarts"
```

---

### Task 10: XrplPaymentGateway and PaymentMonitorHealth

Tag issuance, the health snapshot, and reconciliation. Note the class is `XrplPaymentGateway`, not `PaymentGateway`: a type whose name matches its own namespace makes every later reference ambiguous.

**Files:**
- Create: `src/Xrpl.PaymentGateway/XrplPaymentGateway.cs`
- Create: `src/Xrpl.PaymentGateway/PaymentMonitorHealth.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/XrplPaymentGatewayTests.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/PaymentMonitorHealthTests.cs`

- [ ] **Step 1: Write the failing gateway tests**

`tests/Xrpl.PaymentGateway.Tests/XrplPaymentGatewayTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class XrplPaymentGatewayTests
{
    private const string Address = "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p";

    private static XrplPaymentGateway Create(IPaymentStore store) =>
        new XrplPaymentGateway(
            store,
            Options.Create(new PaymentGatewayOptions
            {
                Address = Address,
                Nodes = new[] { new Uri("ws://localhost:6006") },
            }));

    [Fact]
    public async Task InstructionsCarryTheReceivingAddressAndAFreshTag()
    {
        XrplPaymentGateway gateway = Create(new InMemoryPaymentStore());

        PaymentInstructions instructions = await gateway.GetPaymentInstructionsAsync("buyer-1", TestContext.Current.CancellationToken);

        Assert.Equal(Address, instructions.Address);
        Assert.Equal(1u, instructions.DestinationTag);
    }

    [Fact]
    public async Task AReturningBuyerIsGivenTheTagTheyAlreadyHave()
    {
        XrplPaymentGateway gateway = Create(new InMemoryPaymentStore());
        PaymentInstructions first = await gateway.GetPaymentInstructionsAsync("buyer-1", TestContext.Current.CancellationToken);
        await gateway.GetPaymentInstructionsAsync("buyer-2", TestContext.Current.CancellationToken);

        PaymentInstructions again = await gateway.GetPaymentInstructionsAsync("buyer-1", TestContext.Current.CancellationToken);

        Assert.Equal(first.DestinationTag, again.DestinationTag);
    }

    [Fact]
    public async Task AnEmptyBuyerIdIsRejected()
    {
        XrplPaymentGateway gateway = Create(new InMemoryPaymentStore());

        await Assert.ThrowsAsync<ArgumentException>(
            () => gateway.GetPaymentInstructionsAsync("  ", TestContext.Current.CancellationToken));
    }
}
```

- [ ] **Step 2: Write the failing health tests**

`tests/Xrpl.PaymentGateway.Tests/PaymentMonitorHealthTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xrpl.PaymentGateway.Tests.Fixtures;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class PaymentMonitorHealthTests
{
    private static readonly Uri Node = new Uri("ws://node:6006");

    private sealed class Harness
    {
        public Harness(Action<PaymentGatewayOptions>? configure = null)
        {
            Options = new PaymentGatewayOptions
            {
                Address = TransactionFixtures.Receiver,
                Nodes = new[] { Node },
                ReconcileWindow = 100,
            };
            configure?.Invoke(Options);

            Health = new PaymentMonitorHealth(
                Microsoft.Extensions.Options.Options.Create(Options),
                Store,
                Handler,
                Snapshot,
                Factory,
                NullLogger<PaymentMonitorHealth>.Instance,
                TimeProvider.System);
        }

        public PaymentGatewayOptions Options { get; }

        public InMemoryPaymentStore Store { get; } = new InMemoryPaymentStore();

        public RecordingHandler Handler { get; } = new RecordingHandler();

        public MonitorSnapshot Snapshot { get; } = new MonitorSnapshot();

        public FakeXrplNodeConnectionFactory Factory { get; } = new FakeXrplNodeConnectionFactory();

        public PaymentMonitorHealth Health { get; }
    }

    private static PaymentRecord Record(string hash) => new PaymentRecord
    {
        TransactionHash = hash,
        TransactionType = "Payment",
        Sender = "rSender",
        DestinationTag = null,
        Currency = "XRP",
        Value = 1m,
        LedgerIndex = 10,
        ProcessedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task AStreamingMonitorWithNoLagReportsHealthy()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetState(PaymentMonitorState.Streaming);
        harness.Snapshot.SetCursor(999);
        harness.Snapshot.SetValidatedLedger(1000, DateTimeOffset.UnixEpoch);

        PaymentMonitorHealthReport report = await harness.Health.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(report.IsHealthy);
        Assert.Equal(1u, report.LedgerLag);
        Assert.Equal(PaymentMonitorState.Streaming, report.State);
    }

    [Fact]
    public async Task LagBeyondTheThresholdIsNotHealthy()
    {
        Harness harness = new Harness(options => options.MaxAcceptableLedgerLag = 5);
        harness.Snapshot.SetState(PaymentMonitorState.Streaming);
        harness.Snapshot.SetCursor(900);
        harness.Snapshot.SetValidatedLedger(1000, DateTimeOffset.UnixEpoch);

        PaymentMonitorHealthReport report = await harness.Health.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(report.IsHealthy);
        Assert.Equal(100u, report.LedgerLag);
    }

    [Fact]
    public async Task AReconnectingMonitorIsNotHealthy()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetState(PaymentMonitorState.Reconnecting);

        PaymentMonitorHealthReport report = await harness.Health.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(report.IsHealthy);
    }

    [Fact]
    public async Task UnhandledRecordsAreCountedAndReportedAsUnhealthy()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetState(PaymentMonitorState.Streaming);
        harness.Snapshot.SetCursor(1000);
        harness.Snapshot.SetValidatedLedger(1000, DateTimeOffset.UnixEpoch);
        await harness.Store.TryAddPaymentAsync(Record("A"), TestContext.Current.CancellationToken);

        PaymentMonitorHealthReport report = await harness.Health.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, report.UnhandledPaymentCount);
        Assert.False(report.IsHealthy);
    }

    [Fact]
    public async Task ReconciliationRedeliversWhatTheHandlerNeverGot()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetCursor(0);
        await harness.Store.TryAddPaymentAsync(Record("A"), TestContext.Current.CancellationToken);

        ReconciliationResult result = await harness.Health.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.RedeliveredCount);
        Assert.Single(harness.Handler.Deliveries);
        Assert.Empty(await harness.Store.GetUnhandledPaymentsAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReconciliationFindsAPaymentTheMonitorNeverStored()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetCursor(200);
        FakeXrplNodeConnection connection = harness.Factory.For(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 200, CompleteLedgers = "1-200" };
        connection.EnqueuePage(new AccountTransactionPage
        {
            Transactions = new[] { TransactionFixtures.Parse(TransactionFixtures.XrpPayment) },
            Marker = null,
            LedgerIndexMin = 100,
            LedgerIndexMax = 200,
        });

        ReconciliationResult result = await harness.Health.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.RecoveredCount);
        Assert.Single(harness.Store.Snapshot());
        Assert.Single(harness.Handler.Deliveries);
    }

    [Fact]
    public async Task ASweepThatFindsNothingMissingRecoversNothing()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetCursor(200);
        FakeXrplNodeConnection connection = harness.Factory.For(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 200, CompleteLedgers = "1-200" };

        ReconciliationResult result = await harness.Health.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, result.RecoveredCount);
        Assert.Empty(result.Errors);
        Assert.False(result.Skipped);
    }

    [Fact]
    public async Task ANodeThatCannotProveTheWindowIsReportedAsAnError()
    {
        Harness harness = new Harness();
        harness.Snapshot.SetCursor(200);
        FakeXrplNodeConnection connection = harness.Factory.For(Node);
        connection.Status = new NodeStatus { ServerState = "full", ValidatedLedgerIndex = 200, CompleteLedgers = "180-200" };

        ReconciliationResult result = await harness.Health.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Errors);
    }
}
```

- [ ] **Step 3: Run both test classes to verify they fail**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.XrplPaymentGatewayTests" -class "Xrpl.PaymentGateway.Tests.PaymentMonitorHealthTests"
```

Expected: compile error, `XrplPaymentGateway` and `PaymentMonitorHealth` do not exist.

- [ ] **Step 4: Write `XrplPaymentGateway`**

`src/Xrpl.PaymentGateway/XrplPaymentGateway.cs`:

```csharp
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway;

/// <summary>Hands buyers the address and tag to pay to. All the state lives in the host's store.</summary>
public sealed class XrplPaymentGateway : IPaymentGateway
{
    private readonly IPaymentStore _store;
    private readonly PaymentGatewayOptions _options;

    public XrplPaymentGateway(IPaymentStore store, IOptions<PaymentGatewayOptions> options)
    {
        _store = store;
        _options = options.Value;
    }

    public async Task<PaymentInstructions> GetPaymentInstructionsAsync(string buyerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);

        uint tag = await _store.GetOrAssignTagAsync(buyerId, cancellationToken).ConfigureAwait(false);

        return new PaymentInstructions
        {
            Address = _options.Address,
            DestinationTag = tag,
        };
    }
}
```

- [ ] **Step 5: Write `PaymentMonitorHealth`**

`src/Xrpl.PaymentGateway/PaymentMonitorHealth.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xrpl.Models.Methods;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;

namespace Xrpl.PaymentGateway;

/// <summary>
/// Liveness reporting and repair. Neither method drives the monitor: reconciliation is a safety net, and
/// the monitor stays correct whether or not anybody ever calls it.
/// </summary>
/// <remarks>
/// Internal for the same reason as the monitor: the constructor takes the internal
/// <c>MonitorSnapshot</c> and <c>IXrplNodeConnectionFactory</c>, and a public constructor may not expose
/// less accessible types (CS0051). Hosts resolve it as <see cref="IPaymentMonitorHealth"/>.
/// </remarks>
internal sealed class PaymentMonitorHealth : IPaymentMonitorHealth
{
    private readonly PaymentGatewayOptions _options;
    private readonly IPaymentStore _store;
    private readonly MonitorSnapshot _snapshot;
    private readonly IXrplNodeConnectionFactory _connectionFactory;
    private readonly ILogger<PaymentMonitorHealth> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly PaymentDispatcher _dispatcher;
    private readonly TransactionProcessor _processor;
    private readonly CatchUpRunner _catchUp;
    private readonly SemaphoreSlim _reconcileGate = new SemaphoreSlim(1, 1);

    public PaymentMonitorHealth(
        IOptions<PaymentGatewayOptions> options,
        IPaymentStore store,
        IPaymentReceivedHandler handler,
        MonitorSnapshot snapshot,
        IXrplNodeConnectionFactory connectionFactory,
        ILogger<PaymentMonitorHealth> logger,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _store = store;
        _snapshot = snapshot;
        _connectionFactory = connectionFactory;
        _logger = logger;
        _timeProvider = timeProvider;
        _dispatcher = new PaymentDispatcher(store, handler, logger);
        _processor = new TransactionProcessor(_options.Address, timeProvider, logger);
        _catchUp = new CatchUpRunner(logger);
    }

    public async Task<PaymentMonitorHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        MonitorSnapshotData data = _snapshot.Read();
        string? lastError = data.LastError;
        int unhandled = 0;

        try
        {
            IReadOnlyList<PaymentRecord> pending = await _store
                .GetUnhandledPaymentsAsync(_options.HealthUnhandledSampleSize, cancellationToken)
                .ConfigureAwait(false);
            unhandled = pending.Count;
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            _logger.LogError(ex, "reading unhandled payments for the health report failed");
        }

        uint lag = data.LastValidatedLedger is { } validated && data.Cursor is { } cursor && validated > cursor
            ? validated - cursor
            : 0;

        return new PaymentMonitorHealthReport
        {
            State = data.State,
            CurrentNode = data.Node,
            LastValidatedLedger = data.LastValidatedLedger,
            Cursor = data.Cursor,
            LedgerLag = lag,
            UnhandledPaymentCount = unhandled,
            AnomalyCount = data.AnomalyCount,
            LastError = lastError,
            LastLedgerAt = data.LastLedgerAt,
            IsHealthy = data.State == PaymentMonitorState.Streaming
                && lag <= _options.MaxAcceptableLedgerLag
                && unhandled == 0,
        };
    }

    public async Task<ReconciliationResult> ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!await _reconcileGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new ReconciliationResult
            {
                RedeliveredCount = 0,
                RecoveredCount = 0,
                Errors = Array.Empty<string>(),
                Skipped = true,
            };
        }

        try
        {
            List<string> errors = new List<string>();
            int redelivered = await RedeliverAsync(errors, cancellationToken).ConfigureAwait(false);
            int recovered = await SweepAsync(errors, cancellationToken).ConfigureAwait(false);

            return new ReconciliationResult
            {
                RedeliveredCount = redelivered,
                RecoveredCount = recovered,
                Errors = errors,
            };
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    /// <summary>
    /// One batch per run, deliberately. Looping until the queue drains would spin forever on a handler
    /// that keeps failing, and the next scheduled run picks up whatever is left.
    /// </summary>
    private async Task<int> RedeliverAsync(List<string> errors, CancellationToken cancellationToken)
    {
        IReadOnlyList<PaymentRecord> pending;

        try
        {
            pending = await _store
                .GetUnhandledPaymentsAsync(_options.HealthUnhandledSampleSize, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errors.Add($"reading unhandled payments failed: {ex.Message}");
            return 0;
        }

        int delivered = 0;
        foreach (PaymentRecord record in pending)
        {
            await _dispatcher.DeliverAsync(record, cancellationToken).ConfigureAwait(false);
            delivered++;
        }

        if (delivered > 0)
        {
            _logger.LogWarning("reconciliation redelivered {Count} payments the handler had not accepted", delivered);
        }

        return delivered;
    }

    /// <summary>
    /// Re-reads a window of ledgers below the cursor. Every payment it has to insert is a defect, so each
    /// one is logged as an error rather than counted quietly.
    /// </summary>
    private async Task<int> SweepAsync(List<string> errors, CancellationToken cancellationToken)
    {
        MonitorSnapshotData data = _snapshot.Read();
        if (data.Cursor is not { } cursor || cursor == 0)
        {
            return 0;
        }

        uint from = cursor > _options.ReconcileWindow ? cursor - _options.ReconcileWindow : 1;
        int recovered = 0;

        foreach (Uri candidate in _options.EffectiveCatchUpNodes)
        {
            try
            {
                await using IXrplNodeConnection connection = _connectionFactory.Create(candidate);
                await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);

                CatchUpResult result = await _catchUp.RunAsync(
                    connection,
                    _options.Address,
                    from,
                    cursor,
                    async (transaction, token) =>
                    {
                        if (await RecoverAsync(transaction, token).ConfigureAwait(false))
                        {
                            recovered++;
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                if (result.Completed)
                {
                    return recovered;
                }

                errors.Add(result.Reason ?? $"the sweep over {from}-{cursor} on {candidate} could not be proven complete");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"sweep against {candidate} failed: {ex.Message}");
            }
        }

        return recovered;
    }

    /// <summary>Returns true when the payment was missing from the store and had to be recorded now.</summary>
    private async Task<bool> RecoverAsync(IAccountTransaction transaction, CancellationToken cancellationToken)
    {
        ProcessingResult result = _processor.Process(transaction);
        if (result.Record is not { } record)
        {
            return false;
        }

        bool isNew = await _dispatcher.RecordAsync(record, cancellationToken).ConfigureAwait(false);
        if (!isNew)
        {
            return false;
        }

        _logger.LogError(
            "reconciliation found payment {Hash} in ledger {Ledger} that the monitor never recorded; it has been recorded now",
            record.TransactionHash,
            record.LedgerIndex);

        await _dispatcher.DeliverAsync(record, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.XrplPaymentGatewayTests" -class "Xrpl.PaymentGateway.Tests.PaymentMonitorHealthTests"
```

Expected: 11 tests pass.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat: add tag issuance, health reporting and reconciliation"
```

---

### Task 11: Real node connection and DI wiring

The first code that touches a socket. Everything above was proven against the fake, so a mistake here shows up as a connection failure, not a logic bug.

**Files:**
- Create: `src/Xrpl.PaymentGateway/Internal/XrplNodeConnection.cs`
- Create: `src/Xrpl.PaymentGateway/Internal/XrplNodeConnectionFactory.cs`
- Create: `src/Xrpl.PaymentGateway/ServiceCollectionExtensions.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/ServiceCollectionExtensionsTests.cs`

- [ ] **Step 1: Write the failing registration tests**

`tests/Xrpl.PaymentGateway.Tests/ServiceCollectionExtensionsTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class ServiceCollectionExtensionsTests
{
    private static ServiceCollection BaseServices()
    {
        ServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPaymentStore>(new InMemoryPaymentStore());
        services.AddSingleton<IPaymentReceivedHandler, Fakes.RecordingHandler>();
        return services;
    }

    [Fact]
    public void RegistrationResolvesTheGatewayTheHealthServiceAndTheHostedMonitor()
    {
        ServiceCollection services = BaseServices();
        services.AddXrplPaymentGateway(options =>
        {
            options.Address = "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p";
            options.Nodes = new[] { new Uri("ws://localhost:6006") };
        });

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<XrplPaymentGateway>(provider.GetRequiredService<IPaymentGateway>());
        Assert.IsType<PaymentMonitorHealth>(provider.GetRequiredService<IPaymentMonitorHealth>());
        Assert.Single(provider.GetServices<IHostedService>().OfType<XrplPaymentMonitor>());
    }

    [Fact]
    public void InvalidOptionsFailWhenTheOptionsAreFirstRead()
    {
        ServiceCollection services = BaseServices();
        services.AddXrplPaymentGateway(options => options.Address = string.Empty);

        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException error = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<PaymentGatewayOptions>>().Value);
        Assert.Contains("Address", string.Join(" ", error.Failures));
    }

    [Fact]
    public void AHostSuppliedStoreIsNotReplaced()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();
        ServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPaymentStore>(store);
        services.AddSingleton<IPaymentReceivedHandler, Fakes.RecordingHandler>();
        services.AddXrplPaymentGateway(options =>
        {
            options.Address = "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p";
            options.Nodes = new[] { new Uri("ws://localhost:6006") };
        });

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(store, provider.GetRequiredService<IPaymentStore>());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -class "Xrpl.PaymentGateway.Tests.ServiceCollectionExtensionsTests"
```

Expected: compile error, `AddXrplPaymentGateway` does not exist.

- [ ] **Step 3: Write `XrplNodeConnection`**

`src/Xrpl.PaymentGateway/Internal/XrplNodeConnection.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Xrpl.Client;
using Xrpl.Models;
using Xrpl.Models.Methods;
using Xrpl.Models.Subscriptions;

namespace Xrpl.PaymentGateway.Internal;

/// <summary>
/// One <see cref="XrplClient"/> session. The SDK's own reconnect loop is kept to a single attempt: node
/// rotation is the monitor's job, and two independent reconnect policies would fight each other.
/// </summary>
internal sealed class XrplNodeConnection : IXrplNodeConnection
{
    private readonly XrplClient _client;
    private readonly ILogger _logger;
    private string? _account;
    private int _disposed;

    public XrplNodeConnection(Uri node, ILogger logger)
    {
        Node = node;
        _logger = logger;
        _client = new XrplClient(
            node.ToString(),
            new XrplClient.ClientOptions
            {
                MaxReconnectAttempts = 1,
                StopAfterMaxAttempts = true,
                RequestPolicy = RequestFailurePolicy.ImmediateFail,
            });

        _client.OnTransaction += HandleTransactionAsync;
        _client.OnLedgerClosed += HandleLedgerClosedAsync;
        _client.OnSessionEnded += HandleSessionEndedAsync;
        _client.OnConnected += ResubscribeAsync;
    }

    public Uri Node { get; }

    public Func<IAccountTransaction, Task>? OnTransaction { get; set; }

    public Func<ulong, Task>? OnLedgerClosed { get; set; }

    public Func<string, Task>? OnSessionEnded { get; set; }

    public Task ConnectAsync(CancellationToken cancellationToken) => _client.Connect(cancellationToken);

    public async Task SubscribeToAccountAsync(string account, CancellationToken cancellationToken)
    {
        _account = account;

        // Subscribing by account delivers only this account's transactions. The transactions stream would
        // deliver every transaction on the network for the same information.
        await _client.Subscribe(
            new SubscribeRequest
            {
                Accounts = new List<string> { account },
                Streams = new List<StreamType> { StreamType.Ledger },
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeStatus> GetNodeStatusAsync(CancellationToken cancellationToken)
    {
        XrplResponse<ServerInfo> response = await _client
            .ServerInfo(new ServerInfoRequest(), cancellationToken)
            .ConfigureAwait(false);

        Info info = response.Result.Info;

        return new NodeStatus
        {
            ServerState = info.ServerState.ToString().ToLowerInvariant(),
            ValidatedLedgerIndex = info.ValidatedLedger is { Sequence: > 0 } ledger ? (uint)ledger.Sequence : null,
            CompleteLedgers = info.CompleteLedgers,
        };
    }

    public async Task<AccountTransactionPage> GetAccountTransactionsAsync(
        AccountTransactionQuery query,
        CancellationToken cancellationToken)
    {
        AccountTransactionsRequest request = new AccountTransactionsRequest(query.Account)
        {
            LedgerIndexMin = (int)query.LedgerIndexMin,
            LedgerIndexMax = (int)query.LedgerIndexMax,
            Limit = query.Limit,
            Marker = query.Marker,
            Forward = true,
        };

        XrplResponse<AccountTransactions> response = await _client
            .AccountTransactions(request, cancellationToken)
            .ConfigureAwait(false);

        AccountTransactions result = response.Result;

        return new AccountTransactionPage
        {
            Transactions = result.Transactions?.Cast<IAccountTransaction>().ToList() ?? new List<IAccountTransaction>(),
            Marker = result.Marker,
            LedgerIndexMin = result.LedgerIndexMin,
            LedgerIndexMax = result.LedgerIndexMax,
        };
    }

    private async Task ResubscribeAsync()
    {
        if (_account is not { } account)
        {
            return;
        }

        try
        {
            await SubscribeToAccountAsync(account, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("re-subscribed to {Account} on {Node} after the socket came back", account, Node);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "re-subscribing to {Account} on {Node} failed", account, Node);
        }
    }

    private Task HandleTransactionAsync(TransactionStream stream) =>
        OnTransaction?.Invoke(stream) ?? Task.CompletedTask;

    private Task HandleLedgerClosedAsync(LedgerStream stream) =>
        OnLedgerClosed?.Invoke(stream.LedgerIndex) ?? Task.CompletedTask;

    private Task HandleSessionEndedAsync(SessionEndReason reason, string description) =>
        OnSessionEnded?.Invoke($"{reason}: {description}") ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _client.OnTransaction -= HandleTransactionAsync;
        _client.OnLedgerClosed -= HandleLedgerClosedAsync;
        _client.OnSessionEnded -= HandleSessionEndedAsync;
        _client.OnConnected -= ResubscribeAsync;

        try
        {
            await _client.DisconnectAndWaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "disconnecting from {Node} was not clean", Node);
        }

        _client.Dispose();
    }
}
```

Two import traps in this one file: `StreamType` lives in `Xrpl.Models`, not `Xrpl.Models.Enums`, despite the file path; and `TransactionStream` lives in `Xrpl.Models.Subscriptions`, which nothing else here pulls in.

- [ ] **Step 4: Write the factory**

`src/Xrpl.PaymentGateway/Internal/XrplNodeConnectionFactory.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Xrpl.PaymentGateway.Internal;

internal sealed class XrplNodeConnectionFactory : IXrplNodeConnectionFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public XrplNodeConnectionFactory(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    public IXrplNodeConnection Create(Uri node) =>
        new XrplNodeConnection(node, _loggerFactory.CreateLogger($"Xrpl.PaymentGateway.Node[{node}]"));
}
```

- [ ] **Step 5: Write `ServiceCollectionExtensions`**

`src/Xrpl.PaymentGateway/ServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;

namespace Xrpl.PaymentGateway;

/// <summary>Registration for the payment gateway.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the gateway, the health service and the background monitor. The host must separately
    /// register its own <see cref="IPaymentStore"/> and <see cref="IPaymentReceivedHandler"/>.
    /// </summary>
    public static IServiceCollection AddXrplPaymentGateway(
        this IServiceCollection services,
        Action<PaymentGatewayOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<PaymentGatewayOptions>, PaymentGatewayOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<MonitorSnapshot>();
        services.TryAddSingleton<IXrplNodeConnectionFactory, XrplNodeConnectionFactory>();
        services.TryAddSingleton<IPaymentGateway, XrplPaymentGateway>();
        services.TryAddSingleton<IPaymentMonitorHealth, PaymentMonitorHealth>();
        services.AddHostedService<XrplPaymentMonitor>();

        return services;
    }
}
```

- [ ] **Step 6: Run the whole suite**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests
```

Expected: every test from Tasks 1-11 passes.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat: connect to real nodes and wire the gateway into DI"
```

---

### Task 12: Sample API

A minimal host that exercises every public entry point: issue instructions, watch payments arrive, read health, run reconciliation on demand.

**Files:**
- Create: `samples/Xrpl.PaymentGateway.SampleApi/Xrpl.PaymentGateway.SampleApi.csproj`
- Create: `samples/Xrpl.PaymentGateway.SampleApi/Program.cs`
- Create: `samples/Xrpl.PaymentGateway.SampleApi/SamplePaymentHandler.cs`
- Create: `samples/Xrpl.PaymentGateway.SampleApi/appsettings.json`
- Modify: `Xrpl.PaymentGateway.sln`

- [ ] **Step 1: Create the project file**

`samples/Xrpl.PaymentGateway.SampleApi/Xrpl.PaymentGateway.SampleApi.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <RootNamespace>Xrpl.PaymentGateway.SampleApi</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Xrpl.PaymentGateway\Xrpl.PaymentGateway.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the sample handler**

`samples/Xrpl.PaymentGateway.SampleApi/SamplePaymentHandler.cs`:

```csharp
using System.Collections.Concurrent;
using Xrpl.PaymentGateway.Abstractions;

namespace Xrpl.PaymentGateway.SampleApi;

/// <summary>
/// Stands in for whatever a real host does on payment: activate a subscription, release an order,
/// credit a balance. It only logs and remembers, so the sample stays inspectable over HTTP.
/// </summary>
public sealed class SamplePaymentHandler : IPaymentReceivedHandler
{
    private readonly ConcurrentQueue<DeliveredPayment> _delivered = new ConcurrentQueue<DeliveredPayment>();
    private readonly ILogger<SamplePaymentHandler> _logger;

    public SamplePaymentHandler(ILogger<SamplePaymentHandler> logger) => _logger = logger;

    public IReadOnlyCollection<DeliveredPayment> Delivered => _delivered.ToArray();

    public Task OnPaymentReceivedAsync(PaymentRecord payment, string? buyerId, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "buyer {Buyer} paid {Value} {Currency} (tx {Hash}, tag {Tag}, ledger {Ledger})",
            buyerId ?? "unknown",
            payment.Value,
            payment.Currency,
            payment.TransactionHash,
            payment.DestinationTag,
            payment.LedgerIndex);

        _delivered.Enqueue(new DeliveredPayment(
            payment.TransactionHash,
            buyerId,
            payment.Sender,
            payment.Currency,
            payment.Issuer,
            payment.Value,
            payment.LedgerIndex,
            payment.ProcessedAt));

        return Task.CompletedTask;
    }
}

/// <summary>What the sample shows for a delivered payment.</summary>
public sealed record DeliveredPayment(
    string TransactionHash,
    string? BuyerId,
    string Sender,
    string Currency,
    string? Issuer,
    decimal Value,
    uint LedgerIndex,
    DateTimeOffset ProcessedAt);
```

- [ ] **Step 3: Write `Program.cs`**

`samples/Xrpl.PaymentGateway.SampleApi/Program.cs`:

```csharp
using Xrpl.PaymentGateway;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.SampleApi;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// The store is the host's choice. This sample keeps everything in memory; swapping in Postgres or a
// file is a matter of implementing IPaymentStore, with no change to anything below.
// Note that tag allocation belongs to the store, so the store is where the first tag is configured —
// PaymentGatewayOptions.FirstDestinationTag is the value to hand it, not a setting the library applies
// behind your back.
uint firstTag = builder.Configuration.GetValue<uint?>("Xrpl:FirstDestinationTag") ?? 1;
builder.Services.AddSingleton<InMemoryPaymentStore>(_ => new InMemoryPaymentStore(firstTag));
builder.Services.AddSingleton<IPaymentStore>(services => services.GetRequiredService<InMemoryPaymentStore>());

builder.Services.AddSingleton<SamplePaymentHandler>();
builder.Services.AddSingleton<IPaymentReceivedHandler>(services => services.GetRequiredService<SamplePaymentHandler>());

builder.Services.AddXrplPaymentGateway(options =>
{
    options.Address = builder.Configuration["Xrpl:Address"]
        ?? throw new InvalidOperationException("configure Xrpl:Address with the receiving r-address");
    options.Nodes = (builder.Configuration.GetSection("Xrpl:Nodes").Get<string[]>() ?? ["ws://localhost:6006"])
        .Select(node => new Uri(node))
        .ToArray();
    options.FirstDestinationTag = firstTag;
});

WebApplication app = builder.Build();

app.MapPost("/checkout/{buyerId}", async (
    string buyerId,
    IPaymentGateway gateway,
    CancellationToken cancellationToken) =>
{
    PaymentInstructions instructions = await gateway.GetPaymentInstructionsAsync(buyerId, cancellationToken);
    return Results.Ok(new { instructions.Address, instructions.DestinationTag });
});

app.MapGet("/payments", (SamplePaymentHandler handler) => Results.Ok(handler.Delivered));

app.MapGet("/recorded", (InMemoryPaymentStore store) => Results.Ok(store.Snapshot()));

app.MapGet("/health", async (IPaymentMonitorHealth health, CancellationToken cancellationToken) =>
{
    PaymentMonitorHealthReport report = await health.CheckAsync(cancellationToken);
    return report.IsHealthy ? Results.Ok(report) : Results.Json(report, statusCode: 503);
});

app.MapPost("/reconcile", async (IPaymentMonitorHealth health, CancellationToken cancellationToken) =>
    Results.Ok(await health.ReconcileAsync(cancellationToken)));

app.Run();
```

- [ ] **Step 4: Write `appsettings.json`**

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Xrpl.PaymentGateway": "Information"
    }
  },
  "AllowedHosts": "*",
  "Xrpl": {
    "Address": "",
    "Nodes": [ "ws://localhost:6006" ],
    "FirstDestinationTag": 1
  }
}
```

- [ ] **Step 5: Add the sample to the solution and build**

```bash
dotnet sln add samples/Xrpl.PaymentGateway.SampleApi/Xrpl.PaymentGateway.SampleApi.csproj
```

Then:

```bash
dotnet build --nologo
```

Expected: the whole solution builds.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: add a sample API that exercises the public surface"
```

---

### Task 13: Standalone rippled stand and the end-to-end test

Everything so far was proven against a fake. This proves it against a real node: a real payment, a real subscription, a real balance change.

**Files:**
- Create: `.ci-config/docker-compose.ci.yml`
- Copy: `.ci-config/rippled.cfg` and `.ci-config/validators.txt` from the XrplCSharp repository
- Create: `tests/Xrpl.PaymentGateway.Tests/Integration/StandaloneFixture.cs`
- Create: `tests/Xrpl.PaymentGateway.Tests/Integration/EndToEndPaymentTests.cs`

- [ ] **Step 1: Copy the rippled configuration from the SDK repository**

The config files are generated by the SDK's `generate-amendments.sh` and must not be hand-written.

```bash
cp E:/Claude/XrplCSharp/xrpl-encrypted-messenger-59ef6a/.ci-config/rippled.cfg .ci-config/rippled.cfg
```

Then:

```bash
cp E:/Claude/XrplCSharp/xrpl-encrypted-messenger-59ef6a/.ci-config/validators.txt .ci-config/validators.txt
```

- [ ] **Step 2: Write the compose file**

`.ci-config/docker-compose.ci.yml`:

```yaml
# Bring this up with an explicit project name so it cannot collide with the XrplCSharp stand:
#   docker compose -p xrplpg-ci -f .ci-config/docker-compose.ci.yml up -d
# It publishes the same ports, so only one of the two stands can run at a time.
services:
  xrpld:
    image: xrpllabsofficial/xrpld:3.3.0
    container_name: xrplpg-rippled
    command: ["-a", "--start"]
    ports:
      # Loopback only: every port below grants the admin role, which means node control, not just reads.
      - "127.0.0.1:5005:5005"
      - "127.0.0.1:5006:5006"
      - "127.0.0.1:6006:6006"
    volumes:
      - ./rippled.cfg:/config/rippled.cfg:ro
      - ./validators.txt:/config/validators.txt:ro

  ledger-acceptor:
    image: curlimages/curl:latest
    container_name: xrplpg-ledger-acceptor
    depends_on:
      - xrpld
    entrypoint:
      - sh
      - -c
      - |
        sleep 3
        while true; do
          curl -s -X POST http://xrpld:5006/ \
            -H "Content-Type: application/json" \
            -d '{"method":"ledger_accept"}'
          sleep 4
        done
    restart: unless-stopped
```

- [ ] **Step 3: Write the standalone fixture**

`tests/Xrpl.PaymentGateway.Tests/Integration/StandaloneFixture.cs`:

```csharp
using Xrpl.Client;
using Xrpl.Models.Common;
using Xrpl.Models.Methods;
using Xrpl.Models.Transactions;
using Xrpl.Sugar;
using Xrpl.Wallet;

namespace Xrpl.PaymentGateway.Tests.Integration;

/// <summary>
/// Talks to the standalone rippled from .ci-config. The stand closes a ledger every four seconds through
/// its ledger-acceptor container, so the waits here are for validation, not for the node to be prodded.
/// </summary>
public static class StandaloneFixture
{
    public const string NodeUrl = "ws://localhost:6006";
    public const string MasterAccount = "rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh";
    public const string MasterSecret = "snoPBrXtMeMyMHUVTgbuqAfg1SUTb";

    /// <summary>Connects, or returns null when no stand is running so the test can skip.</summary>
    public static async Task<XrplClient?> TryConnectAsync()
    {
        XrplClient client = new XrplClient(
            NodeUrl,
            new XrplClient.ClientOptions
            {
                MaxReconnectAttempts = 1,
                StopAfterMaxAttempts = true,
                ConnectionAttemptTimeout = TimeSpan.FromSeconds(5),
                RequestPolicy = RequestFailurePolicy.ImmediateFail,
            });

        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.Connect(cts.Token);
            return client;
        }
        catch (Exception)
        {
            client.Dispose();
            return null;
        }
    }

    /// <summary>Creates a funded account and waits until the ledger has validated it.</summary>
    public static async Task<XrplWallet> CreateFundedWalletAsync(XrplClient client, decimal xrp = 400m)
    {
        XrplWallet wallet = XrplWallet.Generate();
        XrplWallet master = XrplWallet.FromSeed(MasterSecret);

        Payment funding = new Payment
        {
            Account = MasterAccount,
            Destination = wallet.ClassicAddress,
            Amount = new Currency { CurrencyCode = "XRP", ValueAsXrp = xrp },
        };

        Submit response = await client.Submit(funding, master, true);
        if (response.EngineResult != "tesSUCCESS")
        {
            throw new InvalidOperationException($"funding {wallet.ClassicAddress} failed with {response.EngineResult}");
        }

        await WaitUntilFundedAsync(client, wallet.ClassicAddress);
        return wallet;
    }

    /// <summary>Polls until the account exists in a validated ledger.</summary>
    public static async Task WaitUntilFundedAsync(XrplClient client, string address, int timeoutSeconds = 40)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                decimal balance = await client.GetXrpFreeBalance(address);
                if (balance > 0m)
                {
                    return;
                }
            }
            catch (Exception)
            {
                // The account is not in a validated ledger yet.
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException($"account {address} was not validated within {timeoutSeconds} seconds");
    }

    /// <summary>Sends XRP with a destination tag and waits only for provisional acceptance.</summary>
    public static async Task<string> SendTaggedPaymentAsync(
        XrplClient client,
        XrplWallet from,
        string destination,
        uint destinationTag,
        decimal xrp)
    {
        Payment payment = new Payment
        {
            Account = from.ClassicAddress,
            Destination = destination,
            DestinationTag = destinationTag,
            Amount = new Currency { CurrencyCode = "XRP", ValueAsXrp = xrp },
        };

        Submit response = await client.Submit(payment, from, true);
        if (response.EngineResult != "tesSUCCESS")
        {
            throw new InvalidOperationException($"payment failed with {response.EngineResult}");
        }

        // Submit.TxJson is declared as object, so it has no Hash. Submit.Transaction is the computed
        // ITransactionResponse that does.
        return response.Transaction?.Hash ?? string.Empty;
    }
}
```

`GetXrpFreeBalance` is an extension method on `IXrplClient` from `Xrpl.Sugar` (class `BalancesSugar`), which is why that import is there; `Submit` is an ordinary instance method on the client and needs nothing extra.

- [ ] **Step 4: Write the end-to-end test**

`tests/Xrpl.PaymentGateway.Tests/Integration/EndToEndPaymentTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xrpl.Client;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xrpl.Wallet;
using Xunit;

namespace Xrpl.PaymentGateway.Tests.Integration;

[Trait("Category", "Integration")]
public class EndToEndPaymentTests
{
    [Fact]
    public async Task ATaggedPaymentOnAStandaloneLedgerReachesTheHostHandler()
    {
        using XrplClient? node = await StandaloneFixture.TryConnectAsync();
        if (node is null)
        {
            Assert.Skip("no standalone rippled on ws://localhost:6006; start .ci-config/docker-compose.ci.yml");
        }

        XrplWallet receiver = await StandaloneFixture.CreateFundedWalletAsync(node);
        XrplWallet buyer = await StandaloneFixture.CreateFundedWalletAsync(node);

        RecordingHandler handler = new RecordingHandler();
        InMemoryPaymentStore store = new InMemoryPaymentStore();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IPaymentStore>(store);
        builder.Services.AddSingleton<IPaymentReceivedHandler>(handler);
        builder.Services.AddXrplPaymentGateway(options =>
        {
            options.Address = receiver.ClassicAddress;
            options.Nodes = new[] { new Uri(StandaloneFixture.NodeUrl) };
        });

        using IHost host = builder.Build();
        IPaymentGateway gateway = host.Services.GetRequiredService<IPaymentGateway>();
        PaymentInstructions instructions = await gateway.GetPaymentInstructionsAsync("buyer-1", TestContext.Current.CancellationToken);

        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            MonitorSnapshot snapshot = host.Services.GetRequiredService<MonitorSnapshot>();
            await TestWait.UntilAsync(
                () => snapshot.Read().State == PaymentMonitorState.Streaming,
                "the monitor to reach the streaming state",
                timeoutMs: 30000);

            await StandaloneFixture.SendTaggedPaymentAsync(
                node, buyer, receiver.ClassicAddress, instructions.DestinationTag, 25m);

            await TestWait.UntilAsync(
                () => handler.Deliveries.Count == 1,
                "the payment to reach the host handler",
                timeoutMs: 60000);

            (PaymentRecord payment, string? buyerId) = handler.Deliveries[0];
            Assert.Equal("buyer-1", buyerId);
            Assert.Equal("XRP", payment.Currency);
            Assert.Equal(25m, payment.Value);
            Assert.Equal(buyer.ClassicAddress, payment.Sender);
            Assert.Equal(instructions.DestinationTag, payment.DestinationTag);
            Assert.Empty(await store.GetUnhandledPaymentsAsync(10, TestContext.Current.CancellationToken));

            PaymentMonitorHealthReport report = await host.Services
                .GetRequiredService<IPaymentMonitorHealth>()
                .CheckAsync(TestContext.Current.CancellationToken);
            Assert.Equal(PaymentMonitorState.Streaming, report.State);
            Assert.Equal(0, report.AnomalyCount);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task APaymentMissedWhileDisconnectedIsPickedUpByCatchUp()
    {
        using XrplClient? node = await StandaloneFixture.TryConnectAsync();
        if (node is null)
        {
            Assert.Skip("no standalone rippled on ws://localhost:6006; start .ci-config/docker-compose.ci.yml");
        }

        XrplWallet receiver = await StandaloneFixture.CreateFundedWalletAsync(node);
        XrplWallet buyer = await StandaloneFixture.CreateFundedWalletAsync(node);

        InMemoryPaymentStore store = new InMemoryPaymentStore();
        RecordingHandler handler = new RecordingHandler();

        // Pin the cursor below the payment, then start: the monitor has never seen the transaction live,
        // so only catch-up can find it.
        uint startLedger = await CurrentValidatedLedgerAsync(node);
        await store.SetLastProcessedLedgerAsync(startLedger, TestContext.Current.CancellationToken);
        uint tag = await store.GetOrAssignTagAsync("buyer-2", TestContext.Current.CancellationToken);

        await StandaloneFixture.SendTaggedPaymentAsync(node, buyer, receiver.ClassicAddress, tag, 7m);
        await Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IPaymentStore>(store);
        builder.Services.AddSingleton<IPaymentReceivedHandler>(handler);
        builder.Services.AddXrplPaymentGateway(options =>
        {
            options.Address = receiver.ClassicAddress;
            options.Nodes = new[] { new Uri(StandaloneFixture.NodeUrl) };
        });

        using IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await TestWait.UntilAsync(
                () => handler.Deliveries.Count == 1,
                "catch-up to find the payment sent while the monitor was down",
                timeoutMs: 60000);

            Assert.Equal(7m, handler.Deliveries[0].Payment.Value);
            Assert.Equal("buyer-2", handler.Deliveries[0].BuyerId);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task<uint> CurrentValidatedLedgerAsync(XrplClient client)
    {
        XrplResponse<Xrpl.Models.Methods.ServerInfo> response =
            await client.ServerInfo(new Xrpl.Models.Methods.ServerInfoRequest());
        return (uint)(response.Result.Info.ValidatedLedger?.Sequence ?? 0);
    }
}
```

If `Assert.Skip` is unavailable in the installed xUnit version, replace each skip with an early `return` and a `Console.WriteLine` explaining why, and keep the `Category=Integration` trait as the real gate.

- [ ] **Step 5: Check whether a stand is already running before starting one**

```bash
docker ps --format "{{.Names}}\t{{.Ports}}"
```

On this machine a standalone `rippled` 3.3.0 from the neighbouring `xrpl-video-platform` project already
occupies 5005/5006/6006 and is healthy (`server_state: proposing`, `complete_ledgers: 2-…`, master account
funded). **Use it as-is and skip to Step 6** — do not bring it down and do not start a second stand, which
would fail on the ports anyway. Start our own only when nothing is listening there:

```bash
docker compose -p xrplpg-ci -f .ci-config/docker-compose.ci.yml up -d
```

Worth noting for the code: that node reports `proposing`, not `full`. `NodeStatus.IsSynced` accepts
`full`, `validating` and `proposing` precisely for this reason — narrowing it to `full` would make the
monitor treat a perfectly healthy standalone node as stalled.

Wait for the node to answer, then confirm it is producing ledgers:

```bash
docker logs xrplpg-ledger-acceptor --tail 5
```

Expected: JSON responses from `ledger_accept` with an increasing `ledger_current_index`.

- [ ] **Step 6: Run the unit tests without the integration ones**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -trait- "Category=Integration"
```

Expected: every unit test passes.

- [ ] **Step 7: Run the integration tests**

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -trait "Category=Integration"
```

Expected: both tests pass. If the first hangs at "the monitor to reach the streaming state", check `docker logs xrplpg-rippled` — an unreachable node surfaces as a connection error in the test host's log output.

- [ ] **Step 8: Stop the stand — only if you started it**

```bash
docker compose -p xrplpg-ci -f .ci-config/docker-compose.ci.yml down
```

Skip this entirely if you reused the neighbouring project's node in Step 5.

- [ ] **Step 9: Commit**

```bash
git add -A && git commit -m "test: prove the gateway end to end against a standalone ledger"
```

---

### Task 14: Documentation and CI

**Files:**
- Create: `README.md`
- Create: `.github/workflows/dotnet.test.yml`
- Create: `CHANGES.md`

- [ ] **Step 1: Write `README.md`**

````markdown
# Xrpl.PaymentGateway

Accept XRP Ledger payments in any .NET application, and record them wherever you like.

The library watches one receiving account, hands each buyer a stable destination tag, and records every
incoming payment exactly once. Where those records live is your decision: implement one interface and the
gateway writes through it, whether that is PostgreSQL, a file, or something else entirely.

## Install

```
dotnet add package Xrpl.PaymentGateway
```

Implement storage against `Xrpl.PaymentGateway.Abstractions`, which has no dependency on the XRPL SDK.

## Use

```csharp
builder.Services.AddSingleton<IPaymentStore, MyPostgresPaymentStore>();
builder.Services.AddSingleton<IPaymentReceivedHandler, MyOrderActivator>();

builder.Services.AddXrplPaymentGateway(options =>
{
    options.Address = "rYourReceivingAddress";
    options.Nodes =
    [
        new Uri("wss://xrplcluster.com"),
        new Uri("wss://s1.ripple.com"),
    ];
});
```

Issue instructions when a buyer reaches checkout:

```csharp
PaymentInstructions instructions = await gateway.GetPaymentInstructionsAsync(buyerId, cancellationToken);
```

React when the money arrives:

```csharp
public sealed class MyOrderActivator : IPaymentReceivedHandler
{
    public Task OnPaymentReceivedAsync(PaymentRecord payment, string? buyerId, CancellationToken ct)
    {
        // Called at least once per payment. Make this idempotent.
        return activations.ActivateAsync(buyerId, payment.Value, ct);
    }
}
```

## What it guarantees

- **Exactly once in the store.** Records are keyed by transaction hash, and the store rejects duplicates.
- **At least once to your handler.** Delivery is retried by reconciliation until it succeeds, so handlers
  must be idempotent.
- **No gaps across disconnects.** A persisted ledger cursor marks the boundary below which completeness is
  proven. Every reconnect subscribes first, then replays `account_tx` from the cursor.
- **Amounts as delivered.** Values come from transaction metadata balance changes, not the `Amount` field,
  so a partial payment is recorded at what actually arrived.

## What it expects of the receiving account

Use a dedicated account that only receives. Specifically, it must not have `DefaultRipple` enabled and
should not hold DEX offers or AMM positions. A transaction that both credits and debits the account is
treated as an exchange rather than a payment: it is refused, logged as an error, and counted in the health
report rather than credited to a buyer.

## Health and reconciliation

Call these from whatever scheduler you already run — Hangfire, Quartz, a timer:

```csharp
PaymentMonitorHealthReport report = await health.CheckAsync(cancellationToken);   // cheap, call often
ReconciliationResult result = await health.ReconcileAsync(cancellationToken);     // slower, call hourly
```

`ReconcileAsync` redelivers anything the handler never accepted and re-verifies a window of ledgers below
the cursor. A non-zero `RecoveredCount` means a payment was missing from the store: investigate, because
the monitor should never let that happen.

## Operational notes

- Run **one** monitor instance per receiving account. A second one produces no duplicate records, but it is
  not a supported configuration.
- Catch-up refuses nodes whose `complete_ledgers` does not cover the range it needs. If the health report
  says `HistoryGap`, add a full-history node to `CatchUpNodes`.
- A network-wide consensus stall is reported as `NetworkStalled` rather than treated as a node failure. No
  payments are lost: nothing is being validated while the network is stopped.

## Development

```
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -trait- "Category=Integration"
```

Integration tests need a standalone node:

```
docker compose -p xrplpg-ci -f .ci-config/docker-compose.ci.yml up -d
dotnet run --project tests/Xrpl.PaymentGateway.Tests -- -trait "Category=Integration"
docker compose -p xrplpg-ci -f .ci-config/docker-compose.ci.yml down
```

The stand publishes the same ports as the XrplCSharp CI stand, so only one of the two can run at a time.
````

- [ ] **Step 2: Write the CI workflow**

`.github/workflows/dotnet.test.yml`:

```yaml
name: build and test

on:
  push:
    branches: [dev, release]
  pull_request:
    branches: [dev, release]

concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: ${{ github.event_name == 'pull_request' }}

jobs:
  unit:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: |
            8.0.x
            9.0.x
            10.0.x
      - run: dotnet restore
      - run: dotnet build --no-restore --configuration Release
      - run: dotnet run --project tests/Xrpl.PaymentGateway.Tests --no-build -c Release -- -trait- "Category=Integration"

  integration:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - name: start standalone rippled
        run: docker compose -p xrplpg-ci -f .ci-config/docker-compose.ci.yml up -d
      - name: wait for the node
        run: |
          for i in $(seq 1 60); do
            if curl -s -X POST http://localhost:5005/ -H 'Content-Type: application/json' \
              -d '{"method":"server_info"}' | grep -q '"complete_ledgers"'; then
              echo "node is up"
              exit 0
            fi
            sleep 2
          done
          echo "node did not come up in time"
          docker compose -p xrplpg-ci -f .ci-config/docker-compose.ci.yml logs
          exit 1
      - run: dotnet run --project tests/Xrpl.PaymentGateway.Tests -c Release -- -trait "Category=Integration"
      - if: always()
        run: docker compose -p xrplpg-ci -f .ci-config/docker-compose.ci.yml down
```

- [ ] **Step 3: Write `CHANGES.md`**

```markdown
# Changelog

## 0.1.0 — unreleased

First release.

- `IPaymentStore` abstraction so the host decides where payments are recorded.
- Sequential destination tag allocation, stable per buyer.
- Background monitor: account subscription, catch-up after every reconnect, node rotation, network-stall
  detection, node history verification.
- Amounts computed from transaction metadata balance changes, so partial payments record what arrived.
- `IPaymentMonitorHealth` for liveness reporting and reconciliation from any scheduler.
- `InMemoryPaymentStore` reference implementation and a sample API.
```

- [ ] **Step 4: Run the full verification**

```bash
dotnet build --nologo --configuration Release
```

Then:

```bash
dotnet run --project tests/Xrpl.PaymentGateway.Tests -c Release -- -trait- "Category=Integration"
```

Expected: a clean build for all three target frameworks and a fully green unit suite.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "docs: document usage, guarantees and the test stand"
```

---

## Self-Review

Checked against `docs/specs/2026-08-25-payment-gateway-design.md`:

| Spec requirement | Task |
|---|---|
| Two packages, Abstractions free of the SDK | 1 |
| `PaymentRecord` with a single amount, sender filter, anomaly path | 1, 4 |
| `IPaymentStore` with atomicity and uniqueness requirements | 1, 2 |
| Sequential tags from `FirstDestinationTag`, stable per buyer | 2, 10 |
| Handler in DI, at-least-once, cannot block recording | 5, 11 |
| Subscribe → catch-up → stream ordering | 9 |
| Cursor as a proven-completeness boundary, advanced to `N−1` | 9 |
| Node pool rotation with jittered backoff | 6, 9 |
| Node stall vs network-wide stall | 9 |
| `complete_ledgers` and echoed-range verification, `CatchUpNodes` | 3, 7, 9 |
| Store outage: unbounded retries, frozen cursor, buffer overflow drops the session | 5, 8, 9 |
| Health report and reconciliation from any scheduler | 10 |
| xUnit v3, fixtures incl. partial payment, `InMemoryPaymentStore` | 1-10 |
| Docker standalone integration tests | 13 |
| Private repo, English artifacts, CI | 14 |

Deliberate additions beyond the spec, all consistent with it:

1. **Debit-and-credit transactions are refused, not recorded.** The spec covered multiple credits but not a
   simultaneous debit. Recording a phantom payment could release goods for value not received, so the safer
   reading of "no silent loss" is a loud refusal: error log, anomaly counter, health report.
2. **`NetworkStallProbeInterval`** exists so the network-stall probe runs at the 30-second cadence the spec
   names, rather than inheriting the 20-second stall timeout.
3. **Extra options not named in the spec:** `StoreRetryBaseDelay` / `StoreRetryMaxDelay`,
   `HealthUnhandledSampleSize`, `MaxAcceptableLedgerLag`. All three exist because the spec's behaviour
   needs a number and hard-coding one would be worse.
4. **Extra report fields:** `PaymentMonitorHealthReport.IsHealthy` and `.LastLedgerAt`, and
   `ReconciliationResult.Skipped`. The first two let a `/health` endpoint answer without re-deriving the
   rule; the third distinguishes "nothing to do" from "another run held the lock".
5. **`XrplPaymentMonitor` and `PaymentMonitorHealth` are internal, not public.** Their constructors take
   internal types, and C# forbids a public constructor exposing less accessible types. Hosts reach them
   through `IHostedService` and `IPaymentMonitorHealth`, so the public surface loses nothing.

These are recorded rather than silently absorbed, and the spec should be amended if they are accepted.

### Corrections applied after review

An independent review of this plan against the SDK sources, plus a compile-and-run probe of the toolchain,
found defects that are now fixed here. Recorded so the same ground is not re-covered:

| Defect | Where it was | Fix |
|---|---|---|
| Public classes with internal-typed constructor parameters — CS0051, would not compile | Tasks 9, 10 | `XrplPaymentMonitor` and `PaymentMonitorHealth` are internal |
| Public test fakes exposing internal types — CS0050/CS0053 | Task 6 | Both fakes are internal |
| **Cursor advanced past an unproven range after `HistoryGap`**, turning a visible gap into a permanent invisible one, and `Streaming` overwrote the `HistoryGap` state | Task 9 | `_unprovenFromLedger` freezes the cursor and the state until a catch-up proves the range; new test `AFrozenCursorStaysFrozenEvenAsLedgersKeepClosing` |
| Fresh-store cursor never reached the store: `_persistedCursor` was set first, so `PersistCursorAsync`'s guard swallowed the write | Task 9 | Direct store write; the fresh-store test now asserts on the store |
| `response.TxJson?.Hash` — `TxJson` is `object`, CS1061 | Task 13 | `response.Transaction?.Hash` |
| `GetXrpFreeBalance` unresolved — it is an extension in `Xrpl.Sugar` | Task 13 | `using Xrpl.Sugar;` |
| `TransactionStream` unresolved in the real connection | Task 11 | `using Xrpl.Models.Subscriptions;` |
| Test project could not compile `ServiceCollection` / `Host.CreateApplicationBuilder` | Task 1 | Added `Microsoft.Extensions.Hosting` |
| `Assert.Equal(42u, TagInFixture())` was a tautology; the tag-to-buyer link was never exercised at monitor level | Task 9 | Harness takes `firstDestinationTag`; the test asserts the resolved `BuyerId` |
| Network-stall verdict polled one peer, not the pool, so two stuck nodes could outvote a healthy third | Task 9 | Full pool cycle |
| `StoreUnavailable` never cleared after the store recovered | Task 9 | Previous state restored on recovery |
| `FirstDestinationTag` was validated but never consumed by anything | Task 12 | The sample passes it to the store and the docs explain that tag allocation belongs to the store |
| Counter threaded through `Action<int>`/`Func<int>` for no reason | Task 10 | `RecoverAsync` returns `bool` |
| **`dotnet test` does not work at all here**: xunit.v3 4.0.0 runs on Microsoft.Testing.Platform, and the .NET 10 SDK's `dotnet test` still routes through VSTest | Task 1 and every task's run step | Test project is an executable run via `dotnet run --project`, with xunit's own `-class` / `-trait` / `-trait-` filters |
| `Channel.CreateBounded` ambiguous between `System.Threading.Channels` and `Xrpl.Models.Methods` — CS0104 | Task 9 | `using Channel = System.Threading.Channels.Channel;` |

Verified by compilation across `net8.0`, `net9.0` and `net10.0` before this plan was finalised: the balance-change
call, the `Currency` amount semantics, `IPayment.DestinationTag`, `IAccountTransaction` covering both the
stream and history types, the `account_tx` request and response shapes, the `server_info` fields, and
`Task.Delay` with a `TimeProvider`.

Two known gaps left open on purpose: there is no monitor-level test for "store outage pauses without moving
the cursor" (only the `StoreRetryPolicy` unit tests), and the integration test for catch-up simulates a
missed window by pinning the cursor rather than physically severing the socket.

