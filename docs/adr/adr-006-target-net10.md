# ADR-006: Target .NET 10

## Status

Accepted

## Context

Every project targeted `net8.0`. .NET 8 is an LTS release in its maintenance phase, and its
support ends on **10 November 2026**. After that date it receives no security patches.

That matters more here than in an average project. The platform's stated purpose is a
reference architecture with CRA and NIS2 compliance designed in from the message schema
upward, and CRA Annex I Part II is precisely about handling vulnerabilities and shipping
security updates. Running the reference implementation on an unsupported runtime would
contradict the argument the repository makes.

Two facts shaped the timing:

- **The codebase is small.** Nine source and test files, roughly 214 lines. A retarget is
  a target-framework change plus package updates. After the Function implementation lands
  it would be the same change across a much larger surface, with more to re-test.
- **Hosting and package decisions depend on it.** Azure Functions 4.x supports .NET 10 in
  the isolated worker model, but requires `Microsoft.Azure.Functions.Worker` 2.50.0 or
  later, and .NET 10 cannot run on the Linux Consumption plan. Choosing a hosting plan or
  writing code against the old worker packages first would mean deciding twice.

## Options

1. **Stay on `net8.0`.** No work now, unsupported within weeks, and at odds with the
   compliance story.
2. **Move to `net10.0` now.** Small, and unblocks the hosting and package decisions.
3. **Move after the Function implementation.** Same change, larger surface, re-test.

## Decision

**Target `net10.0` now.**

- `TargetFramework` set once in `Directory.Build.props`
- `Microsoft.Azure.Functions.Worker` updated to 2.52.0, extensions to current versions
- The project SDK moves from `Microsoft.NET.Sdk` plus the
  `Microsoft.Azure.Functions.Worker.Sdk` package to **`Azure.Functions.Sdk/1.0.1`**. Per
  Microsoft, this migration requires no changes to application code, `Program.cs`,
  function classes or `host.json`
- `OutputType` and `AzureFunctionsVersion` removed — the new SDK sets both
- Test tooling updated: xUnit, the test SDK and coverlet
- CI no longer installs a separate .NET 8 runtime; `global.json` remains the single pin

## Consequences

- **Hosting must be Flex Consumption.** .NET 10 is not supported on the Linux Consumption
  plan, and the Consumption plan is in any case now the legacy option. This settles a
  question that was open for the infrastructure work.
- **`Directory.Solution.targets` is required.** `Azure.Functions.Sdk` generates its
  extensions project from a post-restore hook that only runs when the Functions project is
  restored *directly*. Restoring the solution skips it, producing warning AZFW0108 and a
  fallback restore during build, which Microsoft warns "may cause issues in some build
  environments". The added target walks `ProjectReference` items so the hook runs during
  `dotnet restore`.
- **.NET 8 is no longer needed** anywhere: not on developer machines, not in CI.
- The runtime is supported until **November 2028**, past the project's horizon.
- Upgrading again is a comparable change. Doing it while the codebase is small is the
  cheapest this decision will ever be.

## References

- [ADR-001](adr-001-programming-language.md) — the original choice of C# and .NET, which
  this supersedes on version only
- Microsoft: .NET 8 end of support, 10 November 2026
- Microsoft: Azure Functions isolated worker guide — supported versions, and the
  `Azure.Functions.Sdk` migration
- Microsoft: AZFW0108 rule description and fix
