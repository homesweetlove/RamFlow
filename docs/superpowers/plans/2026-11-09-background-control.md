# Background Control Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Implement explicit background priority control, memory-aware job admission, and UI-to-tray process handoff; rerun actual controlled workloads.
**Architecture:** Retain Engine ownership/journaling and official Windows setters. Separate priority eligibility from conservative trim eligibility. A small tray owns residency while WPF is disposable; a bounded engine-owned queue starts explicitly submitted processes.
**Tech Stack:** C#/.NET 10, WPF, WinForms, Windows named pipes, Python observer.
**Spec:** `docs/superpowers/specs/2026-11-09-background-control-design.md`

## Global Constraints
- Preserve prior changes/results; no push, installation, personal-app termination or system memory configuration changes.
- Mutations in benchmarks restricted to created PIDs and identities. No accelerated clock in efficacy runs.
- Exact limits: background paths <=256; concurrency 1–16; reserve default1024 MiB; projected Commit <=85%; admission requires reserve + declared estimate.

## Review Focus
- Busy selected target vs same-name foreground and explicit whitelist: never weaken hard protections.
- Settings array aliasing, invalid executable paths and stale UI settings: validate/copy atomically.
- Queue pressure, child exit, cancellation and shutdown: no excess launch or killing running work.
- IPC privilege: nonadmin cannot enqueue commands into an elevated engine.
- UI handoff failure, reopen and pending restores: retain access and ownership without double engine shutdown.

### Task 1: Explicit background priority policy
Files: Core Contracts/Policy/Engine; Tests BackgroundControlTests; UI settings.
Interfaces: Settings.BackgroundApps full paths; Policy.Protect(..., bool explicitBackground=false); Engine retains original journal.
- [x] Write/run RED tests: busy designated worker is adjusted immediately under demand; normal unselected busy worker untouched; foreground/system/unknown/whitelist protection; restore under relief/removal/foreground; no new busy trims; invalid paths and array isolation.
- [x] Implement paths/validation, priority-only bypass, fresh foreground recheck and UI settings.
- [x] Run entire native suite GREEN, including Windows/IPC.

### Task 2: Memory-aware process queue
Files: Core BackgroundJobs.cs, Engine/EnginePipe/Contracts; UI BackgroundJobsPage; Tests BackgroundJobsTests.
Interfaces: BackgroundJobRequest, BackgroundJobSummary; Engine.EnqueueBackgroundJob/CancelBackgroundJob; State.BackgroundJobs; Settings.BackgroundConcurrency/BackgroundReserveMiB.
- [x] RED tests: concurrency, headroom/Commit gate, Dry/pause, waiting cancellation, failed start, running shutdown, validation and IPC denial.
- [x] Implement bounded job admission and UI input using executable picker + JSON argument array.
- [x] Run native and owned-child Windows integration GREEN.

### Task 3: Disposable WPF and small tray
Files: new RamFlow.Tray project; Core UiHandoff; UI App/Program/MainWindow/WindowActivation; service Program; build/verification scripts.
Interfaces: Engine.IsRemote/Detach; readiness/dismiss events scoped by user and verification namespace; Window.CloseToTray callback.
- [x] RED lifecycle tests: detached remote proxy does not stop owner; local owner cannot detach; cold engine is Dry, reopening preserves mode; integration expects WPF exit with same engine.
- [x] Implement readiness-confirmed handoff, lightweight tray open/pause/simulation/exit, isolated command-line context.
- [x] Build/package and run actual handoff, smoke/render/start/install checks GREEN.

### Task 4: Controlled efficacy and footprint study
Files: isolated benchmark harness and immutable run folders; docs and compact evidence.
- [x] Preserve prior Core binaries and record SHA-256 before building replacement.
- [x] Run 5 randomized paired blocks using real clocks, foreground window, correctness hashes, owned-only native mutations and fixed workload per block. Report latency and background progress; CPU/mixed load scope explicit.
- [x] Measure UI/tray private memory and verify all test processes exit; retain all negative outcomes.
- [x] Update validation/case study only with measured claims; diff/link/source-integrity checks.
