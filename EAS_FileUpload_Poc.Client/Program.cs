// Program.cs

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

var fileName = "video.mp4";
var directory = "directoryPath";
var filePath = Path.Combine(directory, fileName);
var host = "http://localhost:5044";
var uploadUrl = $"{host}/files?fileName={fileName}&contentType=video/mp4";

using var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromMinutes(10)
};

var fileInfo = new FileInfo(filePath);
var totalBytes = fileInfo.Length;
var uploadedBytes = 0L;
var stopwatch = Stopwatch.StartNew();

await using var fileStream = File.OpenRead(filePath);

var progressStream = new ProgressStream(fileStream, (bytesRead) =>
{
    uploadedBytes += bytesRead;
    var percent = uploadedBytes * 100 / totalBytes;
    var elapsed = stopwatch.Elapsed;
    var speed = uploadedBytes / elapsed.TotalSeconds; // bytes/sec
    var remainingBytes = totalBytes - uploadedBytes;
    var estimatedRemaining = TimeSpan.FromSeconds(remainingBytes / speed);

    Console.Write(@$"\rUploaded {uploadedBytes} of {totalBytes} bytes ({percent}%) | Speed: {FormatBytes(speed)}/s | ETA: {estimatedRemaining:mm\:ss}");
    Console.CursorLeft = 0;
});

using var content = new StreamContent(progressStream);
content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

Console.WriteLine("Uploading file...");
Console.WriteLine();
var response = await httpClient.PostAsync(uploadUrl, content);
Console.WriteLine(); // finish progress line

if (!response.IsSuccessStatusCode)
{
    Console.WriteLine($"Upload failed: {response.StatusCode}");
    Console.WriteLine(await response.Content.ReadAsStringAsync());
    return;
}

var result = await response.Content.ReadFromJsonAsync<FileUploadResponse>();
Console.WriteLine($"Upload successful:");
Console.WriteLine(result);
Console.WriteLine($"Completed in {stopwatch.Elapsed.TotalSeconds:F2} seconds.");


string downloadUrl = $"{host}/download/{result.Id}"; // Replace {FILE_ID} with actual GUID
var fileId = result.Id;
string downloadUrl = $"{host}/files/{fileId}"; // Replace {FILE_ID} with actual GUID
string outputPath = Path.Combine(directory,$"Copy_{fileName}");


Console.WriteLine("Starting download...");
response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
response.EnsureSuccessStatusCode();

await using var stream = await response.Content.ReadAsStreamAsync();
await using var file = File.Create(outputPath);

var buffer = new byte[81920];
long totalRead = 0;
stopwatch = Stopwatch.StartNew();
var lastLoggedBytes = 0L;
var lastLogTime = DateTime.UtcNow;

int bytesRead;
while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
{
    await file.WriteAsync(buffer.AsMemory(0, bytesRead));
    totalRead += bytesRead;

    var now = DateTime.UtcNow;
    if ((now - lastLogTime).TotalSeconds >= 1)
    {
        var seconds = (now - lastLogTime).TotalSeconds;
        var speed = (totalRead - lastLoggedBytes) / seconds;
        Console.Write($"\rDownloaded {totalRead} bytes | Speed: {FormatBytes(speed)}/s");
        Console.CursorLeft = 0;
        lastLoggedBytes = totalRead;
        lastLogTime = now;
    }
}

stopwatch.Stop();
Console.WriteLine($"\nDownload complete. Saved to {outputPath} in {stopwatch.Elapsed.TotalSeconds:F2} seconds.");

return;

static string FormatBytes(double bytes)
{
    string[] sizes = { "B", "KB", "MB", "GB" };
    int order = 0;
    while (bytes >= 1024 && order < sizes.Length - 1)
    {
        order++;
        bytes /= 1024;
    }
    return $"{bytes:0.##} {sizes[order]}";
}

public record FileUploadResponse(Guid Id, string Sha256Hash, long FileSize);
public class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long> _progress;

    public ProgressStream(Stream inner, Action<long> progress)
    {
        _inner = inner;
        _progress = progress;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        _progress(read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        _progress(read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        _progress(read);
        return read;
    }
}