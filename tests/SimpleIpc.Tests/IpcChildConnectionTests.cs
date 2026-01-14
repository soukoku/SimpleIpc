using SimpleIpc;

namespace SimpleIpc.Tests;

public class IpcChildConnectionTests
{
    [Fact]
    public void GetPipeNameFromArgs_WithValidArgs_ReturnsPipeName()
    {
        var args = new[] { "--ipc-pipe", "test-pipe-name" };

        var result = IpcChildConnection.GetPipeNameFromArgs(args);

        Assert.Equal("test-pipe-name", result);
    }

    [Fact]
    public void GetPipeNameFromArgs_WithMissingArg_ReturnsNull()
    {
        var args = new[] { "--other-arg", "value" };

        var result = IpcChildConnection.GetPipeNameFromArgs(args);

        Assert.Null(result);
    }

    [Fact]
    public void GetPipeNameFromArgs_WithEmptyArgs_ReturnsNull()
    {
        var args = Array.Empty<string>();

        var result = IpcChildConnection.GetPipeNameFromArgs(args);

        Assert.Null(result);
    }

    [Fact]
    public void GetPipeNameFromArgs_WithArgAtEnd_ReturnsNull()
    {
        var args = new[] { "--ipc-pipe" };

        var result = IpcChildConnection.GetPipeNameFromArgs(args);

        Assert.Null(result);
    }

    [Fact]
    public void GetParentPidFromArgs_WithValidArgs_ReturnsPid()
    {
        var args = new[] { "--parent-pid", "12345" };

        var result = IpcChildConnection.GetParentPidFromArgs(args);

        Assert.Equal(12345, result);
    }

    [Fact]
    public void GetParentPidFromArgs_WithInvalidPid_ReturnsNull()
    {
        var args = new[] { "--parent-pid", "not-a-number" };

        var result = IpcChildConnection.GetParentPidFromArgs(args);

        Assert.Null(result);
    }

    [Fact]
    public void GetParentPidFromArgs_WithMissingArg_ReturnsNull()
    {
        var args = new[] { "--other-arg", "value" };

        var result = IpcChildConnection.GetParentPidFromArgs(args);

        Assert.Null(result);
    }

    [Fact]
    public async Task ConnectAsync_WithMissingPipeName_ThrowsArgumentException()
    {
        var args = new[] { "--other-arg", "value" };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            IpcChildConnection.ConnectAsync(args));
    }

    [Fact]
    public async Task ConnectAsync_WithNullSerializer_ThrowsArgumentNullException()
    {
        var args = new[] { "--ipc-pipe", "test-pipe" };

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            IpcChildConnection.ConnectAsync(args, null!, TimeSpan.FromSeconds(1)));
    }
}
