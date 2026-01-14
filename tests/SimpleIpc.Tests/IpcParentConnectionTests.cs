using SimpleIpc;

namespace SimpleIpc.Tests;

public class IpcParentConnectionTests
{
    [Fact]
    public async Task StartChildAsync_WithNonExistentPath_ThrowsFileNotFoundException()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".exe");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            IpcParentConnection.StartChildAsync(invalidPath));
    }

    [Fact]
    public async Task StartChildAsync_WithNullSerializer_ThrowsArgumentNullException()
    {
        var dummyPath = typeof(IpcParentConnectionTests).Assembly.Location;

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            IpcParentConnection.StartChildAsync(dummyPath, null!, TimeSpan.FromSeconds(1)));
    }
}
