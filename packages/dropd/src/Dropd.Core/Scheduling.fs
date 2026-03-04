namespace Dropd.Core

open System
open Dropd.Core.Types

module Scheduling =

    type SchedulingDecision =
        | StartSync
        | SkipSync of SyncSkipReason
        | WaitForNextWindow

    module AC = ApiContracts

    let decide
        (nowUtc: DateTimeOffset)
        (syncTimeUtc: TimeOnly)
        (isSyncRunning: bool)
        (lastSyncDateUtc: DateOnly option)
        : SchedulingDecision =
        let nowDate = DateOnly.FromDateTime(nowUtc.UtcDateTime)
        let nowTime = TimeOnly.FromDateTime(nowUtc.UtcDateTime)

        if nowTime <> syncTimeUtc then
            WaitForNextWindow
        elif lastSyncDateUtc = Some nowDate then
            WaitForNextWindow
        elif isSyncRunning then
            SkipSync AlreadyRunning
        else
            StartSync

    /// Detect missed sync windows at service startup.
    /// Returns `SkipSync MissedWhileUnavailable` when the current time is past
    /// the configured sync time and no sync ran today. Otherwise returns
    /// `WaitForNextWindow`.
    let decideOnStartup
        (nowUtc: DateTimeOffset)
        (syncTimeUtc: TimeOnly)
        (lastSyncDateUtc: DateOnly option)
        : SchedulingDecision =
        let nowDate = DateOnly.FromDateTime(nowUtc.UtcDateTime)
        let nowTime = TimeOnly.FromDateTime(nowUtc.UtcDateTime)

        if nowTime > syncTimeUtc && lastSyncDateUtc <> Some nowDate then
            SkipSync MissedWhileUnavailable
        else
            WaitForNextWindow

    /// Convert a scheduling decision into an optional log entry for
    /// observability. Only `SkipSync` decisions produce log entries.
    let logDecision (decision: SchedulingDecision) : AC.LogEntry option =
        match decision with
        | StartSync
        | WaitForNextWindow -> None
        | SkipSync reason ->
            let reasonCode, message =
                match reason with
                | AlreadyRunning ->
                    "SyncSkippedOverlap", "Scheduled sync skipped because a sync is already in progress."
                | MissedWhileUnavailable ->
                    "SyncSkippedMissed", "Scheduled sync skipped because service was unavailable during the sync window."

            Some
                { Level = AC.Warning
                  Code = reasonCode
                  Message = message
                  Data = Map.empty }
