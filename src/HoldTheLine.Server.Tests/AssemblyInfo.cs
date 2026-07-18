using Xunit;

// These are integration tests: each spins up a real Kestrel + WebSocket server and, in several cases,
// drives full bot matches with reconnect/timeout timing. Running whole classes in parallel starves the
// CPU and makes the timing-sensitive ones (reconnect, queue pairing) flake. Serialize them — the suite
// stays well under a minute and is deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
