using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace FileHistory
{
    public static class RestoreHelper
    {
        /// <summary>
        /// Reconstructs the attribute (checkout or delta chain) into a MemoryStream.
        /// Caller is responsible for disposing the returned stream.
        /// </summary>
        public static Stream ReconstructAttribute(IBackupDb db, Settings settings, AttributeDbEntry attr, ILoggerFactory loggerFactory)
        {
            if (attr == null) throw new ArgumentNullException(nameof(attr));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            // If StoredFileName present, it's an .fhc package
            if (!string.IsNullOrEmpty(attr.StoredFileName))
            {
                var fhcPath = Path.Combine(settings.DataDir, attr.StoredFileName);
                if (!File.Exists(fhcPath)) throw new FileNotFoundException("Stored .fhc not found", fhcPath);

                using var fhcFs = File.OpenRead(fhcPath);
                var (meta, payload) = FhcPackage.ExtractPackageAsync(fhcFs, CancellationToken.None).GetAwaiter().GetResult();
                if (meta.Type == "checkout")
                {
                    var ms = new MemoryStream();
                    payload.CopyTo(ms);
                    ms.Seek(0, SeekOrigin.Begin);
                    return ms;
                }

                if (meta.Type == "delta")
                {
                    if (!attr.BaseAttributeId.HasValue) throw new InvalidOperationException("Delta attribute missing BaseAttributeId");

                    var attrs = db.GetAttributes(attr.FileId).OrderBy(a => a.BackupTime).ToList();
                    var baseAttr = attrs.FirstOrDefault(a => a.Id == attr.BaseAttributeId.Value);
                    if (baseAttr == null) throw new InvalidOperationException("Base checkout attribute not found");
                    if (string.IsNullOrEmpty(baseAttr.StoredFileName)) throw new InvalidOperationException("Base checkout StoredFileName missing");

                    // Extract base
                    var baseFhc = Path.Combine(settings.DataDir, baseAttr.StoredFileName);
                    using var baseFs = File.OpenRead(baseFhc);
                    var (baseMeta, basePayload) = FhcPackage.ExtractPackageAsync(baseFs, CancellationToken.None).GetAwaiter().GetResult();
                    Stream current = new MemoryStream();
                    basePayload.CopyTo(current);
                    current.Seek(0, SeekOrigin.Begin);

                    var deltaService = new DeltaService(loggerFactory?.CreateLogger<DeltaService>(), settings.AllowOctodiffFallback);
                    try
                    {
                        var deltasToApply = attrs.Where(a => a.BaseAttributeId == baseAttr.Id).OrderBy(a => a.DeltaIndex).ToList();
                        foreach (var d in deltasToApply)
                        {
                            var dFhc = Path.Combine(settings.DataDir, d.StoredFileName);
                            using var dFs = File.OpenRead(dFhc);
                            var (dMeta, dPayload) = FhcPackage.ExtractPackageAsync(dFs, CancellationToken.None).GetAwaiter().GetResult();
                            var next = deltaService.ApplyDeltaAsync(current, dPayload, CancellationToken.None).GetAwaiter().GetResult();
                            try { current.Dispose(); } catch { }
                            current = next;
                            if (d.Id == attr.Id) break;
                        }
                    }
                    finally { deltaService.Dispose(); }

                    var result = new MemoryStream();
                    current.Seek(0, SeekOrigin.Begin);
                    current.CopyTo(result);
                    result.Seek(0, SeekOrigin.Begin);
                    try { current.Dispose(); } catch { }
                    return result;
                }
            }

            // Legacy file backup
            var backupFileFullPath = BackupDb.BackupFileName(settings.DataDir, Path.Combine(db.GetFileDir(attr.FileId), db.GetAttribute(attr.FileId, attr.CreationTime, attr.LastWriteTime, attr.Size).ToString()), attr.BackupTime);
            if (!File.Exists(backupFileFullPath)) throw new FileNotFoundException("Backup file not found", backupFileFullPath);
            return File.OpenRead(backupFileFullPath);
        }
    }
}
