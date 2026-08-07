// The precondition every EXCLUSIVE operation checks once, at the top, before touching anything.
//
// Exclusive = an operation that edits the LaunchBox XMLs directly (or deletes whole files) rather
// than going through the op-log: node deletion today; the ROM scan / mass import tomorrow. The
// journal is safe only for ops aimed at identity that already exists and is shared with LaunchBox;
// an exclusive op mints identity or decides from a snapshot of the disk, so it needs the field:
//   • not read-only (the UI should be greyed anyway — this is the mechanism, not the convenience);
//   • LaunchBox/BigBox closed (they own the files and rewrite them wholesale at exit);
//   • a HEALTHY journal (a faulted one has lost writes — the drain below can't be trusted);
//   • the journal DRAINED (pending ops were computed against the files as they were; flushing
//     them first means the exclusive op starts from the real, current state).
//
// Callers show `why` to the user on refusal — an exclusive op that silently does nothing reads
// as data loss.

#nullable enable

namespace LbApiHost.Host.Data;

internal static class ExclusiveGate
{
    /// <summary>True when the exclusive operation may proceed. On false, <paramref name="why"/>
    /// carries the user-facing reason. Drains the journal as a side effect when it can.</summary>
    public static bool CanRun(GameStore? store, out string why)
    {
        if (store == null) { why = "The library is not loaded."; return false; }
        if (store.ReadOnly)
        { why = "LiteBox is in read-only mode — nothing is written to the LaunchBox files."; return false; }
        if (GameStore.IsLaunchBoxRunning())
        { why = "LaunchBox / BigBox is running — it owns the XML files. Close it first."; return false; }
        if (store.JournalFaulted)
        { why = "The change journal is faulted (" + (store.JournalFaultReason ?? "unknown") + ") — the state of pending edits is unknown."; return false; }
        store.Flush();                       // drain pending edits onto disk first
        if (store.PendingCount > 0)
        { why = "Pending edits could not be written to the XML files — resolve that before this operation."; return false; }
        why = "";
        return true;
    }
}
