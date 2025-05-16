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

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = null;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "File API", Version = "v1" });
});
builder.Services.AddScoped<FileStorageService>();
builder.Services.AddScoped<HashingService>();
builder.Services.AddLogging();
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "File API V1");
    });
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

// Download (streaming, headers first)
app.MapGet("/download/{id:guid}",
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


app.MapGet("/filehashes/{id:guid}",
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


using (var scope = app.Services.CreateScope())
{
    var fileDbContext = scope.ServiceProvider.GetRequiredService<FileDbContext>();
    fileDbContext.Database.EnsureCreated();
}

app.Run();

public record FileUploadResponse(Guid Id, string Sha256Hash, long FileSize);
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
    public FileDbContext(DbContextOptions<FileDbContext> options) : base(options) { }
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
        _logger.LogInformation("Finished upload of file {FileName} ({FileSize} bytes) in {ElapsedMs} ms", fileName, totalBytes, stopwatch.ElapsedMilliseconds);

        return new FileUploadResponse(record.Id, record.Sha256Hash, record.FileSize);
    }

    // Get metadata only (for headers)
    public async Task<FileRecord?> GetFileMetadataAsync(Guid fileId, CancellationToken cancellationToken)
    {
        return await _db.Files.FindAsync([fileId], cancellationToken);
    }

    // Streaming download: write directly to output stream (e.g. response.Body)
    public async Task DownloadToStreamAsync(
        Guid fileId, Stream output, Action<byte[], int>? onChunk = null, CancellationToken cancellationToken = default)
    {
        var file = await _db.Files.FindAsync([fileId], cancellationToken);
        if (file == null) return;

        var connStr = _config.GetConnectionString("DefaultConnection")!;
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        var openCmd = new NpgsqlCommand("SELECT lo_open(@oid, 262144)", conn, (NpgsqlTransaction)tx);
        openCmd.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, file.LargeObjectOid);
        var fd = (int)(await openCmd.ExecuteScalarAsync(cancellationToken))!;

        const int bufferLength = 81920;
        while (true)
        {
            var readCmd = new NpgsqlCommand("SELECT loread(@fd, @len)", conn, (NpgsqlTransaction)tx);
            readCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, fd);
            readCmd.Parameters.AddWithValue("len", NpgsqlDbType.Integer, bufferLength);

            var result = await readCmd.ExecuteScalarAsync(cancellationToken);
            if (result is not byte[] chunk || chunk.Length == 0)
                break;

            await output.WriteAsync(chunk, 0, chunk.Length, cancellationToken);
            onChunk?.Invoke(chunk, chunk.Length);
        }

        var closeCmd = new NpgsqlCommand("SELECT lo_close(@fd)", conn, (NpgsqlTransaction)tx);
        closeCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, fd);
        await closeCmd.ExecuteNonQueryAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
        await output.FlushAsync(cancellationToken);
    }
    
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
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

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

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
