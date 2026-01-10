#if NET462
using System;
#endif
using System.Text.Json;

namespace SimpleIpc;

/// <summary>
/// Default IPC serializer implementation using System.Text.Json.
/// </summary>
public sealed class SystemTextJsonSerializer : IIpcSerializer
{
    /// <summary>
    /// Gets the default instance with standard options (camelCase, case-insensitive).
    /// </summary>
    public static SystemTextJsonSerializer Default { get; } = new();

    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// Creates a new instance with default options.
    /// </summary>
    public SystemTextJsonSerializer()
        : this(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        })
    {
    }

    /// <summary>
    /// Creates a new instance with custom options.
    /// </summary>
    /// <param name="options">The JSON serializer options to use.</param>
    public SystemTextJsonSerializer(JsonSerializerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string Serialize<T>(T value)
    {
        try
        {
            return JsonSerializer.Serialize(value, _options);
        }
        catch (JsonException ex)
        {
            throw new IpcSerializationException($"Failed to serialize {typeof(T).Name}.", null, ex);
        }
    }

    /// <inheritdoc />
    public T? Deserialize<T>(string? data)
    {
        if (data is null) return default;

        try
        {
            return JsonSerializer.Deserialize<T>(data, _options);
        }
        catch (JsonException ex)
        {
            throw new IpcSerializationException($"Failed to deserialize message to {typeof(T).Name}.", data, ex);
        }
    }
}
