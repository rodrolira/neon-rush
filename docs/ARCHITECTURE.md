# Neon Rush — Architecture

**Studio:** MoonCat Studio
**Engine:** Unity 6 (6000.5.2f1 LTS) · URP
**Language:** C# (.NET Standard 2.1)

This document is the contract. Every agent, skill, and pull request in this repository is
bound by it. If code and this document disagree, one of them is a bug.

---

## 1. Guiding constraints

Four constraints drive every decision below. They are listed in priority order — when they
conflict, the higher one wins.

1. **The game must run at 60 FPS on a low-end Android device.** This is the binding
   constraint. It kills allocation-per-frame, it kills `Update()` on thousands of objects,
   it kills `Instantiate` during a run, and it forces pooling and Burst-friendly data layouts.
2. **Live values must be changeable without shipping a build.** Store prices, ad frequency,
   drop rates, difficulty curves, and season content are *data*, delivered by Remote Config
   and Addressables. Any hardcoded balance number is a defect.
3. **The client is untrusted.** Anything a player can edit in memory is advisory only.
   Currency and purchase state are authoritative on the server.
4. **The game must be testable without an SDK, a device, or a network.** CI runs on a Linux
   container with no Google Play, no Firebase, and no GPU. If a system cannot be tested
   there, it is wrongly coupled.

---

## 2. Layering

Dependencies point **inward only**. An inner layer may never reference an outer one.

```
┌──────────────────────────────────────────────────────────────┐
│  Presentation      UI, VFX, Audio, Scene composition         │  ← MonoBehaviours live here
├──────────────────────────────────────────────────────────────┤
│  Application       Game loop, state machine, use-cases,      │
│                    mission tracking, run scoring             │
├──────────────────────────────────────────────────────────────┤
│  Domain            Entities + rules: Wallet, Economy, Player │  ← Pure C#. No UnityEngine.
│                    Progression, Mission, Offer, Season       │     No I/O. Fully unit-testable.
├──────────────────────────────────────────────────────────────┤
│  Infrastructure    Firebase, AdMob, IAP, Persistence,        │  ← Implements Domain ports.
│                    Analytics, RemoteConfig, Clock            │     Swappable. Mockable.
└──────────────────────────────────────────────────────────────┘
```

**The Domain layer must not contain `using UnityEngine;`.** This is enforced by its assembly
definition, which does not reference Unity's engine assembly at all. That is not stylistic
purity — it is what lets the entire economy, progression, and mission rule set run in a plain
NUnit process in under a second, with no Editor and no device.

### Ports and adapters

The Domain declares *interfaces* (ports) for everything it needs from the outside world. The
Infrastructure layer provides *implementations* (adapters). Nothing else may talk to an SDK.

| Port (Domain)          | Production adapter        | Test / CI adapter    | Why the port exists                                    |
| ---------------------- | ------------------------- | -------------------- | ------------------------------------------------------ |
| `IAdsService`          | `AdMobAdsService`         | `NullAdsService`     | AdMob is a binary SDK; CI has none.                    |
| `IIapService`          | `UnityIapService`         | `FakeIapService`     | Billing requires a signed build + store account.       |
| `IRemoteConfigService` | `FirebaseRemoteConfig`    | `LocalRemoteConfig`  | Designers need offline defaults that match production. |
| `IAnalyticsService`    | `FirebaseAnalytics`       | `RecordingAnalytics` | Tests assert *which events fired*, not that they sent. |
| `IAuthService`         | `FirebaseAuthService`     | `AnonymousAuth`      | Auth needs network; the game must boot offline.        |
| `ICloudSave`           | `FirestoreCloudSave`      | `InMemoryCloudSave`  | Conflict-resolution logic must be unit-testable.       |
| `ICrashReporter`       | `CrashlyticsReporter`     | `ConsoleReporter`    |                                                        |
| `IClock`               | `SystemClock`             | `FakeClock`          | **Critical.** See §6 — time is an attack surface.      |

