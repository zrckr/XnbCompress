namespace XnbCompress.Test;

// Validate the two environment-variable values without changing process-wide state.
public class SampleSettingsTests
{
    [Fact]
    public void AcceptExistingDirectories()
    {
        // Existing folders are resolved to absolute paths for consistent asset pairing.
        var root = Path.GetTempPath();
        var settings = SampleSettings.Load(root, root);
        Assert.Equal(Path.GetFullPath(root), settings.CompressedRoot);
        Assert.Equal(Path.GetFullPath(root), settings.DecompressedRoot);
    }

    [Theory]
    [InlineData(null, "valid")]
    [InlineData("valid", null)]
    [InlineData("", "valid")]
    [InlineData("valid", " ")]
    public void RejectMissingPaths(string? compressed, string? decompressed)
    {
        // Each variable is required; omitted paths must not yield zero passing tests.
        Assert.Throws<InvalidDataException>(() => SampleSettings.Load(compressed, decompressed));
    }

    [Fact]
    public void RejectNonexistentDirectory()
    {
        // A configured typo must fail with the missing directory, not reduce coverage.
        var missing = Path.Combine(Path.GetTempPath(), "xnb-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(() => SampleSettings.Load(missing, Path.GetTempPath()));
    }
}
