namespace IIoT.DeviceCa.Commands;

/// <summary>
/// A command refused to act, and changed nothing: it would have overwritten the CA, or touched a
/// device that authenticates some other way.
/// </summary>
public sealed class CaRefusedException : Exception
{
    public CaRefusedException(string message) : base(message)
    {
    }
}
