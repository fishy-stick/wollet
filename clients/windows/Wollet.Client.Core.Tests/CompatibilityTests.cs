using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wollet.Client.Core;
namespace Wollet.Client.Core.Tests;
[TestClass]
public sealed class CompatibilityTests
{
    public sealed record Fixture(string Name, CompatibilityEndpoint Client, CompatibilityEndpoint Server, string Kind, string Component, string Target, int Count);
    [TestMethod]
    public void SharedFixtures()
    {
        var cases = JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "compatibility-fixtures.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        foreach (var test in cases)
        {
            var result = FeatureCatalog.Default.Evaluate(test.Client, test.Server);
            Assert.AreEqual(test.Kind, result.Kind, test.Name);
            Assert.HasCount(test.Count, result.Missing, test.Name);
            foreach (var missing in result.Missing) { Assert.AreEqual(test.Component, missing.Component, test.Name); Assert.AreEqual(test.Target, missing.Target ?? "", test.Name); }
        }
    }
    [TestMethod]
    public void DisconnectClearsUpgradeAdvice()
    {
        var state = new ConnectionCompatibility();
        state.Connected("1.0.4", ["protocol.v1"], ["protocol.v1", "shutdown-plan.v1"]);
        Assert.AreEqual("server_upgrade", state.Snapshot.Kind);
        state.Disconnected();
        Assert.IsTrue(state.Snapshot.Historical);
        Assert.IsEmpty(state.Snapshot.Missing);
        Assert.AreEqual("待确认", state.Snapshot.Label);
    }
    [TestMethod]
    public void NormalizeVersions()
    {
        foreach (var (input, expected) in new[] {("1.0", "1.0.0"), ("1.0.0.0", "1.0.0"), ("1.0.0.1", "1.0.0.1"), ("1.1.0-RC.1+abc", "1.1.0-rc.1"), ("v1.0.0", ""), ("1.0.0\n", ""), ("999999999999.0.0", "")})
            Assert.AreEqual(expected, FeatureCatalog.Normalize(input), input);
    }
}
