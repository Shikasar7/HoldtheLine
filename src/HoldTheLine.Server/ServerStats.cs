namespace HoldTheLine.Server;

/// <summary>Live server gauges for /healthz (M3 B4). Just an atomic connection count for now; matches,
/// queue length and today's games are read from their own owners at request time.</summary>
public sealed class ServerStats
{
    private int _connections;

    public int Connections => Volatile.Read(ref _connections);
    public void Connected() => Interlocked.Increment(ref _connections);
    public void Disconnected() => Interlocked.Decrement(ref _connections);
}
