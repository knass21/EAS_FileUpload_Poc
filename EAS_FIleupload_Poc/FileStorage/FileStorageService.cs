using System.Security.Cryptography;
using EAS_FIleupload_Poc.Data;
using Npgsql;
using NpgsqlTypes;

namespace EAS_FIleupload_Poc.FileStorage;

public class FileStorageService
{
    private readonly FileDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<FileStorageService> _logger;

    public FileStorageService(FileDbContext db, IConfiguration config, ILogger<FileStorageService> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    public async Task<FileUploadResponse> UploadAsync(
        Stream inputStream, string fileName, string contentType, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("Starting upload of file: {FileName}", fileName);

        var connStr = _config.GetConnectionString("DefaultConnection")!;
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        var createCmd = new NpgsqlCommand("SELECT lo_create(0)", conn, (NpgsqlTransaction)tx);
        var oid = (uint)(await createCmd.ExecuteScalarAsync(cancellationToken))!;

        var openCmd = new NpgsqlCommand("SELECT lo_open(@oid, 131072)", conn, (NpgsqlTransaction)tx);
        openCmd.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, oid);
        var fd = (int)(await openCmd.ExecuteScalarAsync(cancellationToken))!;

        var buffer = new byte[81920];
        int bytesRead;
        long totalBytes = 0;
        using var sha256 = SHA256.Create();

        while ((bytesRead = await inputStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            var writeCmd = new NpgsqlCommand("SELECT lowrite(@fd, @data)", conn, (NpgsqlTransaction)tx);
            writeCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, fd);
            writeCmd.Parameters.AddWithValue("data", NpgsqlDbType.Bytea, buffer[..bytesRead]);
            await writeCmd.ExecuteNonQueryAsync(cancellationToken);

            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            totalBytes += bytesRead;
        }

        sha256.TransformFinalBlock([], 0, 0);
        var hash = ConvertToHexStringLower(sha256.Hash!);

        var closeCmd = new NpgsqlCommand("SELECT lo_close(@fd)", conn, (NpgsqlTransaction)tx);
        closeCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, fd);
        await closeCmd.ExecuteNonQueryAsync(cancellationToken);

        var record = new FileRecord
        {
            Id = Guid.NewGuid(),
            FileName = fileName,
            ContentType = contentType,
            FileSize = totalBytes,
            Sha256Hash = hash,
            LargeObjectOid = oid,
            UploadedAt = DateTime.UtcNow
        };

        _db.Files.Add(record);
        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation("Finished upload of file {FileName} ({FileSize} bytes) in {ElapsedMs} ms", fileName,
            totalBytes, stopwatch.ElapsedMilliseconds);

        return new FileUploadResponse(record.Id, record.FileName, record.Sha256Hash, record.FileSize);
    }

    public async Task<List<FileUploadResponse>> UploadManyAsync(
        IEnumerable<(Stream InputStream, string FileName, string ContentType)> files,
        CancellationToken cancellationToken)
    {
        var responses = new List<FileUploadResponse>();
        var connStr = _config.GetConnectionString("DefaultConnection")!;
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        foreach (var (inputStream, fileName, contentType) in files)
        {
            var createCmd = new NpgsqlCommand("SELECT lo_create(0)", conn, (NpgsqlTransaction)tx);
            var oid = (uint)(await createCmd.ExecuteScalarAsync(cancellationToken))!;

            var openCmd = new NpgsqlCommand("SELECT lo_open(@oid, 131072)", conn, (NpgsqlTransaction)tx);
            openCmd.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, oid);
            var fd = (int)(await openCmd.ExecuteScalarAsync(cancellationToken))!;

            var buffer = new byte[81920];
            int bytesRead;
            long totalBytes = 0;
            using var sha256 = SHA256.Create();

            while ((bytesRead = await inputStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                var writeCmd = new NpgsqlCommand("SELECT lowrite(@fd, @data)", conn, (NpgsqlTransaction)tx);
                writeCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, fd);
                writeCmd.Parameters.AddWithValue("data", NpgsqlDbType.Bytea, buffer[..bytesRead]);
                await writeCmd.ExecuteNonQueryAsync(cancellationToken);

                sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
                totalBytes += bytesRead;
            }

            sha256.TransformFinalBlock([], 0, 0);
            var hash = ConvertToHexStringLower(sha256.Hash!);

            var closeCmd = new NpgsqlCommand("SELECT lo_close(@fd)", conn, (NpgsqlTransaction)tx);
            closeCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, fd);
            await closeCmd.ExecuteNonQueryAsync(cancellationToken);

            var record = new FileRecord
            {
                Id = Guid.NewGuid(),
                FileName = fileName,
                ContentType = contentType,
                FileSize = totalBytes,
                Sha256Hash = hash,
                LargeObjectOid = oid,
                UploadedAt = DateTime.UtcNow
            };

            _db.Files.Add(record);
            responses.Add(new FileUploadResponse(record.Id, record.FileName, record.Sha256Hash, record.FileSize));
        }

        await _db.SaveChangesAsync(cancellationToken); // Save all at once
        await tx.CommitAsync(cancellationToken); // Commit the whole batch

        return responses;
    }


    // Get metadata only (for headers)
    public async Task<FileRecord?> GetFileMetadataAsync(Guid fileId, CancellationToken cancellationToken)
    {
        return await _db.Files.FindAsync([fileId], cancellationToken);
    }

    // Streaming download: write directly to output stream (e.g. response.Body)

    public async Task<Stream?> OpenFileStreamAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var file = await _db.Files.FindAsync([fileId], cancellationToken);
        if (file == null) return null;

        var connStr = _config.GetConnectionString("DefaultConnection")!;
        var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(cancellationToken);
        var tx = await conn.BeginTransactionAsync(cancellationToken);

        var openCmd = new NpgsqlCommand("SELECT lo_open(@oid, 262144)", conn, (NpgsqlTransaction)tx);
        openCmd.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, file.LargeObjectOid);
        var fd = (int)(await openCmd.ExecuteScalarAsync(cancellationToken))!;

        return new LargeObjectDbStream(conn, tx, fd);
    }

    public static string ConvertToHexStringLower(byte[] hash)
    {
        return Convert.ToHexStringLower(hash);
    }
}