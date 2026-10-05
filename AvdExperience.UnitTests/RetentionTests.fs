module AvdStats.UnitTests.RetentionTests

// The retention invariant: folding the retained history (events after the cutoff)
// seeded with fold(prefix) is identical to folding the full history — same final
// state/locked/shadow/reason, and the same closed intervals for everything that
// closes after the cutoff. This is what makes 90-day pruning safe: the prune cuts
// at a whole same-timestamp batch and persists the exact machine state there.

open System
open Xunit
open FsCheck
open FsUnit.Xunit
open AvdStats.UnitTests.TestHelpers
open AvdStats.EventLog
open AvdStats.Stats

// ── Event generation ─────────────────────────────────────────────────────────

/// Canonical-order event list: strictly increasing timestamps, 5 min apart, mixed
/// kinds like PropertyTests — every index is a batch boundary.
let private eventsFromKindIndices (baseTime: DateTimeOffset) (kindIndices: int list) =
    kindIndices
    |> List.truncate 30
    |> List.mapi (fun i kindIdx ->
        let t = baseTime.AddMinutes(float ((i + 1) * 5))
        match abs kindIdx % 7 with
        | 0 -> rdpEventAt 1024 t
        | 1 -> rdpEventAt 1027 t
        | 2 -> rdpEventAt 1026 t
        | 3 -> makeEventAt 1026 "" ["x"; "1"] t
        | 4 -> makeEventAt 4800 "Microsoft-Windows-Security-Auditing" [] t
        | 5 -> makeEventAt 4801 "Microsoft-Windows-Security-Auditing" [] t
        | _ -> makeEventAt 42 "Microsoft-Windows-Kernel-Power" [] t)

/// Fold everything after the cut index, seeded with fold(prefix) — the retention
/// fold. Returns (closed intervals + reasons, final state tuple).
let private foldRetained (all: LogEvent list) (cutIndex: int) =
    let prefix = all |> List.truncate cutIndex
    let retained = all |> List.skip cutIndex
    let seedState, seedLocked, seedShadow, seedReason = foldState emptySeed.State emptySeed.Locked emptySeed.Shadow emptySeed.Reason prefix
    let seed = { State = seedState; Locked = seedLocked; Shadow = seedShadow; Reason = seedReason }
    let closed, st, lk, sh, rs =
        foldHistory seed.State seed.Locked seed.Shadow seed.Reason retained
    closed, (st, lk, sh, rs), prefix

// ── Property: fold continuation ──────────────────────────────────────────────

[<Fact>]
let ``retained fold seeded at a batch boundary equals the full-history fold`` () =
    let baseTime = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    // pd in 2026, well after all events (only relevant for computeWithTrace, not foldHistory)
    let prop (kindIndices: int list) (cutIndexRaw: int) =
        let all = eventsFromKindIndices baseTime kindIndices
        let cutIndex = abs cutIndexRaw % (List.length all + 1)
        let closedRetained, finalRetained, prefix = foldRetained all cutIndex
        let closedFull, stFull, lkFull, shFull, rsFull = foldHistory emptySeed.State emptySeed.Locked emptySeed.Shadow emptySeed.Reason all
        // The seed must reproduce the final machine state exactly…
        let cutTime =
            match List.tryLast prefix with
            | Some e -> Some e.TimeCreated
            | None   -> None
        // …and every interval that closes after the cutoff, with identical
        // (kind, start, end, reason). Intervals closed at/before the cutoff are
        // intentionally gone (the CLI warns about windows that reach that far back).
        let survivorsFull =
            closedFull |> List.filter (fun (iv, _) ->
                match cutTime with Some t -> iv.End > t | None -> true)
        finalRetained = (stFull, lkFull, shFull, rsFull) && closedRetained = survivorsFull
    Check.QuickThrowOnFailure prop

// ── Same-timestamp batches ───────────────────────────────────────────────────

[<Fact>]
let ``prune cutoff after a complete same-timestamp batch keeps the fold exact`` () =
    let b = DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero)
    // Day 1: connect, lock at 12:00 — then at 12:05 two siblings share a timestamp
    // (canonical order: System rank 1 before RDP rank 2). Day 2: reconnect, disconnect.
    let t2 = b.AddHours 12.0
    let all =
        [ rdpEventAt 1024 (b.AddHours 9.0)
          rdpEventAt 1027 (b.AddHours 9.0 + TimeSpan.FromMinutes 1.0)
          makeEventAt 4800 "Microsoft-Windows-Security-Auditing" [] t2
          makeEventAt 42 "Microsoft-Windows-Kernel-Power" [] (t2.AddMinutes 5.0)   // same timestamp
          makeEventAt 1026 "" ["x"; "1"] (t2.AddMinutes 5.0)
          rdpEventAt 1024 (b.AddDays 1.0 + TimeSpan.FromHours 9.0)
          rdpEventAt 1027 (b.AddDays 1.0 + TimeSpan.FromHours 9.0 + TimeSpan.FromMinutes 1.0)
          makeEventAt 1026 "" ["x"; "1"] (b.AddDays 1.0 + TimeSpan.FromHours 17.0) ]
    // Cutoff after the two 12:05 siblings (batch boundary = first day-2 event).
    let cutIndex = 5
    let closedRetained, finalRetained, prefix = foldRetained all cutIndex
    let closedFull, stFull, lkFull, shFull, rsFull = foldHistory emptySeed.State emptySeed.Locked emptySeed.Shadow emptySeed.Reason all
    finalRetained |> should equal (stFull, lkFull, shFull, rsFull)
    let cutTime = (List.last prefix).TimeCreated
    closedRetained |> should equal (closedFull |> List.filter (fun (iv, _) -> iv.End > cutTime))

