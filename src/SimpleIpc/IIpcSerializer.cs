#if NET462
using System;
#endif

namespace SimpleIpc;

/// <summary>
/// Defines the contract for serializing and deserializing IPC messages.
/// Implement this interface to use a different serialization library.
/// </summary>
public interface IIpcSerializer
{
    /// <summary>
    /// Serializes an object to a string.
    /// </summary>
    /// <typeparam name="T">The type of object to serialize.</typeparam>
    /// <param name="value">The object to serialize.</param>
    /// <returns>The serialized string representation.</returns>
    /// <exception cref="IpcSerializationException">Thrown when serialization fails.</exception>
    string Serialize<T>(T value);

    /// <summary>
    /// Serializes an object to a string.
    /// </summary>
    /// <param name="value">The object to serialize.</param>
    /// <returns>The serialized string representation.</returns>
    /// <exception cref="IpcSerializationException">Thrown when serialization fails.</exception>
    string Serialize(object value);

    /// <summary>
    /// Deserializes a string to an object of the specified type.
    /// </summary>
    /// <typeparam name="T">The type to deserialize to.</typeparam>
    /// <param name="data">The string data to deserialize.</param>
    /// <returns>The deserialized object, or default if data is null.</returns>
    /// <exception cref="IpcSerializationException">Thrown when deserialization fails.</exception>
    T? Deserialize<T>(string? data);

    /// <summary>
    /// Deserializes a string to an object of the specified type.
    /// </summary>
    /// <param name="data">The string data to deserialize.</param>
    /// <param name="type">The type to deserialize to.</param>
    /// <returns>The deserialized object, or null if data is null.</returns>
    /// <exception cref="IpcSerializationException">Thrown when deserialization fails.</exception>
    object? Deserialize(string? data, Type type);
}
