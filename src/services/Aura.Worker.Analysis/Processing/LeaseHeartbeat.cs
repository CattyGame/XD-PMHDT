using Aura.Worker.Analysis.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Analysis.Processing;

/// <summary>Renews only the claim acquired by this invocation, using an independent repository connection.</summary>
internal sealed class LeaseHeartbeat : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task running;
    public LeaseHeartbeat(IJobRepository jobs,JobLease lease,TimeSpan interval,TimeSpan duration,
        CancellationTokenSource operation,ILogger logger)
    {
        running=Run();
        async Task Run()
        {
            try
            {
                while(true)
                {
                    await Task.Delay(interval,stop.Token);
                    if(await jobs.RenewLeaseAsync(lease,duration,stop.Token) is null)
                        throw new JobLeaseLostException(lease.JobId);
                }
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
            catch(Exception ex)
            {
                logger.LogWarning(ex,"Worker lease renewal failed for job {JobId}",lease.JobId);
                operation.Cancel();
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();await running;stop.Dispose();
    }
}
