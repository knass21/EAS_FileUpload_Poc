using System.Net.Http.Headers;
using EAS_FIleupload_Poc;
using EAS_FIleupload_Poc.Data;
using EAS_FIleupload_Poc.FileStorage;
using EAS_FIleupload_Poc.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

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
builder.Services.AddProblemDetails();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => { c.SwaggerEndpoint("/swagger/v1/swagger.json", "File API V1"); });
}

app.UseStatusCodePages();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseExceptionHandler();

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
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
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
