using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileHistory
{
    /// <summary>
    /// Contrato para generación y aplicación de deltas, y cálculo de checksum SHA-256.
    /// Implementaciones concretas pueden usar Octodiff u otra librería; también debe
    /// existir un fallback que trate el "delta" como el archivo completo cuando sea necesario.
    /// </summary>
    public interface IDeltaService : IDisposable
    {
        /// <summary>
        /// Crea un delta (binary diff) entre baseStream y newStream.
        /// Devuelve un Stream que contiene el delta. El llamador es responsable de disposar el stream devuelto.
        /// </summary>
        Task<Stream> CreateDeltaAsync(Stream baseStream, Stream newStream, CancellationToken cancellationToken = default);

        /// <summary>
        /// Aplica un delta sobre baseStream y devuelve un Stream con el resultado reconstruido.
        /// El llamador es responsable de disposar el stream devuelto.
        /// </summary>
        Task<Stream> ApplyDeltaAsync(Stream baseStream, Stream deltaStream, CancellationToken cancellationToken = default);

        /// <summary>
        /// Calcula el SHA-256 del stream (desde la posición actual). No modifica la posición del stream.
        /// </summary>
        Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken = default);
    }
}
