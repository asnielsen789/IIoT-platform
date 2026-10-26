using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;

namespace IIoT.Integration.Tests.Infrastructure;

/// <summary>
/// A function invocation's context, with only what the functions read from it: the cancellation
/// token and the trigger's metadata.
/// </summary>
/// <remarks>
/// The metadata is held as the worker holds it, in a case-insensitive dictionary, and a test
/// passes values in the form the host sends them: the queue trigger's dequeue count arrives as
/// text, not as a number.
/// </remarks>
public sealed class TestFunctionContext : FunctionContext
{
    private readonly TestBindingContext _bindingContext;

    public TestFunctionContext(IReadOnlyDictionary<string, object?>? bindingData = null)
    {
        _bindingContext = new TestBindingContext(
            new Dictionary<string, object?>(bindingData ?? new Dictionary<string, object?>(), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>A queue-triggered invocation on its <paramref name="dequeueCount"/>th attempt.</summary>
    public static TestFunctionContext ForQueueAttempt(long dequeueCount) =>
        new(new Dictionary<string, object?> { ["DequeueCount"] = dequeueCount.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    public override string InvocationId => "test-invocation";

    public override string FunctionId => "test-function";

    public override TraceContext TraceContext => throw new NotSupportedException();

    public override BindingContext BindingContext => _bindingContext;

    public override RetryContext RetryContext => throw new NotSupportedException();

    public override IServiceProvider InstanceServices { get; set; } = new ServiceCollection().BuildServiceProvider();

    public override FunctionDefinition FunctionDefinition => throw new NotSupportedException();

    public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();

    public override IInvocationFeatures Features => throw new NotSupportedException();

    private sealed class TestBindingContext(IReadOnlyDictionary<string, object?> bindingData) : BindingContext
    {
        public override IReadOnlyDictionary<string, object?> BindingData { get; } = bindingData;
    }
}
