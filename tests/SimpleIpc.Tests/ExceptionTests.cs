using SimpleIpc;

namespace SimpleIpc.Tests;

public class ExceptionTests
{
    [Fact]
    public void IpcDisconnectedException_DefaultConstructor_HasDefaultMessage()
    {
        var ex = new IpcDisconnectedException();

        Assert.Equal("The IPC connection was lost.", ex.Message);
    }

    [Fact]
    public void IpcDisconnectedException_WithMessage_PreservesMessage()
    {
        var ex = new IpcDisconnectedException("Custom message");

        Assert.Equal("Custom message", ex.Message);
    }

    [Fact]
    public void IpcDisconnectedException_WithInnerException_PreservesInner()
    {
        var inner = new InvalidOperationException("Inner");
        var ex = new IpcDisconnectedException("Outer", inner);

        Assert.Equal("Outer", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void IpcRemoteException_WithMessage_PreservesMessage()
    {
        var ex = new IpcRemoteException("Remote error");

        Assert.Equal("Remote error", ex.Message);
    }

    [Fact]
    public void IpcRemoteException_WithInnerException_PreservesInner()
    {
        var inner = new InvalidOperationException("Inner");
        var ex = new IpcRemoteException("Outer", inner);

        Assert.Equal("Outer", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void IpcSerializationException_WithRawJson_PreservesRawJson()
    {
        var ex = new IpcSerializationException("Failed", """{"invalid":}""");

        Assert.Equal("Failed", ex.Message);
        Assert.Equal("""{"invalid":}""", ex.RawJson);
    }

    [Fact]
    public void IpcSerializationException_WithInnerException_PreservesAll()
    {
        var inner = new FormatException("Format error");
        var ex = new IpcSerializationException("Failed", "raw", inner);

        Assert.Equal("Failed", ex.Message);
        Assert.Equal("raw", ex.RawJson);
        Assert.Same(inner, ex.InnerException);
    }
}
