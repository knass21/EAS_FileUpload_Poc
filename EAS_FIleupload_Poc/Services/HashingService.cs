using System.Security.Cryptography;
using EAS_FIleupload_Poc.FileStorage;

namespace EAS_FIleupload_Poc.Services;

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