> **On "no placeholder code".** `NullAdsService` is not a placeholder or a stub-to-be-filled.
> It is a real, shipping implementation of the Null Object pattern: it is what runs for a
> subscriber who has paid to remove ads, and it is what runs in CI. Both adapters are
> production code. The design goal is that *deleting the Firebase SDK from the project
> should not break a single Domain test.*

---

## 3. Assembly definitions

Assemblies are the mechanism that makes the layering above *enforceable by the compiler*
rather than aspirational. A reference that violates the layering will not compile.

| Assembly                     | References                         | Platform  |
| ---------------------------- | ---------------------------------- | --------- |
| `NeonRush.Domain`            | *(nothing — not even UnityEngine)* | Any       |
| `NeonRush.Application`       | Domain                             | Any       |
| `NeonRush.Infrastructure`    | Domain, Application                | Any       |
| `NeonRush.Presentation`      | Domain, Application                | Any       |
| `NeonRush.Composition`       | all of the above                   | Any       |
| `NeonRush.Editor`            | all + Editor                       | Editor    |
| `NeonRush.Tests.EditMode`    | all + `nunit`                      | Editor    |
| `NeonRush.Tests.PlayMode`    | all + `nunit`                      | Editor    |

`NeonRush.Composition` is the only assembly permitted to know every concrete type — it is the
composition root where the object graph is wired. Keeping it isolated means the rest of the
codebase cannot secretly depend on a concrete adapter.

---

## 4. Dependency injection

A hand-rolled, ~200-line constructor-injection container (`Core/DI`). Deliberately **not**
Zenject/VContainer:

- Reflection-heavy containers cost real milliseconds at boot on low-end Android.
- We need exactly three lifetimes (singleton, transient, scoped-to-run). That is a weekend
  of code, not a dependency.
- A container we own can be made **AOT/IL2CPP-safe by construction**, which is where
  third-party reflection containers tend to bite on iOS.

Composition happens once, at boot, in `GameBootstrap`. There is **no Service Locator** in
gameplay code. `ServiceLocator` exists only as a bridge for `MonoBehaviour`s, which Unity
constructs itself and into which we therefore cannot inject via constructor — that bridge is
the single justified exception the brief allows, and it is confined to `Presentation`.

---

## 5. Communication: the event bus

Systems do not hold references to each other. `CoinCollected` is published by gameplay and
consumed independently by the wallet, the mission tracker, the analytics reporter, the audio
system, and the HUD. None of them know the others exist.

- Events are **immutable `readonly record struct`** — zero heap allocation, which matters
  because coin pickups fire dozens of times per second.
- Subscriptions are explicit and **must be disposed**. A leaked subscription to a run-scoped
  system is the classic Unity memory leak; `IDisposable` handles + a scope that disposes them
  in bulk make it hard to get wrong.
- The bus is synchronous. Async event delivery makes ordering non-deterministic, and
  non-deterministic ordering makes the anti-cheat and mission systems untestable.

---

## 6. Time is an attack surface

Every deadline in this game — daily rewards, streaks, season end, flash-sale countdowns,
energy refill, offer expiry — is a place a player can gain value by changing the device clock.

Therefore: **no gameplay or economy code may call `DateTime.Now`, `Time.time`, or
`Environment.TickCount` directly.** All time flows through `IClock`, which:

- prefers **server time** (Firestore/FCM timestamp, fetched at boot and drift-corrected),
- falls back to a **monotonic** device clock (`Stopwatch`) for elapsed-time measurement,
- and records the delta between device wall-clock and server time. A large negative jump is
  reported to analytics as a tamper signal and freezes time-gated rewards until resync.

Injecting `FakeClock` in tests is how "does the streak reset correctly at the season
boundary in UTC−11?" becomes a one-second unit test instead of a QA ticket.

---

## 7. Runtime performance rules

These are not suggestions; they are review-blocking.

- **Zero allocations per frame during a run.** Verified by a PlayMode test asserting
  `GC.GetTotalMemory` is flat across 600 simulated frames.
- **No `Instantiate`/`Destroy` during a run.** Chunks, obstacles, coins, VFX and audio all
  come from pre-warmed pools. Pool exhaustion grows the pool *between* runs, never during.
