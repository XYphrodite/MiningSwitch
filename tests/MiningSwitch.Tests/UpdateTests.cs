using System.IO;
using System.Security.Cryptography;
using MiningSwitch.App.Services;
using SelfUpdateKit;
using Xunit;

namespace MiningSwitch.Tests;

public class UpdateTests
{
    [Fact]
    public void Options_use_fixed_repository_direct_urls_and_sidecar_checksum()
    {
        var options = MiningSwitchUpdate.Options();

        Assert.Equal("XYphrodite/MiningSwitch", options.Repository);
        Assert.Equal(["MiningSwitch.exe"], options.ExecutableAssetNames);
        Assert.Equal("MiningSwitch.exe.sha256", options.ChecksumAssetName);
        Assert.Equal(ChecksumMode.SidecarAsset, options.ChecksumMode);
        Assert.True(options.UseDirectDownloadUrls);
        Assert.Equal([(byte)'M', (byte)'Z'], options.ExpectedMagic);
    }

    [Fact]
    public async Task SelfUpdateService_replaces_single_file_executable_after_checksum_and_probe()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "miningswitch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var installedExe = Path.Combine(tempDir, "MiningSwitch.exe");
            var oldBytes = "MZ-old-payload"u8.ToArray();
            var newBytes = "MZ-new-payload-4.3.0"u8.ToArray();
            await File.WriteAllBytesAsync(installedExe, oldBytes);

            var sha256 = Convert.ToHexStringLower(SHA256.HashData(newBytes));
            var options = MiningSwitchUpdate.Options();
            var source = new FakeReleaseSource(
                new ReleaseDescriptor(
                    "v4.3.0",
                    new ReleaseVersion(4, 3, 0),
                    new Uri("https://github.com/XYphrodite/MiningSwitch/releases/download/v4.3.0/MiningSwitch.exe"),
                    new Uri("https://github.com/XYphrodite/MiningSwitch/releases/download/v4.3.0/MiningSwitch.exe.sha256"),
                    newBytes.Length,
                    null,
                    new Uri("https://github.com/XYphrodite/MiningSwitch/releases/tag/v4.3.0")),
                newBytes,
                $"{sha256}  MiningSwitch.exe");

            var service = new SelfUpdateService(
                installedExe,
                new ReleaseVersion(4, 2, 0),
                source,
                options,
                probe: (path, _) => Task.FromResult(File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(newBytes)));

            var report = await service.UpdateAsync(new SelfUpdateRequest(), CancellationToken.None);

            Assert.Equal(SelfUpdateStatus.Updated, report.Status);
            Assert.Equal("v4.3.0", report.Tag);
            Assert.Equal(newBytes, await File.ReadAllBytesAsync(installedExe));

            // Subsequent run cleans up the retired .old-* copy.
            var cleaned = new ExecutableReplacer().RemoveRetiredCopies(installedExe);
            Assert.Equal(1, cleaned);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class FakeReleaseSource(ReleaseDescriptor descriptor, byte[] payload, string checksumText) : IReleaseSource
    {
        public Task<ReleaseDescriptor> ResolveAsync(string? tag, CancellationToken cancellationToken) =>
            Task.FromResult(descriptor);

        public async Task DownloadAsync(
            Uri address, string destinationPath, CancellationToken cancellationToken, Action<long, long?>? progress = null)
        {
            await File.WriteAllBytesAsync(destinationPath, payload, cancellationToken);
            progress?.Invoke(payload.Length, payload.Length);
        }

        public Task<string> ReadTextAsync(Uri address, CancellationToken cancellationToken) =>
            Task.FromResult(checksumText);
    }
}
