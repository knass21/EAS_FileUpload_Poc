using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Npgsql;
using NpgsqlTypes;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<FileDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.WebHost.ConfigureKestrel(options => { options.Limits.MaxRequestBodySize = null; });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => { c.SwaggerDoc("v1", new OpenApiInfo { Title = "File API", Version = "v1" }); });
builder.Services.AddScoped<FileStorageService>();
builder.Services.AddScoped<HashingService>();
builder.Services.AddScoped<FFProbeMetadataService>();
builder.Services.AddScoped<ExifMetadataService>();
builder.Services.AddLogging();
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => { c.SwaggerEndpoint("/swagger/v1/swagger.json", "File API V1"); });
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// Upload endpoint
app.MapPost("/upload", async (HttpRequest request, [FromServices] FileStorageService fileService,
        [FromQuery] string fileName, [FromQuery] string contentType, CancellationToken token) =>
    {
        var result = await fileService.UploadAsync(request.Body, fileName, contentType, token);
        return Results.Ok(result);
    })
    .WithName("UploadFile")
    .DisableAntiforgery();

// Upload with Swagger
app.MapPost("/upload-swagger",
        async ([FromServices] FileStorageService fileService,
            IFormFile file, CancellationToken token) =>
        {
            await using var stream = file.OpenReadStream();
            var result = await fileService.UploadAsync(stream, file.FileName, file.ContentType, token);
            return Results.Ok(result);
        })
    .WithName("UploadFileSwagger")
    .DisableAntiforgery();

// Multi-file upload endpoint (Swagger/form-data)
app.MapPost("/upload-multi-swagger",
    async ([FromServices] FileStorageService fileService,
        [FromForm] IFormFileCollection files,
        CancellationToken token) =>
    {
        if (files == null || files.Count == 0)
            return Results.BadRequest("No files uploaded.");

        var uploads = new List<(Stream, string, string)>();
        foreach (var file in files)
            uploads.Add((file.OpenReadStream(), file.FileName, file.ContentType));

        var result = await fileService.UploadManyAsync(uploads, token);
        foreach (var (stream, _, _) in uploads) 
            stream.Dispose();
        return Results.Ok(result);
    })
    .Accepts<IFormFileCollection>("multipart/form-data")
    .WithName("UploadFilesMultiSwagger")
    .DisableAntiforgery();

// Download (streaming, headers first)
app.MapGet("/files/{id:guid}",
        async ([FromServices] FileStorageService fileService,
            Guid id,
            HttpResponse response,
            CancellationToken token) =>
        {
            var fileRecord = await fileService.GetFileMetadataAsync(id,
                token);
            if (fileRecord == null)
                return Results.NotFound("File not found");

            response.ContentType = fileRecord.ContentType;
            response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileNameStar = fileRecord.FileName
            }.ToString();

            await using var stream = await fileService.OpenFileStreamAsync(id, token);
            if (stream == null)
                return Results.NotFound("File not found");
            await stream.CopyToAsync(response.Body, token);
            return Results.Empty;
        })
    .WithName("DownloadFile")
    .DisableAntiforgery();


app.MapGet("/files/{id:guid}/hashes",
        async ([FromServices] FileStorageService fileService,
            [FromServices] HashingService hashingService,
            Guid id,
            CancellationToken token) =>
        {
            await using var stream = await fileService.OpenFileStreamAsync(id, token);
            if (stream == null)
                return Results.NotFound("File not found");

            var (sha256, sha1, md5) = hashingService.ComputeHashes(stream);
            return Results.Ok(new FileHashesResponse(sha256, sha1, md5));
        })
    .WithName("GetFileHashes")
    .DisableAntiforgery();

app.MapGet("/files/{id:guid}/metadata/ffprobe",
        async ([FromServices] FileStorageService fileService,
            [FromServices] FFProbeMetadataService metadataService,
            Guid id,
            CancellationToken token) =>
        {
            var fileRecord = await fileService.GetFileMetadataAsync(id, token);
            if (fileRecord == null)
                return Results.NotFound("File not found");

            // Use extension from filename if possible, fallback to ".bin"
            string extension = Path.GetExtension(fileRecord.FileName);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".bin";

            await using var stream = await fileService.OpenFileStreamAsync(id, token);
            if (stream == null)
                return Results.NotFound("File not found");

            try
            {
                string metadataJson = await metadataService.ExtractMetadataAsync(stream, extension, token);
                return Results.Ok(System.Text.Json.JsonDocument.Parse(metadataJson));
            }
            catch (Exception ex)
            {
                return Results.Problem($"Metadata extraction failed: {ex.Message}");
            }
        })
    .WithName("GetVideoMetadata")
    .DisableAntiforgery();

app.MapGet("/files/{id:guid}/metadata/Exif",
        async ([FromServices] FileStorageService fileService,
            [FromServices] ExifMetadataService metadataService,
            Guid id,
            CancellationToken token) =>
        {
            var fileRecord = await fileService.GetFileMetadataAsync(id, token);
            if (fileRecord == null)
                return Results.NotFound("File not found");

            // Use extension from filename if possible, fallback to ".bin"
            string extension = Path.GetExtension(fileRecord.FileName);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".bin";

            await using var stream = await fileService.OpenFileStreamAsync(id, token);
            if (stream == null)
                return Results.NotFound("File not found");

            try
            {
                string metadataJson = await metadataService.ExtractMetadataAsync(stream, extension, token);
                return Results.Ok(System.Text.Json.JsonDocument.Parse(metadataJson));
            }
            catch (Exception ex)
            {
                return Results.Problem($"Metadata extraction failed: {ex.Message}");
            }
        })
    .WithName("GetVideoMetadata Exif")
    .DisableAntiforgery();