- **No `GameObject.Find`, `SendMessage`, or `Camera.main` in a hot path.** `Camera.main` is a
  tagged search; it is cached at boot.
- **One `Update()` per system, not per object.** 300 obstacles are ticked by one manager
  iterating a contiguous array, not by 300 `MonoBehaviour.Update` callbacks — Unity's
  managed→native call per behaviour is the measurable cost here.
- **Addressables for anything seasonal or cosmetic**, so a Halloween drop is a download and
  not a store submission.

---

## 8. Trust boundary

```
        CLIENT (untrusted)                    SERVER (authoritative)
  ┌───────────────────────────┐          ┌──────────────────────────────┐
  │ Run simulation            │          │ Firestore security rules     │
  │ Coin pickups              │          │  · wallet writes rejected    │
  │ Predicted wallet balance  │  ──────► │    unless from Cloud Function│
  │ Obscured in-memory values │          │ Cloud Functions              │
  │ Local run signature       │          │  · validate run plausibility │
  └───────────────────────────┘          │  · verify IAP receipts       │
                                         │  · grant currency            │
                                         └──────────────────────────────┘
```

The client's wallet is a **prediction**, shown immediately so the game feels responsive. The
server's balance is the **truth**. On divergence, the server wins and the client reconciles.

Client-side defences (`ObscuredInt`, run-plausibility checks, speed-hack detection) exist to
raise the cost of casual cheating and to *generate telemetry*, not to be unbreakable. Any
design that treats a client-side check as authoritative is rejected in review.

**Receipt validation is server-side, always.** A client that grants gems because a local IAP
callback said "success" will be drained by every receipt-replay tool on the internet.

---

## 9. Data-driven content

| Content                                    | Mechanism                        | Ships with build? |
| ------------------------------------------ | -------------------------------- | ----------------- |
| Tuning constants (speed curve, spawn rates) | `ScriptableObject` + Remote Config override | Yes (as default)  |
| Shop prices, offers, discounts              | Remote Config                    | Defaults only     |
| Ad frequency, cooldowns                     | Remote Config                    | Defaults only     |
| Missions, season pass tracks                | Remote Config + Addressables     | Defaults only     |
| Characters, skins, boards (art)             | Addressables                     | Core set only     |

Every Remote Config key has a **compiled-in default** that produces a shippable, balanced
game. A player who launches offline on day one must get a complete experience — Remote Config
tunes the game, it does not constitute it.

---

## 10. Folder structure

```
Assets/_Project/
  Scripts/
    Domain/            Pure C#. Entities, value objects, rules, port interfaces.
      Economy/         Wallet, Currency, Price, Transaction
      Progression/     PlayerLevel, XP, Prestige
      Missions/        Mission, Objective, Progress
      Monetization/    Offer, Bundle, SeasonPass, Subscription
      Inventory/       Item, Loadout, Ownership
      Ports/           IAdsService, IIapService, IClock, ...
    Application/       Use-cases + orchestration. State machine. Run scoring.
    Infrastructure/    Adapters: Firebase/, Ads/, Iap/, Save/, Analytics/, Time/
    Presentation/      MonoBehaviours: Player/, Obstacles/, UI/, VFX/, Audio/
    Core/              DI, EventBus, Pooling, Logging, Utilities
    Composition/       GameBootstrap — the composition root
  Data/                ScriptableObject assets (tuning, catalogues)
  Art/ Audio/ Prefabs/ Scenes/
  Editor/              Tooling, validators, build scripts
  Tests/
    EditMode/          Domain + Application. Fast. No engine.
    PlayMode/          Pooling, performance, integration.
```

---

## 11. What is deliberately NOT in v1

Naming these prevents scope creep from being mistaken for progress:

- **Multiplayer / real-time races.** The brief's leaderboards are asynchronous.
- **Energy system.** The brief marks it optional. It suppresses session count and, for an
  endless runner monetised on ads and cosmetics, it costs more retention than it earns.
  Revisit only with data.
- **Player-to-player trading.** An economy exploit surface with no revenue upside.
