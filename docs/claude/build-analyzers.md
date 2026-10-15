# Build-time analyzers

Where a build's time goes, which SDK analyzers are off for speed, and what stands in for them.

## Where the time goes

Measured 2026-10-15 on the 16-core dev container with `dotnet build -p:ReportAnalyzer=true -p:UseSharedCompilation=false --no-incremental`, summed across every project of the solution (analyzer CPU seconds; the table is per analyzer, and the library is most of every row):

| Analyzer | Debug | What it checks |
|---|---|---|
| ILLink `DynamicallyAccessedMembersAnalyzer` (IL2xxx / IL3xxx) | 269 | trim safety, from `IsTrimmable` |
| IDE0370 | 171 | unnecessary `!` |
| CA2000 | 112 | disposable not disposed |
| CA1508 | 88 | dead conditional code |
| IDE0055 | 53 | formatting |
| CA5403 | 49 | hard-coded certificate |
| IDE0005 | 35 | unnecessary `using` |
| IDE0058 / 59 / 60 | 31 | unused values and parameters |
| CA5390 | 17 | hard-coded encryption key |
| everything else | under 15 each | |

Of about 990 analyzer CPU seconds in all, the four dataflow-based CA rules (CA2000, CA1508, CA5403, CA5390) share one cost: the points-to and value-content analyses they build per method are cached between them, so disabling some moves the cost onto the rest.
With CA2000, CA5403 and CA5390 off, CA1508 alone was charged what the four had been, and turning it off as well removed nearly half of the remaining Debug build's CPU.
The per-analyzer attribution is therefore only a guide; the totals below are the measure.

## What changed

- **Trim analysis runs only in Release** (`EnableTrimAnalyzer` in the root `Directory.Build.props`).
  CI's Release leg enforces it, and no trim warning has been seen to fail a build, so a local Debug build skips it; `dotnet build -c Release`, or `-p:EnableTrimAnalyzer=true` on any build, runs it on demand.
  The package ships the Release build, so Release is also the configuration whose trim safety matters; Debug's extra `#if DEBUG` code goes unchecked.
- **IDE0370 is off; SSS017 stands in.**
  IDE0370 proves a `!` unnecessary by rebinding the code around it without the suppression.
  The semantic model can't do it cheaply: it reports an expression's flow state *after* its `!`, so the operand of `s!` always reads not-null.
  SSS017 judges from declarations instead — a non-nullable parameter, local, field, property or method result, a value type, `this`, `new`, a string — and costs one symbol lookup per `!` (well under a second for the library).
  What IDE0370 catches and SSS017 doesn't: a `!` on a nullable-declared value that flow analysis has already proven not-null (a `var` local after a null check), and anything of generic, array or tuple type, where the `!` may be silencing a nested-nullability mismatch.
  SSS017 runs in the two projects that reference the repo's analyzers (the library and the EF Core adapter); test projects have no unnecessary-`!` check.
- **CA2000 is off; SSS018 stands in.**
  CA2000 tracks every disposable through the method's dataflow and one level into callees.
  SSS018 catches the plainly visible leak: a `new` of a disposable class held in a local (outside `using`) and only ever used through its members, or discarded as it's created.
  Anything that could hand the object on — return, assignment, argument, capture, `?.` — counts as a transfer, so a leak on one branch or through a helper goes unreported, and only `new` counts as creating an object, since a method's result may be borrowed (a `Volatile.Read` of a field was the case that showed it).
  The existing CA2000 pragmas went with the rule; their rationales stay as comments.
- **CA5390 and CA5403 are off without a stand-in.**
  They look for hard-coded keys and certificates; the simulator holds no secrets of its own — every key and certificate it handles comes from the SQL it runs — so neither has a target here.
- **CA1508 is off without a stand-in.**
  With CA2000, CA5403 and CA5390 gone it carried the whole shared dataflow cost, nearly half of a Debug build's CPU, and no cheap rule approximates dead-condition detection; its one site in the repo was a suppressed false positive.

## Not changed

- **IDE0005** and **IDE0055** stay.
  IDE0005 is charged much of the semantic binding every analyzer shares, so its row overstates what it alone costs.
  IDE0055 is the formatter CI's whitespace job also runs, but locally it is the only formatting gate.

## Totals

Full-solution `dotnet build --no-incremental` with the compiler server off, measured 2026-10-15 on the 16-core dev container, medians of three or four runs alternating before and after on a machine other work was also loading (so wall times scatter by tens of seconds; the CPU column, the whole process tree's user + system time, is the steadier measure and the closer guide to a 4-core CI runner, where a build is CPU-bound):

| Build | Before wall | After wall | Before CPU | After CPU |
|---|---|---|---|---|
| Debug | 112 s | 56 s | 987 s | 289 s |
| Release | 105 s | 70 s | 960 s | 559 s |

Release keeps the trim analyzer, about 270 CPU seconds of what it has left.
The IDE and CA rules see the same source in both configurations except `#if DEBUG` code, which Release lacks, so a Release build that also skipped them (`-p:EnforceCodeStyleInBuild=false -p:EnableNETAnalyzers=false`) measured 48 s wall and 324 s CPU, leaving each analyzer to run in exactly one CI leg; that arrangement is not adopted.
