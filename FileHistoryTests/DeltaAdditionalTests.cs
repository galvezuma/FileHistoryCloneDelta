using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FileHistoryTests
{
    [TestClass]
    public class DeltaAdditionalTests
    {
        [TestMethod]
        public async Task ChainReconstruction_MultipleDeltas_Works()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "fhc_chain_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var baseBytes = Encoding.UTF8.GetBytes("LineA\nLineB\nLineC\n");
                var v1Bytes = Encoding.UTF8.GetBytes("LineA modified\nLineB\nLineC\n");
                var v2Bytes = Encoding.UTF8.GetBytes("LineA modified\nLineB changed\nLineC\nExtra\n");

                var ds = new FileHistory.DeltaService();
                using var baseStream = new MemoryStream(baseBytes);
                using var v1Stream = new MemoryStream(v1Bytes);
                using var v2Stream = new MemoryStream(v2Bytes);

                var delta1 = await ds.CreateDeltaAsync(baseStream, v1Stream);
                delta1.Seek(0, SeekOrigin.Begin);

                // apply delta1 to base -> should equal v1
                baseStream.Seek(0, SeekOrigin.Begin);
                using var recon1 = await ds.ApplyDeltaAsync(baseStream, delta1);
                recon1.Seek(0, SeekOrigin.Begin);
                using var ms1 = new MemoryStream(); await recon1.CopyToAsync(ms1);
                CollectionAssert.AreEqual(v1Bytes, ms1.ToArray());

                // create delta2 between recon1 and v2
                recon1.Seek(0, SeekOrigin.Begin);
                using var delta2 = await ds.CreateDeltaAsync(recon1, v2Stream);
                delta2.Seek(0, SeekOrigin.Begin);

                // apply delta1 then delta2 sequentially
                using var applyBase = new MemoryStream(baseBytes);
                using var d1 = await ds.CreateDeltaAsync(new MemoryStream(baseBytes), new MemoryStream(v1Bytes));
                d1.Seek(0, SeekOrigin.Begin);
                using var r1 = await ds.ApplyDeltaAsync(applyBase, d1);
                r1.Seek(0, SeekOrigin.Begin);
                using var r2 = await ds.ApplyDeltaAsync(r1, delta2);
                r2.Seek(0, SeekOrigin.Begin);
                using var msFinal = new MemoryStream(); await r2.CopyToAsync(msFinal);
                CollectionAssert.AreEqual(v2Bytes, msFinal.ToArray());
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        [TestMethod]
        public async Task FhcPackage_And_BackupDb_Roundtrip_Works()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "fhc_db_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var settings = new FileHistory.Settings { BackupBaseDir = tmp };
                // ensure config dir inside tmp
                var loggerFactory = new Microsoft.Extensions.Logging.LoggerFactory();
                using var db = new FileHistory.BackupDb(settings, loggerFactory);

                var dataDir = settings.DataDir;
                Directory.CreateDirectory(dataDir);

                var original = Encoding.UTF8.GetBytes("original content\n");
                var fhcPath = Path.Combine(dataDir, "test1.fhc");
                using (var payload = new MemoryStream(original))
                {
                    var meta = new FileHistory.FhcMetadata { Type = "checkout", Sha256 = "", OriginalFileName = "a.txt", OriginalSize = original.Length };
                    using var outfs = new FileStream(fhcPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await FileHistory.FhcPackage.CreatePackageAsync(payload, meta, outfs);
                }

                var fentry = db.InsertFile(Path.Combine("C:", "a.txt"));
                db.InsertAttributeExtended(fentry.Id, DateTime.Now, DateTime.Now, DateTime.Now, DateTime.Now, original.Length, "checkout", Path.GetRelativePath(dataDir, fhcPath), "", null, 0, DateTimeOffset.UtcNow);

                var attrs = db.GetAttributes(fentry.Id);
                Assert.AreEqual(1, attrs.Count);
                var a = attrs[0];
                var stored = Path.Combine(dataDir, a.StoredFileName);
                using var fhc = new FileStream(stored, FileMode.Open, FileAccess.Read, FileShare.Read);
                var (metaRead, payloadRead) = await FileHistory.FhcPackage.ExtractPackageAsync(fhc);
                using var ms = new MemoryStream(); await payloadRead.CopyToAsync(ms);
                CollectionAssert.AreEqual(original, ms.ToArray());
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        class FaultyStream : Stream
        {
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new IOException("simulated read failure");
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        [TestMethod]
        public async Task DeltaService_NoFallback_ThrowsOnOctodiffFailure()
        {
            var ds = new FileHistory.DeltaService(null, allowFallback: false);
            using var baseStream = new MemoryStream(Encoding.UTF8.GetBytes("base"));
            using var badNew = new FaultyStream();

            await Assert.ThrowsExceptionAsync<Exception>(async () => await ds.CreateDeltaAsync(baseStream, badNew));
        }
    }
}
