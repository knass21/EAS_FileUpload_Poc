namespace EAS_FIleupload_Poc.Services;

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