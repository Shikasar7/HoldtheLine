namespace HoldTheLine.Server.Rooms;

/// <summary>A client request that can't be honored. Surfaced to the client as an <c>error</c> frame
/// with this <see cref="Code"/> — never crashes the connection.</summary>
public sealed class ProtocolError(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
