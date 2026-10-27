namespace IIoT.Functions.DeadLettering;

/// <summary>The dead-letter queues and the connection they are reached through (ADR-010).</summary>
/// <remarks>
/// The names are fixed here and in <c>infra/main.bicep</c>, which declares both queues, so that
/// nothing has to create a queue at run time and no identity needs the right to.
/// </remarks>
public static class DeadLetterQueues
{
    /// <summary>
    /// The connection-setting prefix: <c>DeadLetterStorage__queueServiceUri</c>,
    /// <c>__credential</c> and <c>__clientId</c>, or <c>DeadLetterStorage</c> itself as a connection
    /// string. Its own prefix even though staging points it at the host storage account, so that the
    /// queues can move to another account by changing settings alone.
    /// </summary>
    public const string ConnectionName = "DeadLetterStorage";

    /// <summary>Messages whose forwarding failed, waiting for <c>RetryForwarding</c>.</summary>
    public const string ForwardingFailed = "forwarding-failed";

    /// <summary>
    /// Entries a retry gave up on, for an operator. The name follows the queue trigger's own
    /// convention, <c>&lt;queue&gt;-poison</c>, so that the runtime's poison handling and
    /// <c>RetryForwarding</c>'s explicit moves land in the same queue.
    /// </summary>
    public const string ForwardingFailedPoison = ForwardingFailed + "-poison";
}
