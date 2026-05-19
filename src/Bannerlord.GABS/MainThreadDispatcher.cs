using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;

using TaleWorlds.CampaignSystem;

namespace Bannerlord.GABS;

/// <summary>
/// Dispatches actions to the game's main thread.
/// GABP tool handlers run on background TCP threads, but Bannerlord game API
/// calls must execute on the main thread. Queue work here and it will be
/// processed during OnApplicationTick.
/// </summary>
public static class MainThreadDispatcher
{
    private static readonly ConcurrentQueue<Action> ExecutionQueue = new();

    /// <summary>
    /// Enqueue a fire-and-forget action on the main thread.
    /// </summary>
    public static void Enqueue(Action action)
    {
        ExecutionQueue.Enqueue(action);
    }

    /// <summary>
    /// Enqueue work on the main thread and return a Task that completes with the result.
    /// Use this from async tool handlers that need to read game state.
    /// </summary>
    public static Task<T> EnqueueAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        ExecutionQueue.Enqueue(() => RunWithTcs(func, tcs));
        return tcs.Task;
    }

    /// <summary>
    /// Enqueue a void action on the main thread and return a Task that completes when done.
    /// </summary>
    public static Task EnqueueAsync(Action action)
    {
        var tcs = new TaskCompletionSource<bool>();
        ExecutionQueue.Enqueue(() => RunWithTcs(action, tcs));
        return tcs.Task;
    }

    // We rely on a C# exception FILTER (`when` clause), not on
    // [DebuggerNonUserCode], to keep the throw frame alive for inspection.
    //
    // The previous approach — a normal try/catch in a method marked
    // [DebuggerNonUserCode] — assumed Rider's JMC would classify the catch
    // as non-user and therefore not suppress the "user-unhandled exception"
    // first-chance break. In practice Rider does NOT reliably honor the
    // attribute for catch-block classification: a catch anywhere in the
    // user-code call chain still makes the exception look user-handled, so
    // the first-chance break is skipped, the catch runs, the stack unwinds,
    // and by the time the debugger pauses (at the rethrow or higher) the
    // original throw frame's locals are gone — that's what produced
    // "Value is not accessible" in the Variables panel.
    //
    // `catch (Exception ex) when (CaptureToTcs(tcs, ex))` is different in a
    // crucial way: the `when` filter is evaluated during the CLR's
    // first-pass stack walk, BEFORE any unwinding happens. The filter is
    // called once per throw, gets the live exception, completes the TCS
    // for the awaiting HTTP handler, and then returns `false`. Returning
    // false tells the CLR "this handler does not match" — so the catch
    // body is never entered, the stack is never unwound here, and the
    // exception keeps propagating uncaught past this method. The
    // first-chance debugger break therefore fires at the actual TaleWorlds
    // throw site with all locals still in scope.
    //
    // The exception is then caught further up the stack by the top-level
    // safety net in ProcessQueue (also using a `when` filter), so the game
    // tick loop survives and the game does not hard-crash on a bug. That
    // safety-net catch is itself gated on `!Debugger.IsAttached`: when a
    // debugger is attached we want even ProcessQueue to let the exception
    // through, otherwise the main-thread first-pass walk would still find
    // a handler and the first-chance break would land on the worker thread
    // at ExceptionDispatchInfo.Throw() instead of at the real throw site.
    [DebuggerNonUserCode]
    private static void RunWithTcs<T>(Func<T> func, TaskCompletionSource<T> tcs)
    {
        try
        {
            tcs.SetResult(func());
        }
        catch (Exception ex) when (CaptureToTcs(tcs, ex))
        {
            // Unreachable: CaptureToTcs always returns false, so the CLR
            // never enters this catch body and the exception continues
            // propagating uncaught past this frame.
        }
    }

    [DebuggerNonUserCode]
    private static void RunWithTcs(Action action, TaskCompletionSource<bool> tcs)
    {
        try
        {
            action();
            tcs.SetResult(true);
        }
        catch (Exception ex) when (CaptureToTcs(tcs, ex))
        {
            // Unreachable — see RunWithTcs<T>.
        }
    }

    // Filter helper. Runs during the CLR first-pass walk — the throw frame is
    // still live when we enter this method. We complete the TCS so the
    // awaiting HTTP handler sees the failure, then return false to tell the
    // CLR our catch does not match.
    [DebuggerNonUserCode]
    private static bool CaptureToTcs<T>(TaskCompletionSource<T> tcs, Exception ex)
    {
        tcs.TrySetException(ex);
        return false;
    }

    private static volatile string? _pendingSaveName;

    /// <summary>
    /// Schedule a save to run on the main thread OUTSIDE the dispatcher queue.
    /// SaveAs blocks the main thread and deadlocks if called inside ProcessQueue.
    /// </summary>
    public static void ScheduleSave(string saveName)
    {
        _pendingSaveName = saveName;
    }

    /// <summary>
    /// Process all queued actions. Called from SubModule.OnApplicationTick.
    /// </summary>
    [DebuggerNonUserCode]
    internal static void ProcessQueue()
    {
        while (ExecutionQueue.TryDequeue(out var action))
        {
            // Top-level safety net. RunWithTcs's `when` filter has already
            // captured the exception into the TCS for the awaiting HTTP
            // handler, then let it keep propagating uncaught.
            //
            // In a normal (no-debugger) run we want this catch to absorb the
            // exception here so it doesn't tear down OnApplicationTick and
            // crash the game — the HTTP caller still sees the failure via
            // the TCS.
            //
            // When a debugger is attached, we deliberately let the exception
            // keep propagating past this frame too. Reason: with this catch
            // in place, the CLR first-pass walk finds a matching handler on
            // the main thread, classifies the original throw as "handled by
            // other code", and Rider's JMC matrix suppresses the first-chance
            // break (Other × Other is typically off; Other × Unhandled is on).
            // Skipping the catch means no main-thread handler matches — the
            // exception is "Other × Unhandled" — so Rider breaks at the
            // actual TaleWorlds throw site with all locals still live. The
            // game tick loop will unwind and the game will likely die, which
            // is exactly what happens for a manual button click that hits the
            // same bug. The sibling Lib.GAB.ToolRegistry catch is gated the
            // same way; both need to be conditional or the break still lands
            // on the worker thread at ExceptionDispatchInfo.Throw() (post-
            // unwind, locals optimized out, CORDBG_E_IL_VAR_NOT_AVAILABLE)
            // instead of the real throw frame.
            //
            // `!Debugger.IsAttached` is evaluated first so SwallowAfterCapture
            // is short-circuited away under the debugger — the catch doesn't
            // match and there's no need to run the no-op recovery either.
            try
            {
                action.Invoke();
            }
            catch (Exception) when (!Debugger.IsAttached && SwallowAfterCapture())
            {
                // No-op — TCS was already completed in the per-action filter.
            }
        }
    }

    // Tail-end matcher for ProcessQueue's safety net. Always returns true so
    // the catch absorbs the exception and keeps the game tick loop alive
    // (when a debugger is not attached — see ProcessQueue for the rationale).
    [DebuggerNonUserCode]
    private static bool SwallowAfterCapture() => true;

    /// <summary>
    /// Run a pending save AFTER ProcessQueue completes.
    /// Called from SubModule.OnApplicationTick, outside the queue loop.
    /// </summary>
    internal static void ProcessPendingSave()
    {
        var saveName = _pendingSaveName;
        if (saveName != null)
        {
            _pendingSaveName = null;
            try
            {
                Campaign.Current?.SaveHandler.SaveAs(saveName);
            }
            catch (Exception)
            {
                // Save failed — nothing to do since we already returned the response.
            }
        }
    }
}