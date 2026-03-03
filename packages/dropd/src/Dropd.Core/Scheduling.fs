namespace Dropd.Core

open System
open Dropd.Core.Types

module Scheduling =

    type SchedulingDecision =
        | StartSync
        | SkipSync of SyncSkipReason
        | WaitForNextWindow

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
