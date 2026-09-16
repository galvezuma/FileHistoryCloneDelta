using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FileHistoryTests
{
    [TestClass]
    public class DeltaIntegrationTests
    {
        [TestMethod]
        public async Task CreateAndApplyDelta_WithOctodiff_Works()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "fhc_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);

            try
            {
                var basePath = Path.Combine(tmp, "base.bin");
                var newPath = Path.Combine(tmp, "new.bin");

                // base content
                var baseContent = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog.\nLine2\nLine3\n");
                await File.WriteAllBytesAsync(basePath, baseContent);

                // new content (modified)
                var newContent = Encoding.UTF8.GetBytes("The quick brown fox jumps over the very lazy dog.\nLine2 modified\nLine3\nAnother line\n");
                await File.WriteAllBytesAsync(newPath, newContent);

                using var ds = new FileHistory.DeltaService();
                using var baseStream = File.OpenRead(basePath);
                using var newStream = File.OpenRead(newPath);

                var delta = await ds.CreateDeltaAsync(baseStream, newStream);
                delta.Seek(0, SeekOrigin.Begin);

                // Apply delta
                baseStream.Seek(0, SeekOrigin.Begin);
                var reconstructed = await ds.ApplyDeltaAsync(baseStream, delta);
                reconstructed.Seek(0, SeekOrigin.Begin);

                using var ms = new MemoryStream();
                await reconstructed.CopyToAsync(ms);
                var got = ms.ToArray();

                CollectionAssert.AreEqual(newContent, got);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }
    }
}
