using Concealment.Accounting;
int checks = 0;
void Check(bool pass, string why) { if (!pass) throw new Exception(why); checks++; }
int simulation = Thread.CurrentThread.ManagedThreadId, sampled = 0;
bool result = AccountingEngine.SolarWorkerStep(simulation, () => { sampled = Thread.CurrentThread.ManagedThreadId; return false; });
Check(!result && sampled != simulation, "main participant dispatches actual sample to worker");
Check(ParallelTasks.Parallel.Starts == 1, "one finite joined handoff");
int workerId = 0, existingId = 0;
var alreadyWorker = new Thread(() => { workerId = Thread.CurrentThread.ManagedThreadId; Check(AccountingEngine.SolarWorkerStep(simulation, () => { existingId = Thread.CurrentThread.ManagedThreadId; return true; }), "stop flag preserved on existing worker"); });
alreadyWorker.Start(); alreadyWorker.Join();
Check(existingId == workerId && workerId != simulation && ParallelTasks.Parallel.Starts == 1, "existing native worker executes directly without nested handoff");
using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
bool insidePhase = true, complete = false;
var observer = new Thread(() => { started.Wait(); Check(insidePhase && !complete, "phase held until child completes"); release.Set(); });
observer.Start();
AccountingEngine.SolarWorkerStep(simulation, () => { started.Set(); release.Wait(); Check(insidePhase, "child live reads finish inside protected caller lifetime"); complete = true; return false; });
insidePhase = false; observer.Join();
Check(complete && !insidePhase, "callback cannot exit its phase before joined work ends");
try { AccountingEngine.SolarWorkerStep(simulation, () => throw new InvalidOperationException("sample fault")); throw new Exception("fault lost"); }
catch (AggregateException e) { Check(e.InnerException is InvalidOperationException && e.InnerException.Message == "sample fault", "native Wait does not throw worker error; helper explicitly propagates after join"); }
using var cancel = new CancellationTokenSource(); cancel.Cancel();
try { AccountingEngine.SolarWorkerStep(simulation, () => { cancel.Token.ThrowIfCancellationRequested(); return false; }); throw new Exception("cancellation lost"); }
catch (AggregateException e) { Check(e.InnerException is OperationCanceledException, "cancellation ends worker before caller fault handling"); }
Console.WriteLine($"PASS {checks} exact-source solar dispatch/join assertions; inert scheduler, no native physics/timing claim");
