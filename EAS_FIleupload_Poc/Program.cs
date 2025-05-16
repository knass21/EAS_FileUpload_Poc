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


// Add services to the container.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "File API", Version = "v1" });
    c.MapType<IFormFile>(() => new OpenApiSchema { Type = "string", Format = "binary" });
});
var app = builder.Build();

// Enable Swagger middleware
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "File API V1");
    });
}

app.UseHttpsRedirection();


// Production: streamed file upload (recommended for large files)
app.MapPost("/upload", async (HttpRequest request, FileDbContext db, IConfiguration config,
                               [FromQuery] string fileName, [FromQuery] string contentType) =>
{
    var connStr = config.GetConnectionString("DefaultConnection")!;
    await using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    var createCmd = new NpgsqlCommand("SELECT lo_create(0)", conn, (NpgsqlTransaction)tx);
    var oid = (uint)(await createCmd.ExecuteScalarAsync())!;

    var openCmd = new NpgsqlCommand("SELECT lo_open(@oid, 131072)", conn, (NpgsqlTransaction)tx);
    openCmd.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, oid);
    var fd = (int)(await openCmd.ExecuteScalarAsync())!;

    var buffer = new byte[81920];
    int bytesRead;
    long totalBytes = 0;
    using var sha256 = SHA256.Create();

    while ((bytesRead = await request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
    {
        var writeCmd = new NpgsqlCommand("SELECT lowrite(@fd, @data)", conn, (NpgsqlTransaction)tx);
        writeCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, fd);
        writeCmd.Parameters.AddWithValue("data", NpgsqlDbType.Bytea, buffer[..bytesRead]);
        await writeCmd.ExecuteNonQueryAsync();

        sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
        totalBytes += bytesRead;
    }

    sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
    var hash = BitConverter.ToString(sha256.Hash!).Replace("-", "").ToLowerInvariant();

    var closeCmd = new NpgsqlCommand("SELECT lo_close(@fd)", conn, (NpgsqlTransaction)tx);
    closeCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, fd);
    await closeCmd.ExecuteNonQueryAsync();

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

    db.Files.Add(record);
    await db.SaveChangesAsync();
    await tx.CommitAsync();

    return Results.Ok(new { record.Id, record.Sha256Hash, record.FileSize });
})
.WithName("UploadFile")
.WithOpenApi();

// Swagger/demo: file upload with IFormFile (limited file size)
app.MapPost("/upload-swagger",
    async (HttpRequest request, FileDbContext db, IConfiguration config,
           [FromQuery] string fileName, [FromQuery] string contentType,
           [FromForm] IFormFile file) =>
{
    var connStr = config.GetConnectionString("DefaultConnection")!;
    await using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    var createCmd = new NpgsqlCommand("SELECT lo_create(0)", conn, (NpgsqlTransaction)tx);
    var oid = (uint)(await createCmd.ExecuteScalarAsync())!;

    var openCmd = new NpgsqlCommand("SELECT lo_open(@oid, 131072)", conn, (NpgsqlTransaction)tx);
    openCmd.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, oid);
    var fd = (int)(await openCmd.ExecuteScalarAsync())!;

    var buffer = new byte[81920];
    int bytesRead;
    long totalBytes = 0;
    using var sha256 = SHA256.Create();
    await using var stream = file.OpenReadStream();

    while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
    {
        var writeCmd = new NpgsqlCommand("SELECT lowrite(@fd, @data)", conn, (NpgsqlTransaction)tx);
        writeCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, fd);
        writeCmd.Parameters.AddWithValue("data", NpgsqlDbType.Bytea, buffer[..bytesRead]);
        await writeCmd.ExecuteNonQueryAsync();

        sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
        totalBytes += bytesRead;
    }

    sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
    var hash = BitConverter.ToString(sha256.Hash!).Replace("-", "").ToLowerInvariant();

    var closeCmd = new NpgsqlCommand("SELECT lo_close(@fd)", conn, (NpgsqlTransaction)tx);
    closeCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, fd);
    await closeCmd.ExecuteNonQueryAsync();

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

    db.Files.Add(record);
    await db.SaveChangesAsync();
    await tx.CommitAsync();

    return Results.Ok(new { record.Id, record.Sha256Hash, record.FileSize });
})
.Accepts<IFormFile>("multipart/form-data")
.WithName("UploadFileSwagger")
.WithOpenApi();

app.MapGet("/download/{id:guid}", async (Guid id, FileDbContext db, IConfiguration config, HttpResponse response) =>
{
    var file = await db.Files.FindAsync(id);
    if (file == null) return Results.NotFound("File not found");

    var connStr = config.GetConnectionString("DefaultConnection")!;
    await using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    var openCmd = new NpgsqlCommand("SELECT lo_open(@oid, 262144)", conn, (NpgsqlTransaction)tx); // INV_READ
    openCmd.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, file.LargeObjectOid);
    var fd = (int)(await openCmd.ExecuteScalarAsync())!;

    var buffer = new byte[81920];
    using var sha256 = SHA256.Create();

    response.ContentType = file.ContentType;
    response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
    {
        FileNameStar = file.FileName
    }.ToString();

    while (true)
    {
        var readCmd = new NpgsqlCommand("SELECT loread(@fd, @len)", conn, (NpgsqlTransaction)tx);
        readCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, fd);
        readCmd.Parameters.AddWithValue("len", NpgsqlDbType.Integer, buffer.Length);

        var result = await readCmd.ExecuteScalarAsync();
        if (result is not byte[] chunk || chunk.Length == 0)
            break;

        await response.Body.WriteAsync(chunk);
        sha256.TransformBlock(chunk, 0, chunk.Length, null, 0);
    }

    sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
    var computed = BitConverter.ToString(sha256.Hash!).Replace("-", "").ToLowerInvariant();

    if (computed != file.Sha256Hash)
        Console.Error.WriteLine($"⚠️ Hash mismatch for file {file.FileName}! {computed} != {file.Sha256Hash}");

    var closeCmd = new NpgsqlCommand("SELECT lo_close(@fd)", conn, (NpgsqlTransaction)tx);
    closeCmd.Parameters.AddWithValue("fd", NpgsqlDbType.Integer, fd);
    await closeCmd.ExecuteNonQueryAsync();

    await tx.CommitAsync();
    return Results.Ok();
})
.WithName("DownloadFile")
.WithOpenApi();


using (var scope = app.Services.CreateScope())
{
    var fileDbContext = scope.ServiceProvider.GetRequiredService<FileDbContext>();
    fileDbContext.Database.EnsureDeleted();
    fileDbContext.Database.EnsureCreated();
}

app.Run();

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
