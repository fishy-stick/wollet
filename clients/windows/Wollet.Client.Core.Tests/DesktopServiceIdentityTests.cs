using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wollet.Client;

namespace Wollet.Client.Core.Tests;

[TestClass]
public sealed class DesktopServiceIdentityTests
{
    [TestMethod]
    public void AcceptsMatchingServiceAndPipeProcess() => DesktopServiceIdentity.Validate(42, 42, 42);

    [TestMethod]
    [DataRow(0u, 0u, 0u)]
    [DataRow(42u, 42u, 99u)]
    [DataRow(42u, 99u, 42u)]
    [DataRow(42u, 99u, 99u)]
    [DataRow(42u, 0u, 42u)]
    public void RejectsImpostorsAndServiceRestart(uint before, uint after, uint pipe) =>
        Assert.Throws<IOException>(() => DesktopServiceIdentity.Validate(before, after, pipe));
}
