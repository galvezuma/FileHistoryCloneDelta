using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Reflection;
using Octodiff.Core;
using Octodiff.Diagnostics;

namespace FileHistory
{
    /// <summary>
    /// Implementación inicial de IDeltaService.
    /// Actualmente usa un fallback simple donde el "delta" es el fichero completo comprimido.
    /// En el futuro se substituirá por una implementación que use Octodiff para deltas reales.
    /// </summary>
    using Microsoft.Extensions.Logging;

    public class DeltaService : IDeltaService
    {
        private bool _disposed;
        readonly ILogger<DeltaService>? _logger;
        readonly bool _allowFallback;
        public DeltaService(ILogger<DeltaService>? logger = null, bool allowFallback = true)
        {
            _logger = logger;
            _allowFallback = allowFallback;
        }

        public async Task<Stream> CreateDeltaAsync(Stream baseStream, Stream newStream, CancellationToken cancellationToken = default)
        {
            // Intentar usar Octodiff por reflexión (evita enlazar tipos en tiempo de compilación)
            try
            {
                // Asegurar posiciones
                if (baseStream != null && baseStream.CanSeek) baseStream.Seek(0, SeekOrigin.Begin);
                if (newStream.CanSeek) newStream.Seek(0, SeekOrigin.Begin);

                // Usar API directa de Octodiff
                // 1) Generar signature del base (SignatureWriter escribe en un stream)
                var signatureStream = new MemoryStream();
                var sigWriter = new SignatureWriter(signatureStream);
                var sigBuilder = new SignatureBuilder();
                sigBuilder.Build(baseStream ?? Stream.Null, sigWriter);
                signatureStream.Seek(0, SeekOrigin.Begin);

                // 2) Crear delta comparando signature (SignatureReader) y newStream, escribiendo mediante BinaryDeltaWriter
                var signatureReader = new SignatureReader(signatureStream, new NullProgressReporter());
                var deltaStream = new MemoryStream();
                var deltaWriter = new BinaryDeltaWriter(deltaStream);
                var deltaBuilder = new DeltaBuilder();
                deltaBuilder.BuildDelta(newStream, signatureReader, deltaWriter);
                if (deltaStream.CanSeek) deltaStream.Seek(0, SeekOrigin.Begin);
                return deltaStream;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Octodiff CreateDelta failed, allowFallback={Allow}, falling back to GZip: {Message}", _allowFallback, ex.Message);
                if (!_allowFallback)
                {
                    throw;
                }
                // Fallback conservador: empaquetar el nuevo fichero comprimido como "delta".
                var ms = new MemoryStream();
                using (var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                {
                    if (newStream.CanSeek) newStream.Seek(0, SeekOrigin.Begin);
                    await newStream.CopyToAsync(gzip, 81920, cancellationToken).ConfigureAwait(false);
                }
                ms.Seek(0, SeekOrigin.Begin);
                return ms; // caller disposes
            }
        }

        public async Task<Stream> ApplyDeltaAsync(Stream baseStream, Stream deltaStream, CancellationToken cancellationToken = default)
        {
            try
            {
                if (baseStream != null && baseStream.CanSeek) baseStream.Seek(0, SeekOrigin.Begin);
                if (deltaStream.CanSeek) deltaStream.Seek(0, SeekOrigin.Begin);

                // Usar API directa de Octodiff para aplicar delta
                var outMs = new MemoryStream();
                var deltaReader = new BinaryDeltaReader(deltaStream, new NullProgressReporter());
                var applier = new DeltaApplier();
                applier.Apply(baseStream ?? Stream.Null, deltaReader, outMs);
                if (outMs.CanSeek) outMs.Seek(0, SeekOrigin.Begin);
                return outMs;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Octodiff ApplyDelta failed, allowFallback={Allow}, using GZip fallback: {Message}", _allowFallback, ex.Message);
                if (!_allowFallback)
                    throw;
                // Fallback: descomprimir gzip
                var outMs = new MemoryStream();
                using (var gzip = new GZipStream(deltaStream, CompressionMode.Decompress, leaveOpen: true))
                {
                    await gzip.CopyToAsync(outMs, 81920, cancellationToken).ConfigureAwait(false);
                }
                outMs.Seek(0, SeekOrigin.Begin);
                return outMs;
            }
        }

        public async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken = default)
        {
            if (stream is null) throw new ArgumentNullException(nameof(stream));
            var initialPosition = stream.CanSeek ? stream.Position : (long?)null;
            if (stream.CanSeek) stream.Seek(0, SeekOrigin.Begin);

            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            if (initialPosition.HasValue) stream.Seek(initialPosition.Value, SeekOrigin.Begin);
            return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
