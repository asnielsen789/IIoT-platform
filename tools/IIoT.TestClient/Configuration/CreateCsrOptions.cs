namespace IIoT.TestClient.Configuration;

/// <summary>What one <c>create-csr</c> run needs to know.</summary>
/// <param name="DeviceId">The device the new key is for. Also the CN the CSR asks for.</param>
/// <param name="OutputDirectory">Where the key and the CSR are written.</param>
public sealed record CreateCsrOptions(string DeviceId, string OutputDirectory);
