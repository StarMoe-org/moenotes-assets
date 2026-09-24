using MoenotesAssets;
using Xunit;
namespace MoenotesAssets.Tests;

public class ExportSelectionTests
{
    [Fact]
    public void AliasesCollapseButInventoryAndUnsupportedReasonsRemain()
    {
        var (bytes, _) = Fixture.Create(); var catalog = Catalog.Parse(bytes);
        var target = catalog.Target(Fixture.Key);
        catalog.Keys["0000guid"] = [target.Id];
        Assert.Equal(new[] { Fixture.Key }, ExportSelection.UniqueKeys(catalog, catalog.Keys.Keys));
        Assert.True(catalog.Keys.ContainsKey("0000guid"));
        Assert.Null(ExportSelection.SkipReason(catalog, Fixture.Key));
        catalog.Locations[target.Id] = target with { ResourceType = "UnityEngine.Material" };
        Assert.Contains("Material", ExportSelection.SkipReason(catalog, Fixture.Key)!);
    }
    [Fact]
    public void OnlyScriptMetadataIsOmittedForSupportedDataExports()
    {
        var (bytes, _) = Fixture.Create(); var catalog = Catalog.Parse(bytes); var target = catalog.Target(Fixture.Key);
        var script = new Location(999, "scripts", "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/Android/shared_monoscripts.bundle", Catalog.Crypt, "bundle", [], new("hash", "scripts", 0, 20));
        catalog.Locations.Add(script.Id, script); target.Dependencies.Add(script.Id);
        Assert.DoesNotContain(ExportSelection.Dependencies(catalog, Fixture.Key), d => d.Id == script.Id);
        catalog.Locations[script.Id] = script with { Internal = "{runtime}/embedded-textures.bundle" };
        Assert.Contains(ExportSelection.Dependencies(catalog, Fixture.Key), d => d.Id == script.Id);
    }
}
