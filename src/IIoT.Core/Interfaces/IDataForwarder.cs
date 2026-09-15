using IIoT.Core.Models;

namespace IIoT.Core.Interfaces;

public interface IDataForwarder
{
    Task ForwardAsync(SensorReading reading, CancellationToken cancellationToken = default);
}
