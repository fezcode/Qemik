using Qemik.Core;
using Xunit;

namespace Qemik.Tests;
public sealed class LibraryActivationTests
{
    [Fact]
    public async Task SecondLaunchActivatesOnlyTheMatchingLibraryAndCanReconnect()
    {
        var root = CoreTests.TestDirectory(); var other = CoreTests.TestDirectory();
        var called = 0; var otherCalled = 0; var foregroundPid = 0;
        using var first = new LibraryActivation(root, () => Interlocked.Increment(ref called));
        using var second = new LibraryActivation(other, () => Interlocked.Increment(ref otherCalled));
        Assert.True(await LibraryActivation.NotifyAsync(root, pid => foregroundPid = pid));
        Assert.Equal(Environment.ProcessId, foregroundPid);
        Assert.True(await LibraryActivation.NotifyAsync(Path.Combine(root, ".")));
        Assert.Equal(2, called); Assert.Equal(0, otherCalled);
        Assert.True(await LibraryActivation.NotifyAsync(other)); Assert.Equal(1, otherCalled);
    }
}
