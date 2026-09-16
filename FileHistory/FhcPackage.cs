using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FileHistory
{
    public class FhcMetadata
    {
        public string Type { get; set; } = "delta"; // "checkout" | "delta"
        public string? BaseBackupId { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public long OriginalSize { get; set; }
        public int DeltaIndex { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public int FormatVersion { get; set; } = 1;
    }

    /// <summary>
    /// Utilidad para empaquetar/desempaquetar archivos .fhc (ZIP) que contienen manifest.json y payload.
    /// </summary>
    public static class FhcPackage
    {
        private const string ManifestName = "manifest.json";
        private const string PayloadName = "payload.bin";

        public static async Task CreatePackageAsync(Stream payloadStream, FhcMetadata metadata, Stream outputStream, CancellationToken cancellationToken = default)
        {
            if (payloadStream is null) throw new ArgumentNullException(nameof(payloadStream));
            if (metadata is null) throw new ArgumentNullException(nameof(metadata));
            // Ensure payloadStream position is at beginning if seekable
            if (payloadStream.CanSeek) payloadStream.Seek(0, SeekOrigin.Begin);

            using var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true);

            // Add manifest
            var manifestEntry = archive.CreateEntry(ManifestName, CompressionLevel.Optimal);
            using (var entryStream = manifestEntry.Open())
            {
                await JsonSerializer.SerializeAsync(entryStream, metadata, new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }, cancellationToken).ConfigureAwait(false);
            }

            // Add payload
            var payloadEntry = archive.CreateEntry(PayloadName, CompressionLevel.Optimal);
            using (var entryStream = payloadEntry.Open())
            {
                // Copy payload
                await payloadStream.CopyToAsync(entryStream, 81920, cancellationToken).ConfigureAwait(false);
            }

            // leave stream open for caller
        }

        public static async Task<(FhcMetadata Metadata, MemoryStream Payload)> ExtractPackageAsync(Stream fhcStream, CancellationToken cancellationToken = default)
        {
            if (fhcStream is null) throw new ArgumentNullException(nameof(fhcStream));
            if (fhcStream.CanSeek) fhcStream.Seek(0, SeekOrigin.Begin);

            using var archive = new ZipArchive(fhcStream, ZipArchiveMode.Read, leaveOpen: true);

            var manifestEntry = archive.GetEntry(ManifestName) ?? throw new InvalidDataException("manifest.json not found in .fhc package");
            FhcMetadata metadata;
            using (var entryStream = manifestEntry.Open())
            {
                metadata = await JsonSerializer.DeserializeAsync<FhcMetadata>(entryStream, cancellationToken: cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("manifest.json could not be deserialized");
            }

            var payloadEntry = archive.GetEntry(PayloadName) ?? throw new InvalidDataException("payload.bin not found in .fhc package");
            var payloadMs = new MemoryStream();
            using (var entryStream = payloadEntry.Open())
            {
                await entryStream.CopyToAsync(payloadMs, 81920, cancellationToken).ConfigureAwait(false);
            }
            payloadMs.Seek(0, SeekOrigin.Begin);

            return (metadata, payloadMs);
        }
    }
}
