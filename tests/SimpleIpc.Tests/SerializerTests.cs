using SimpleIpc;

namespace SimpleIpc.Tests;

public class SystemTextJsonSerializerTests
{
    private readonly SystemTextJsonSerializer _serializer = new();

    public record TestMessage(string Name, int Value);

    [Fact]
    public void Serialize_Object_ReturnsJsonString()
    {
        var message = new TestMessage("test", 42);

        var json = _serializer.Serialize(message);

        Assert.Contains("\"name\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"value\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("42", json);
    }

    [Fact]
    public void Deserialize_ValidJson_ReturnsObject()
    {
        var json = """{"name":"test","value":42}""";

        var result = _serializer.Deserialize<TestMessage>(json);

        Assert.NotNull(result);
        Assert.Equal("test", result.Name);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Deserialize_Null_ReturnsDefault()
    {
        var result = _serializer.Deserialize<TestMessage>(null);

        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_InvalidJson_ThrowsSerializationException()
    {
        var invalidJson = "not valid json";

        Assert.Throws<IpcSerializationException>(() => _serializer.Deserialize<TestMessage>(invalidJson));
    }

    [Fact]
    public void Deserialize_ByType_ReturnsObject()
    {
        var json = """{"name":"test","value":42}""";

        var result = _serializer.Deserialize(json, typeof(TestMessage));

        Assert.NotNull(result);
        var msg = Assert.IsType<TestMessage>(result);
        Assert.Equal("test", msg.Name);
    }

    [Fact]
    public void Serialize_NonGeneric_ReturnsJsonString()
    {
        object message = new TestMessage("test", 42);

        var json = _serializer.Serialize(message);

        Assert.Contains("\"name\"", json, StringComparison.OrdinalIgnoreCase);
    }
}
