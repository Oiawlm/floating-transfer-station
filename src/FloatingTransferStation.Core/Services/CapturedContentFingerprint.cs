using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FloatingTransferStation.Services;

public enum CapturedContentKind
{
    Text = 0,
    Image = 1,
}

/// <summary>
/// Identifies captured content by value so that back-to-back clipboard
/// notifications carrying the same content (for example a source app writing
/// bitmap and file representations in separate steps) can be recognized as one
/// logical copy. Image fingerprints hash the normalized pixels, not the
/// encoded file, so identical pixels stay identical across encoders.
/// </summary>
public readonly record struct CapturedContentFingerprint(CapturedContentKind Kind, string Hash)
{
    public static CapturedContentFingerprint ForText(string text) => new(
        CapturedContentKind.Text,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));

    public static CapturedContentFingerprint? ForImageFile(string absolutePath)
    {
        try
        {
            using var image = Image.Load<Rgba32>(absolutePath);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(header, image.Width);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], image.Height);
            hash.AppendData(header);
            image.ProcessPixelRows(accessor =>
            {
                for (var rowIndex = 0; rowIndex < accessor.Height; rowIndex++)
                {
                    hash.AppendData(MemoryMarshal.AsBytes(accessor.GetRowSpan(rowIndex)));
                }
            });
            return new CapturedContentFingerprint(
                CapturedContentKind.Image,
                Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fingerprinting is best effort: any unreadable or undecodable file
            // simply opts the capture out of duplicate suppression.
            return null;
        }
    }
}
