using System.Security.Cryptography;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class ClientModCatalogTests
{
    [Fact]
    public void Manifest_preserves_configured_order_and_omits_unavailable_mods()
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(folder.FullName, "Alpha.dll"), [1]);
            File.WriteAllBytes(Path.Combine(folder.FullName, "Beta.dll"), [2, 3]);
            File.WriteAllBytes(Path.Combine(folder.FullName, "Unconfigured.dll"), [4]);

            string[] configured = ["Beta.dll", "Missing.dll", "Alpha.dll"];
            var catalog = new ClientModCatalog(folder.FullName, configured);
            ClientModManifest manifest = catalog.CreateManifest();

            Assert.Equal(new[] { "Beta.dll", "Alpha.dll" }, manifest.Mods.Select(mod => mod.Name));
            Assert.Equal(2, manifest.Mods[0].Size);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(new byte[] { 2, 3 })), manifest.Mods[0].Sha256);
            Assert.Null(catalog.OpenDownload("Missing.dll"));
            Assert.Null(catalog.OpenDownload("Unconfigured.dll"));

            using var download = catalog.OpenDownload("Beta.dll");
            Assert.NotNull(download);
            Assert.Equal(2, download.ReadByte());

            ControlApi.Respuesta response = ControlApi.Mods("GET", catalog);
            Assert.Equal(200, response.Codigo);
            using var body = JsonDocument.Parse(response.Json);
            JsonElement entry = body.RootElement.GetProperty("mods")[0];
            Assert.Equal("Beta.dll", entry.GetProperty("name").GetString());
            Assert.Equal(2, entry.GetProperty("size").GetInt64());
            Assert.Equal(manifest.Mods[0].Sha256, entry.GetProperty("sha256").GetString());

            Assert.Empty(new ClientModCatalog(Path.Combine(folder.FullName, "absent"), configured).CreateManifest().Mods);
            File.WriteAllBytes(Path.Combine(folder.FullName, "Empty.dll"), []);
            Assert.Equal(503, ControlApi.Mods("GET", new ClientModCatalog(folder.FullName, ["Empty.dll"])).Codigo);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("../Alpha.dll")]
    [InlineData("sub\\Alpha.dll")]
    [InlineData("Alpha.dll:Other.dll")]
    [InlineData("NUL.dll")]
    [InlineData("JondoMODloader.dll")]
    public void Download_rejects_unsafe_names_even_if_configured(string name)
    {
        Assert.False(ClientModManifest.IsDllName(name));
        var catalog = new ClientModCatalog(Path.GetTempPath(), [name]);
        Assert.Null(catalog.OpenDownload(name));
        Assert.Equal(503, ControlApi.Mods("GET", catalog).Codigo);
    }
}
