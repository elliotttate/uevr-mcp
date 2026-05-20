using System.Text.Json.Nodes;
using UevrMcp;
using Xunit;

namespace UevrMcpTests;

public class RenderManifestWriterTests
{
    static JsonObject ParseObject(string json)
        => JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("Expected JSON object");

    static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "uevr-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task WriteHlslOverride_CreatesManifestAndSource()
    {
        var dir = NewTempDir();
        var result = ParseObject(await RenderDiagnosticsTools.WriteHlslOverride(
            "0xABCDEF01",
            "ps",
            "float4 main() : SV_Target { return float4(1,0,1,1); }",
            overrideDir: dir,
            reload: false));

        Assert.True(result["ok"]!.GetValue<bool>());
        var manifestPath = result["manifest_path"]!.GetValue<string>();
        var sourcePath = result["source_path"]!.GetValue<string>();
        Assert.True(File.Exists(manifestPath));
        Assert.True(File.Exists(sourcePath));

        var manifest = ParseObject(await File.ReadAllTextAsync(manifestPath));
        Assert.Equal("dx12", manifest["backend"]!.GetValue<string>());
        Assert.Equal("ps", manifest["stage"]!.GetValue<string>());
        Assert.Equal("abcdef01", manifest["target_hash"]!.GetValue<string>());
        Assert.False(manifest["enabled"]!.GetValue<bool>());
        Assert.Equal("main.hlsl", manifest["source"]!.GetValue<string>());
    }

    [Fact]
    public async Task WriteBindOverride_CreatesRootConstantManifest()
    {
        var dir = NewTempDir();
        var result = ParseObject(await RenderDiagnosticsTools.WriteBindOverride(
            "ABCDEF01",
            5,
            "[1,2,3,4]",
            kind: "root_constants",
            stage: "ps",
            eye: "right",
            destOffset: 2,
            overrideDir: dir,
            reload: false));

        Assert.True(result["ok"]!.GetValue<bool>());
        var manifest = ParseObject(await File.ReadAllTextAsync(result["manifest_path"]!.GetValue<string>()));
        Assert.Equal("bind_override", manifest["kind"]!.GetValue<string>());
        Assert.Equal("root_constants", manifest["override"]!.GetValue<string>());
        Assert.Equal(5, manifest["root_parameter"]!.GetValue<int>());
        Assert.Equal(2, manifest["dest_offset"]!.GetValue<int>());
        Assert.Equal("right", manifest["eye"]!.GetValue<string>());
        Assert.Equal(4, manifest["values_u32"]!.AsArray().Count);
    }

    [Fact]
    public async Task WriteDxilTextPatch_CreatesPatchManifestPair()
    {
        var dir = NewTempDir();
        var result = ParseObject(await RenderDiagnosticsTools.WriteDxilTextPatch(
            "ABCDEF01",
            "cs",
            """[{ "find": "old", "replace": "new" }]""",
            perEyeVariants: true,
            overrideDir: dir,
            reload: false));

        Assert.True(result["ok"]!.GetValue<bool>());
        var manifest = ParseObject(await File.ReadAllTextAsync(result["manifest_path"]!.GetValue<string>()));
        var patch = ParseObject(await File.ReadAllTextAsync(result["patch_path"]!.GetValue<string>()));
        Assert.Equal("cs", manifest["stage"]!.GetValue<string>());
        Assert.True(manifest["per_eye_variants"]!.GetValue<bool>());
        Assert.Equal("patch.json", manifest["dxil_text_patch"]!.GetValue<string>());
        Assert.Single(patch["replacements"]!.AsArray());
    }

    [Fact]
    public async Task WriteContainerPatch_CreatesPatchManifestPair()
    {
        var dir = NewTempDir();
        var result = ParseObject(await RenderDiagnosticsTools.WriteContainerPatch(
            "ABCDEF01",
            "ms",
            """{ "fourcc": "STAT", "remove": true }""",
            overrideDir: dir,
            reload: false));

        Assert.True(result["ok"]!.GetValue<bool>());
        var manifest = ParseObject(await File.ReadAllTextAsync(result["manifest_path"]!.GetValue<string>()));
        var patch = ParseObject(await File.ReadAllTextAsync(result["patch_path"]!.GetValue<string>()));
        Assert.Equal("ms", manifest["stage"]!.GetValue<string>());
        Assert.Equal("container_patch.json", manifest["container_patch"]!.GetValue<string>());
        Assert.Single(patch["edits"]!.AsArray());
    }

    [Fact]
    public async Task WriteDxilTransform_CreatesTransformManifestPair()
    {
        var dir = NewTempDir();
        var result = ParseObject(await RenderDiagnosticsTools.WriteDxilTransform(
            "ABCDEF01",
            "ps",
            """{ "kind": "redirect_handle", "resource_class": "srv", "from_range_id": 3, "to_range_id": 4 }""",
            eye: "right",
            overrideDir: dir,
            reload: false));

        Assert.True(result["ok"]!.GetValue<bool>());
        var manifest = ParseObject(await File.ReadAllTextAsync(result["manifest_path"]!.GetValue<string>()));
        var transform = ParseObject(await File.ReadAllTextAsync(result["transform_path"]!.GetValue<string>()));
        Assert.Equal("ps", manifest["stage"]!.GetValue<string>());
        Assert.True(manifest["per_eye_variants"]!.GetValue<bool>());
        Assert.Equal("transform.json", manifest["dxil_transform"]!.GetValue<string>());
        Assert.Equal("right", transform["eye"]!.GetValue<string>());
        Assert.Single(transform["transforms"]!.AsArray());
    }
}
