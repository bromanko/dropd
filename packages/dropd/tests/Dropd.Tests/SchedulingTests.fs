module Dropd.Tests.SchedulingTests

open System
open Expecto
open Dropd.Core.Types
open Dropd.Core.Config
open Dropd.Core.Scheduling

[<Tests>]
let tests =
    testList
        "Scheduling"
        [

          // DD-055: dropd shall execute a sync automatically once per day.
          // Setup: Observe scheduling decisions over a simulated 48-hour period.
          // Assert: Exactly two StartSync decisions are produced.
          testCase "DD-055 executes sync once per day" <| fun _ ->
              let start = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
              let syncTime = TimeOnly(4, 0)
              let mutable lastSyncDateUtc = None

              let decisions =
                  [ for hour in 0 .. 47 do
                        let nowUtc = start.AddHours(float hour)
                        let decision = decide nowUtc syncTime false lastSyncDateUtc

                        match decision with
                        | StartSync -> lastSyncDateUtc <- Some(DateOnly.FromDateTime(nowUtc.UtcDateTime))
                        | _ -> ()

                        yield decision ]

              let starts = decisions |> List.filter ((=) StartSync) |> List.length
              Expect.equal starts 2 "should start exactly once each day"

          // DD-056: dropd shall store a configurable time of day at which the daily sync executes.
          // Setup: Create SyncConfig with SyncTimeUtc = TimeOnly(14, 30).
          // Assert: config.SyncTimeUtc = TimeOnly(14, 30).
          testCase "DD-056 stores configurable sync time" <| fun _ ->
              let custom =
                  { defaults with
                      SyncTimeUtc = TimeOnly(14, 30) }

              Expect.equal custom.SyncTimeUtc (TimeOnly(14, 30)) "sync time should be stored"

          // DD-057: When the configured sync time is reached, dropd shall initiate a sync.
          // Setup: Set sync time to T. Call Scheduling.decide with nowUtc = T.
          // Assert: Decision is StartSync.
          testCase "DD-057 initiates sync at configured time" <| fun _ ->
              let nowUtc = DateTimeOffset(2026, 1, 1, 4, 0, 0, TimeSpan.Zero)
              let decision = decide nowUtc (TimeOnly(4, 0)) false None
              Expect.equal decision StartSync "should start when time is reached"

          // DD-058: When the configured sync time is reached while a sync is already in progress,
          // dropd shall skip starting a second sync.
          // Setup: Call Scheduling.decide with isSyncRunning = true at sync time.
          // Assert: Decision is SkipSync AlreadyRunning.
          testCase "DD-058 skips sync when already running" <| fun _ ->
              let nowUtc = DateTimeOffset(2026, 1, 1, 4, 0, 0, TimeSpan.Zero)

              match decide nowUtc (TimeOnly(4, 0)) true None with
              | SkipSync AlreadyRunning -> ()
              | other -> failtestf "expected SkipSync AlreadyRunning, got %A" other

          // DD-059: When dropd starts after being unavailable during a scheduled sync time,
          // dropd shall wait until the next configured sync time.
          // Setup: Call Scheduling.decide with nowUtc past sync time, lastSyncDateUtc = yesterday.
          //        (Service was down during today's window.)
          // Assert: Decision is WaitForNextWindow (not StartSync catch-up).
          testCase "DD-059 waits for next window after missed schedule" <| fun _ ->
              let nowUtc = DateTimeOffset(2026, 1, 2, 7, 30, 0, TimeSpan.Zero)
              let yesterday = DateOnly(2026, 1, 1)
              let decision = decide nowUtc (TimeOnly(4, 0)) false (Some yesterday)
              Expect.equal decision WaitForNextWindow "should not perform catch-up sync"

          ]
