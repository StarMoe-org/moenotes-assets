using MoenotesAssets;
using Xunit;
namespace MoenotesAssets.Tests;

public class ConfigTests
{
    // The 1.1.0 international catalog has 50,281 keys: a full-prefix export (batch/update default) must not be
    // rejected before UniqueKeys reduces it to the ~32,500 actual targets.
    [Fact]
    public void MaxKeysCoversTheLargestCatalog()
    {
        var config = new Config { CdnRoot = "https://cdn.invalid/prod" };
        Assert.True(config.MaxKeys >= 50_281, $"max_keys default {config.MaxKeys} is below the 1.1.0 catalog size");
        config.Validate();
    }

    [Fact]
    public void MaxKeysOverrideStaysBounded()
    {
        new Config { CdnRoot = "https://cdn.invalid/prod", MaxKeys = 500_000 }.Validate();
        Assert.Throws<InvalidDataException>(() => new Config { CdnRoot = "https://cdn.invalid/prod", MaxKeys = 500_001 }.Validate());
    }
}
