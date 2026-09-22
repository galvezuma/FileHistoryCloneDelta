using FileHistory;
using FileHistory.Tests;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Runtime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace FileHistoryTests;

[TestClass]
public class CleanUpTests
{
    string _baseDir = "";
    Settings _settings = new Settings();
    BackupDb _db = null!;

    [TestInitialize()]
    public void Initialize()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_baseDir);
        _settings = new Settings { BackupBaseDir = _baseDir };
        _db = new BackupDb(_settings, new LoggerFactoryMock());
    }

    private static async Task WriteCheckoutAsync(
        string fhcPath,
        string contents,
        string originalFileName,
        DateTimeOffset createdAt)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(fhcPath)!);

        var bytes = Encoding.UTF8.GetBytes(contents);

        var metadata = new FhcMetadata
        {
            Type = "checkout",
            BaseBackupId = null,
            Sha256 = string.Empty,
            OriginalFileName = originalFileName,
            OriginalSize = bytes.Length,
            DeltaIndex = 0,
            CreatedAt = createdAt,
            FormatVersion = 1,
        };

        await using var payload = new MemoryStream(bytes);
        await using var output = new FileStream(
            fhcPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        await FhcPackage.CreatePackageAsync(
            payload,
            metadata,
            output);
    }

    private static async Task<string> ReadPayloadAsync(string fhcPath)
    {
        await using var input = new FileStream(
            fhcPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var extracted = await FhcPackage.ExtractPackageAsync(input);

        using var payload = extracted.Payload;
        using var reader = new StreamReader(
            payload,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);

        return await reader.ReadToEndAsync();
    }

    [TestMethod]
    public void BackupFileName_ComposesExpectedFhcPath()
    {
        var dt = new DateTime(2026, 7, 8, 12, 34, 56);

        var result = BackupDb.BackupFileName(
            @"C:\Data",
            @"C:\Demo\Documents\report.txt",
            dt);

        Assert.AreEqual(
            @"C:\Data\C\Demo\Documents\report(2026_07_08 12_34_56).fhc",
            result);
    }

    [TestMethod]
    public void BackupFileName_FileWithoutExtension_StillUsesFhcExtension()
    {
        var dt = new DateTime(2026, 7, 8, 12, 34, 56);

        var result = BackupDb.BackupFileName(
            @"C:\Data",
            @"C:\Demo\Documents\newfile",
            dt);

        Assert.AreEqual(
            @"C:\Data\C\Demo\Documents\newfile(2026_07_08 12_34_56).fhc",
            result);
    }

    [TestMethod]
    public async Task FhcPackage_Checkout_RoundTripsPayloadAndMetadata()
    {
        var fhcPath = Path.Combine(
            _baseDir,
            "checkout.fhc");

        const string contents = "contenido de prueba";
        var createdAt = new DateTimeOffset(
            2026, 9, 21, 10, 0, 0, TimeSpan.Zero);

        await WriteCheckoutAsync(
            fhcPath,
            contents,
            "document.txt",
            createdAt);

        Assert.IsTrue(
            File.Exists(fhcPath),
            $"Package was not created: '{fhcPath}'");

        await using var input = new FileStream(
            fhcPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var extracted = await FhcPackage.ExtractPackageAsync(input);

        Assert.AreEqual("checkout", extracted.Metadata.Type);
        Assert.IsNull(extracted.Metadata.BaseBackupId);
        Assert.AreEqual("document.txt", extracted.Metadata.OriginalFileName);
        Assert.AreEqual(
            Encoding.UTF8.GetByteCount(contents),
            extracted.Metadata.OriginalSize);
        Assert.AreEqual(0, extracted.Metadata.DeltaIndex);

        using var reader = new StreamReader(extracted.Payload);
        var actualContents = await reader.ReadToEndAsync();

        Assert.AreEqual(contents, actualContents);
    }
    private static Task InvokePreserveOnlyLatestAsync(
        CleanupForm form,
        FileDbEntry file,
        string originalFullPath,
        CancellationToken token)
    {
        var method = typeof(CleanupForm).GetMethod(
            "PreserveOnlyLatestRecoverableVersionAsync",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);

        Assert.IsNotNull(
            method,
            "Could not find PreserveOnlyLatestRecoverableVersionAsync.");

        var task = method.Invoke(
            form,
            new object[] { file, originalFullPath, token }) as Task;

        Assert.IsNotNull(
            task,
            "PreserveOnlyLatestRecoverableVersionAsync did not return a Task.");

        return task;
    }

    [TestMethod]
    public async Task Cleanup_PreserveLatest_WhenLatestIsCheckout_DeletesOlderVersions()
    {
        var originalPath = Path.Combine(
            _baseDir,
            "Watch",
            "document.txt");

        Directory.CreateDirectory(
            Path.GetDirectoryName(originalPath)!);

        await File.WriteAllTextAsync(
            originalPath,
            "estado actual");

        var db = _db;

        var file = db.InsertFile(originalPath);

        var oldTime = new DateTime(
            2026, 9, 21, 10, 0, 0);

        var latestTime = oldTime.AddMinutes(10);

        var oldPath = BackupDb.BackupFileName(
            _settings.DataDir,
            originalPath,
            oldTime);

        var latestPath = BackupDb.BackupFileName(
            _settings.DataDir,
            originalPath,
            latestTime);

        await WriteCheckoutAsync(
            oldPath,
            "versión antigua",
            "document.txt",
            new DateTimeOffset(oldTime));

        await WriteCheckoutAsync(
            latestPath,
            "versión actual",
            "document.txt",
            new DateTimeOffset(latestTime));

        var oldAttr = new AttributeFileEntry(originalPath);

        db.InsertAttributeExtended(
            file.Id,
            oldTime,
            oldAttr.CreationTime,
            oldAttr.LastWriteTime,
            oldAttr.LastAccessTime,
            oldAttr.Size,
            "checkout",
            Path.GetRelativePath(_settings.DataDir, oldPath),
            string.Empty,
            null,
            0,
            new DateTimeOffset(oldTime));

        db.InsertAttributeExtended(
            file.Id,
            latestTime,
            oldAttr.CreationTime,
            oldAttr.LastWriteTime,
            oldAttr.LastAccessTime,
            oldAttr.Size,
            "checkout",
            Path.GetRelativePath(_settings.DataDir, latestPath),
            string.Empty,
            null,
            0,
            new DateTimeOffset(latestTime));

        using var form = new CleanupForm(
            _settings,
            db,
            crawler: null!,
            new LoggerFactoryMock());

        await InvokePreserveOnlyLatestAsync(
            form,
            file,
            originalPath,
            CancellationToken.None).ConfigureAwait(false);

        var remaining = db.GetAttributes(file.Id);

        Assert.AreEqual(1, remaining.Count);
        Assert.AreEqual("checkout", remaining[0].Type);
        Assert.AreEqual(latestTime, remaining[0].BackupTime);

        Assert.IsFalse(
            File.Exists(oldPath),
            "The old checkout package should have been deleted.");

        Assert.IsTrue(
            File.Exists(latestPath),
            "The latest checkout package should have been retained.");

        Assert.AreEqual(
            "versión actual",
            await ReadPayloadAsync(latestPath));
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        using var sha256 = SHA256.Create();

        var hash = sha256.ComputeHash(bytes);

        return BitConverter
            .ToString(hash)
            .Replace("-", string.Empty)
            .ToLowerInvariant();
    }

    private static byte[] CreateDeterministicBytes(
        int length,
        int seed)
    {
        var result = new byte[length];

        new Random(seed).NextBytes(result);

        return result;
    }
    private static async Task WriteCheckoutBytesAsync(
        string fhcPath,
        byte[] contents,
        string originalFileName,
        string sha256,
        DateTimeOffset createdAt)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(fhcPath)!);

        var metadata = new FhcMetadata
        {
            Type = "checkout",
            BaseBackupId = null,
            Sha256 = sha256,
            OriginalFileName = originalFileName,
            OriginalSize = contents.Length,
            DeltaIndex = 0,
            CreatedAt = createdAt,
            FormatVersion = 1,
        };

        await using var payload = new MemoryStream(
            contents,
            writable: false);

        await using var output = new FileStream(
            fhcPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        await FhcPackage.CreatePackageAsync(
            payload,
            metadata,
            output,
            CancellationToken.None);
    }

    private static async Task InvokeDeleteAllBackupVersionsAsync(
        CleanupForm form,
        FileDbEntry file,
        CancellationToken token)
    {
        var method = typeof(CleanupForm).GetMethod(
            "DeleteAllBackupVersionsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.IsNotNull(
            method,
            "Could not find DeleteAllBackupVersionsAsync.");

        var task = method.Invoke(
            form,
            new object[]
            {
            file,
            token,
            }) as Task;

        Assert.IsNotNull(
            task,
            "DeleteAllBackupVersionsAsync did not return Task.");

        await task;
    }

    private static async Task<byte[]> ReadPayloadBytesAsync(
    string fhcPath)
    {
        await using var input = new FileStream(
            fhcPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var extracted = await FhcPackage.ExtractPackageAsync(input);

        using var payload = extracted.Payload;

        if (payload.CanSeek)
        {
            payload.Seek(0, SeekOrigin.Begin);
        }

        using var output = new MemoryStream();

        await payload.CopyToAsync(output);

        return output.ToArray();
    }

    [TestMethod]
    public async Task Cleanup_DeleteAllVersions_WhenOriginalDoesNotExist_RemovesPackagesAttributesAndFile()
    {
        var settings = _settings;
        var testRoot = _baseDir;
        var loggerFactory = new LoggerFactoryMock();
        var db = _db;

        var originalPath = Path.Combine(
            testRoot,
            "Watch",
            "deleted-document.txt");

        Directory.CreateDirectory(
            Path.GetDirectoryName(originalPath)!);

        // Crear primero el original para que InsertFile cree la jerarquía
        // correspondiente en la base de datos.
        await File.WriteAllTextAsync(
            originalPath,
            "contenido temporal");

        var file = db.InsertFile(originalPath);

        // El test representa el estado posterior: el fichero original ya no existe.
        File.Delete(originalPath);

        var time1 = new DateTime(2026, 9, 21, 10, 0, 0);
        var time2 = time1.AddMinutes(10);

        var backup1 = BackupDb.BackupFileName(
            settings.DataDir,
            originalPath,
            time1);

        var backup2 = BackupDb.BackupFileName(
            settings.DataDir,
            originalPath,
            time2);

        await WriteCheckoutAsync(
            backup1,
            "versión uno",
            Path.GetFileName(originalPath),
            new DateTimeOffset(time1));

        await WriteCheckoutAsync(
            backup2,
            "versión dos",
            Path.GetFileName(originalPath),
            new DateTimeOffset(time2));

        var sourceAttributes = new AttributeFileEntry(
            backup2);

        db.InsertAttributeExtended(
            file.Id,
            time1,
            sourceAttributes.CreationTime,
            sourceAttributes.LastWriteTime,
            sourceAttributes.LastAccessTime,
            sourceAttributes.Size,
            "checkout",
            Path.GetRelativePath(settings.DataDir, backup1),
            string.Empty,
            null,
            0,
            new DateTimeOffset(time1));

        db.InsertAttributeExtended(
            file.Id,
            time2,
            sourceAttributes.CreationTime,
            sourceAttributes.LastWriteTime,
            sourceAttributes.LastAccessTime,
            sourceAttributes.Size,
            "checkout",
            Path.GetRelativePath(settings.DataDir, backup2),
            string.Empty,
            null,
            0,
            new DateTimeOffset(time2));

        Assert.AreEqual(2, db.GetAttributes(file.Id).Count);
        Assert.IsTrue(File.Exists(backup1));
        Assert.IsTrue(File.Exists(backup2));

        await InvokeDeleteAllBackupVersionsAsync(
            new CleanupForm(
                settings,
                db,
                crawler: null!,
                loggerFactory),
            file,
            CancellationToken.None);

        Assert.AreEqual(
            0,
            db.GetAttributes(file.Id).Count,
            "All backup attribute rows must be removed.");

        Assert.IsFalse(
            File.Exists(backup1),
            "The first .fhc package must be deleted.");

        Assert.IsFalse(
            File.Exists(backup2),
            "The second .fhc package must be deleted.");

        Assert.IsNull(
            db.GetFile(originalPath),
            "The FileDbEntry must be deleted after all attributes are removed.");
    }

    [TestMethod]
    public async Task Cleanup_PreserveLatest_WhenLatestIsDelta_ConsolidatesIntoOneCheckout()
    {
        var settings = _settings;
        var testRoot = _baseDir;
        settings.AllowOctodiffFallback = true;

        var loggerFactory = new LoggerFactoryMock();
        var db = _db;

        var originalPath = Path.Combine(
            testRoot,
            "Watch",
            "delta-document.bin");

        Directory.CreateDirectory(
            Path.GetDirectoryName(originalPath)!);

        // La versión A y B deben diferir lo suficiente para que el motor
        // de delta tenga contenido real que procesar.
        var versionA = CreateDeterministicBytes(
            length: 16 * 1024,
            seed: 101);

        var versionB = (byte[])versionA.Clone();

        for (var index = 1024; index < 3072; index++)
        {
            versionB[index] = (byte)(versionB[index] ^ 0x5A);
        }

        await File.WriteAllBytesAsync(
            originalPath,
            versionA);

        var file = db.InsertFile(originalPath);

        var checkoutTime = new DateTime(
            2026, 9, 21, 10, 0, 0,
            DateTimeKind.Local);

        var deltaTime = checkoutTime.AddMinutes(10);

        var checkoutPath = BackupDb.BackupFileName(
            settings.DataDir,
            originalPath,
            checkoutTime);

        var deltaPath = BackupDb.BackupFileName(
            settings.DataDir,
            originalPath,
            deltaTime);

        // 1. Crear y registrar el checkout A.
        var checkoutChecksum = ComputeSha256Hex(versionA);

        await WriteCheckoutBytesAsync(
            checkoutPath,
            versionA,
            Path.GetFileName(originalPath),
            checkoutChecksum,
            new DateTimeOffset(checkoutTime));

        var attributesA = new AttributeFileEntry(originalPath);

        db.InsertAttributeExtended(
            file.Id,
            checkoutTime,
            attributesA.CreationTime,
            attributesA.LastWriteTime,
            attributesA.LastAccessTime,
            versionA.Length,
            "checkout",
            Path.GetRelativePath(settings.DataDir, checkoutPath),
            checkoutChecksum,
            null,
            0,
            new DateTimeOffset(checkoutTime));

        var checkoutAttribute = db.GetAttributes(file.Id)
            .Single(a => a.BackupTime == checkoutTime);

        Assert.AreEqual("checkout", checkoutAttribute.Type);

        // 2. Crear el delta A → B y guardarlo en un paquete .fhc.
        Stream? deltaStream = null;

        try
        {
            using var deltaService = new DeltaService(
                loggerFactory.CreateLogger<DeltaService>(),
                allowFallback: true);

            await using var baseStream = new MemoryStream(
                versionA,
                writable: false);

            await using var newStream = new MemoryStream(
                versionB,
                writable: false);

            deltaStream = await deltaService.CreateDeltaAsync(
                baseStream,
                newStream,
                CancellationToken.None);

            if (deltaStream.CanSeek)
            {
                deltaStream.Seek(0, SeekOrigin.Begin);
            }

            var deltaChecksum = ComputeSha256Hex(versionB);

            var deltaMetadata = new FhcMetadata
            {
                Type = "delta",
                BaseBackupId = checkoutAttribute.Id.ToString(),
                Sha256 = deltaChecksum,
                OriginalFileName = Path.GetFileName(originalPath),
                OriginalSize = versionB.Length,
                DeltaIndex = 1,
                CreatedAt = new DateTimeOffset(deltaTime),
                FormatVersion = 1,
            };

            Directory.CreateDirectory(
                Path.GetDirectoryName(deltaPath)!);

            await using (var output = new FileStream(
                deltaPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                await FhcPackage.CreatePackageAsync(
                    deltaStream,
                    deltaMetadata,
                    output,
                    CancellationToken.None);
            }

            // Actualizar el fichero original a la versión B antes de registrar
            // sus atributos, igual que ocurriría en un backup real.
            await File.WriteAllBytesAsync(
                originalPath,
                versionB);

            var attributesB = new AttributeFileEntry(originalPath);

            db.InsertAttributeExtended(
                file.Id,
                deltaTime,
                attributesB.CreationTime,
                attributesB.LastWriteTime,
                attributesB.LastAccessTime,
                versionB.Length,
                "delta",
                Path.GetRelativePath(settings.DataDir, deltaPath),
                deltaChecksum,
                checkoutAttribute.Id,
                1,
                new DateTimeOffset(deltaTime));
        }
        finally
        {
            deltaStream?.Dispose();
        }

        var beforeCleanup = db.GetAttributes(file.Id)
            .OrderBy(a => a.BackupTime)
            .ToList();

        Assert.AreEqual(
            2,
            beforeCleanup.Count,
            "The test must start with one checkout and one delta.");

        Assert.AreEqual("checkout", beforeCleanup[0].Type);
        Assert.AreEqual("delta", beforeCleanup[1].Type);
        Assert.AreEqual(
            checkoutAttribute.Id,
            beforeCleanup[1].BaseAttributeId);

        Assert.IsTrue(File.Exists(checkoutPath));
        Assert.IsTrue(File.Exists(deltaPath));

        // 3. Consolidar.
        using var form = new CleanupForm(
            settings,
            db,
            crawler: null!,
            loggerFactory);

        await InvokePreserveOnlyLatestAsync(
            form,
            file,
            originalPath,
            CancellationToken.None).ConfigureAwait(false);

        // 4. Verificar que se conserva solo un checkout autocontenido.
        var remaining = db.GetAttributes(file.Id);

        Assert.AreEqual(
            1,
            remaining.Count,
            "After consolidation there must be exactly one backup attribute.");

        var retained = remaining.Single();

        Assert.AreEqual(
            "checkout",
            retained.Type,
            "The retained version must be a standalone checkout.");

        Assert.IsNull(
            retained.BaseAttributeId,
            "A consolidated checkout cannot depend on another attribute.");

        Assert.AreEqual(
            0,
            retained.DeltaIndex,
            "A consolidated checkout must have DeltaIndex = 0.");

        Assert.AreEqual(
            versionB.Length,
            retained.Size);

        Assert.AreEqual(
            ComputeSha256Hex(versionB),
            retained.Checksum);

        Assert.IsFalse(
            string.IsNullOrWhiteSpace(retained.StoredFileName),
            "The consolidated checkout must record StoredFileName.");

        var retainedPath = Path.Combine(
            settings.DataDir,
            retained.StoredFileName);

        Assert.IsTrue(
            File.Exists(retainedPath),
            $"Consolidated checkout package does not exist: '{retainedPath}'");

        var retainedBytes = await ReadPayloadBytesAsync(
            retainedPath);

        CollectionAssert.AreEqual(
            versionB,
            retainedBytes,
            "The consolidated checkout payload must equal version B.");

        Assert.IsFalse(
            File.Exists(checkoutPath),
            "The old checkout package must be deleted after successful consolidation.");

        Assert.IsFalse(
            File.Exists(deltaPath),
            "The old delta package must be deleted after successful consolidation.");

        Assert.AreNotEqual(
            checkoutPath,
            retainedPath,
            "The consolidated checkout must not overwrite the old checkout.");

        Assert.AreNotEqual(
            deltaPath,
            retainedPath,
            "The consolidated checkout must not overwrite the old delta.");
    }

}