// ── The cutoff event choice: reason continuity across the cut ────────────────

[<Fact>]
let ``seed preserves PostPause for a reconnect after a short pause across the cutoff`` () =
    let b = DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero)
    // 09:00 initiate → 09:01 connected → 12:00 user disconnect (pause starts).
    // Cutoff at 13:00 (nothing stored between 12:00 and 14:30) → the 12:00
    // disconnect is pruned, its Paused state lives on in the seed.
    // 14:30 reconnect (pause was 2.5h < 3h) → 14:31 connected.
    let all =
        [ rdpEventAt 1024 (b.AddHours 9.0)
          rdpEventAt 1027 (b.AddHours 9.0 + TimeSpan.FromMinutes 1.0)
          makeEventAt 1026 "" ["x"; "1"] (b.AddHours 12.0)
          rdpEventAt 1024 (b.AddHours 14.5)
          rdpEventAt 1027 (b.AddHours 14.5 + TimeSpan.FromMinutes 1.0) ]
    let fullClosed, _, _, _, _ = foldHistory emptySeed.State emptySeed.Locked emptySeed.Shadow emptySeed.Reason all
    let fullReconnectReason = fullClosed |> List.find (fun (iv, _) -> iv.Kind = Connecting && iv.Start = b.AddHours 14.5) |> snd

    // Full history classifies the reconnect PostPause (short pause, not a fresh start).
    fullReconnectReason |> should equal (Some PostPause)

    // The seeded retained fold reproduces that classification…
    let closedRetained, finalRetained, prefix = foldRetained all 3   // keep the two 14:30 events
    closedRetained |> List.find (fun (iv, _) -> iv.Kind = Connecting) |> snd |> should equal (Some PostPause)

    // …whereas an unseeded (naive prune) fold misclassifies it as Initial and
    // would charge the 5-minute fresh-connect grace.
    let naiveClosed, _, _, _, _ = foldHistory emptySeed.State emptySeed.Locked emptySeed.Shadow emptySeed.Reason (all |> List.skip 3)
    naiveClosed |> List.find (fun (iv, _) -> iv.Kind = Connecting) |> snd |> should equal (Some Initial)

    // And the seeded fold matches the full fold on everything else too.
    let closedFull, stFull, lkFull, shFull, rsFull = foldHistory emptySeed.State emptySeed.Locked emptySeed.Shadow emptySeed.Reason all
    finalRetained |> should equal (stFull, lkFull, shFull, rsFull)
    closedRetained |> should equal (closedFull |> List.filter (fun (iv, _) -> iv.End > (List.last prefix).TimeCreated))

// ── Open interval spanning the cutoff ────────────────────────────────────────

[<Fact>]
let ``seed keeps an interval open at the cutoff with its true start`` () =
    let b = DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero)
    // Session 09:00–17:00, cutoff 13:00 falls mid-session (last stored event 09:01).
    let all =
        [ rdpEventAt 1024 (b.AddHours 9.0)
          rdpEventAt 1027 (b.AddHours 9.0 + TimeSpan.FromMinutes 1.0)
          makeEventAt 1026 "" ["x"; "1"] (b.AddHours 17.0) ]
    let cutIndex = 2   // the 17:00 disconnect survives; 09:00/09:01 are pruned
    let closedRetained, finalRetained, prefix = foldRetained all cutIndex
    let closedFull, stFull, lkFull, shFull, rsFull = foldHistory emptySeed.State emptySeed.Locked emptySeed.Shadow emptySeed.Reason all
    finalRetained |> should equal (stFull, lkFull, shFull, rsFull)
    // The Active interval opened pre-cutoff still closes at 17:00 with its TRUE
    // start (not the cutoff) — only intervals closed at/before the cutoff vanish.
    closedRetained |> should equal (closedFull |> List.filter (fun (iv, _) -> iv.End > (List.last prefix).TimeCreated))
