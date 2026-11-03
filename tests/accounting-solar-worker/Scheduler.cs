#nullable enable
namespace ParallelTasks;
static class Parallel
{
    internal static int Starts;
    public static Task Start(Action work) { Interlocked.Increment(ref Starts); return new Task(work); }
}
sealed class Task
{
    readonly Thread worker;
    public Exception[]? Exceptions { get; private set; }
    internal Task(Action action) { worker = new Thread(() => { try { action(); } catch (Exception e) { Exceptions = [e]; } }); worker.Start(); }
    public void Wait() => worker.Join();
}
