using System.Threading.Channels;
using SuperStatus.Data.Constants;

namespace SuperStatus.Services.Services
{
    /// <summary>Issue #168: a request to draft an auto-incident for a check that has
    /// stayed down past the threshold. Carries only ids/enums — the worker reloads
    /// everything fresh in its own DI scope.</summary>
    public sealed record AutoIncidentRequest(long CheckId, FailType FailType);

    /// <summary>Issue #168: a bounded hand-off from the scheduler's hot path to the
    /// background drafting worker, so a slow model can never stall checks. Bounded +
    /// drop-when-full: redundant requests for an already-flagged outage are cheap to
    /// lose (the worker + the partial unique index keep it idempotent anyway).</summary>
    public interface IAutoIncidentQueue
    {
        /// <summary>Enqueue without blocking; returns false if the queue is full.</summary>
        bool TryEnqueue(AutoIncidentRequest request);

        /// <summary>Drains requests for the background worker.</summary>
        IAsyncEnumerable<AutoIncidentRequest> ReadAllAsync(CancellationToken cancellationToken);
    }

    public sealed class AutoIncidentQueue : IAutoIncidentQueue
    {
        private const int Capacity = 256;

        private readonly Channel<AutoIncidentRequest> _channel = Channel.CreateBounded<AutoIncidentRequest>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite, // never block the scheduler tick
                SingleReader = true,
                SingleWriter = false,
            });

        public bool TryEnqueue(AutoIncidentRequest request) => _channel.Writer.TryWrite(request);

        public IAsyncEnumerable<AutoIncidentRequest> ReadAllAsync(CancellationToken cancellationToken)
            => _channel.Reader.ReadAllAsync(cancellationToken);
    }
}
