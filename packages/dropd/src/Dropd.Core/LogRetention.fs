namespace Dropd.Core

open System
open System.IO

module LogRetention =

    /// Delete log files in `logDirectory` whose last-write time is older
    /// than `retentionDays` relative to `now`. Returns the count of deleted
    /// files.
    let prune (logDirectory: string) (retentionDays: int) (now: DateTimeOffset) : int =
        if not (Directory.Exists logDirectory) then
            0
        else
            let cutoff = now.AddDays(- float retentionDays)
            let mutable deleted = 0

            for file in Directory.EnumerateFiles(logDirectory, "*.log") do
                let lastWrite = File.GetLastWriteTimeUtc(file) |> DateTimeOffset
                if lastWrite < cutoff then
                    File.Delete(file)
                    deleted <- deleted + 1

            deleted