using (var scope = app.Services.CreateScope())
{
    var fileDbContext = scope.ServiceProvider.GetRequiredService<FileDbContext>();
    fileDbContext.Database.EnsureCreated();
}

app.Run();

public record FileUploadResponse(Guid Id, string FileName, string Sha256Hash, long FileSize);
public record FileHashesResponse(string SHA256, string SHA1, string MD5);

public class FileRecord
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Sha256Hash { get; set; } = string.Empty;
    public uint LargeObjectOid { get; set; }
    public DateTime UploadedAt { get; set; }
}

public class FileDbContext : DbContext
{
    public FileDbContext(DbContextOptions<FileDbContext> options) : base(options)
    {
    }

    public DbSet<FileRecord> Files => Set<FileRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileRecord>().ToTable("files");
    }
}

public class HashingService
{
    public (string Sha256, string Sha1, string Md5) ComputeHashes(Stream stream)
    {
        using var sha256 = SHA256.Create();
        using var sha1 = SHA1.Create();
        using var md5 = MD5.Create();
        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            sha1.TransformBlock(buffer, 0, bytesRead, null, 0);
            md5.TransformBlock(buffer, 0, bytesRead, null, 0);
        }

        sha256.TransformFinalBlock([], 0, 0);
        sha1.TransformFinalBlock([], 0, 0);
        md5.TransformFinalBlock([], 0, 0);
        return (
            FileStorageService.ConvertToHexStringLower(sha256.Hash!),
            FileStorageService.ConvertToHexStringLower(sha1.Hash!),
            FileStorageService.ConvertToHexStringLower(md5.Hash!)
        );
    }
}

public class ExifMetadataService
{
    public async Task<string> ExtractMetadataAsync(Stream videoStream, string fileExtension,
        CancellationToken cancellationToken)
    {
        // Save to temp file (because ffprobe doesn't support stdin easily for most formats)
        var tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + fileExtension);
        await using (var fs = File.Create(tempFile))
        {
            await videoStream.CopyToAsync(fs, cancellationToken);
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "executeables/exiftool-13.06_64/exiftool.exe",
                Arguments = $"-json -g1 \"{tempFile}\"", // Use JSON output for better parsing
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi)!;
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            string error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
                throw new Exception($"exiftool failed: {error}");

            return output; // Return raw JSON. You can also parse and map to a DTO if needed!
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}

public class FFProbeMetadataService
{
    public async Task<string> ExtractMetadataAsync(Stream videoStream, string fileExtension,
        CancellationToken cancellationToken)
    {
        // Save to temp file (because ffprobe doesn't support stdin easily for most formats)
        var tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + fileExtension);
        await using (var fs = File.Create(tempFile))
        {
            await videoStream.CopyToAsync(fs, cancellationToken);
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v quiet -print_format json -show_format -show_streams \"{tempFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi)!;
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            string error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
                throw new Exception($"ffprobe failed: {error}");

            return output; // Return raw JSON. You can also parse and map to a DTO if needed!
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}


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


public class LargeObjectDbStream : Stream
{
    private readonly NpgsqlConnection _conn;
    private readonly NpgsqlTransaction _tx;
    private readonly int _fd;
    private bool _disposed = false;

    public LargeObjectDbStream(NpgsqlConnection conn, NpgsqlTransaction tx, int fd)
    {
        _conn = conn;
        _tx = tx;
        _fd = fd;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var readCmd = new NpgsqlCommand("SELECT loread(@fd, @len)", _conn, _tx);
        readCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, _fd);
        readCmd.Parameters.AddWithValue("len", NpgsqlDbType.Integer, count);

        var result = readCmd.ExecuteScalar();
        if (result is not byte[] chunk || chunk.Length == 0)
            return 0;

        Array.Copy(chunk, 0, buffer, offset, chunk.Length);
        return chunk.Length;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var readCmd = new NpgsqlCommand("SELECT loread(@fd, @len)", _conn, _tx);
        readCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, _fd);
        readCmd.Parameters.AddWithValue("len", NpgsqlDbType.Integer, buffer.Length);

        var result = await readCmd.ExecuteScalarAsync(cancellationToken);
        if (result is not byte[] chunk || chunk.Length == 0)
            return 0;

        chunk.CopyTo(buffer[..chunk.Length]);
        return chunk.Length;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            var closeCmd = new NpgsqlCommand("SELECT lo_close(@fd)", _conn, _tx);
            closeCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, _fd);
            closeCmd.ExecuteNonQuery();
            _tx.Commit();
            _conn.Dispose();
            _disposed = true;
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            var closeCmd = new NpgsqlCommand("SELECT lo_close(@fd)", _conn, _tx);
            closeCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, _fd);
            await closeCmd.ExecuteNonQueryAsync();
            await _tx.CommitAsync();
            await _conn.DisposeAsync();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
        await base.DisposeAsync();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}