/*
 *                     GNU AFFERO GENERAL PUBLIC LICENSE
 *                       Version 3, 19 November 2007
 *  Copyright (C) 2007 Free Software Foundation, Inc. <https://fsf.org/>
 *  Everyone is permitted to copy and distribute verbatim copies
 *  of this license document, but changing it is not allowed.
 */

using Emgu.CV;
using Emgu.CV.CvEnum;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using UVtools.Core.Extensions;
using UVtools.Core.Layers;

namespace UVtools.Core.EmguCV;

public abstract class MatCompressor
{
    /// <summary>
    /// Compresses the <see cref="Mat"/> into a byte array.
    /// </summary>
    /// <param name="src"></param>
    /// <param name="argument"></param>
    /// <returns></returns>
    public abstract byte[] Compress(Mat src, object? argument = null);

    /// <summary>
    /// Writes Mat data to a stream, handling both continuous and non-continuous matrices efficiently.
    /// </summary>
    protected void CompressToStream(Mat src, Stream stream, object? argument = null)
    {
        if (src.IsContinuous)
        {
            stream.Write(src.GetDataByteReadOnlySpan());
        }
        else
        {
            // Non-continuous Mat: write row by row
            var span2d = src.GetDataByteReadOnlySpan2D();
            for (var row = 0; row < span2d.Height; row++)
            {
                stream.Write(span2d.GetRowSpan(row));
            }
        }
    }

    /// <summary>
    /// Decompresses the <see cref="Mat"/> from a byte array.
    /// </summary>
    /// <param name="compressedBytes"></param>
    /// <param name="dst"></param>
    /// <param name="argument"></param>
    public abstract void Decompress(byte[] compressedBytes, Mat dst, object? argument = null);

    /// <summary>
    /// Compresses the <see cref="Mat"/> into a byte array asynchronously.
    /// </summary>
    public Task<byte[]> CompressAsync(Mat src, object? argument = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Compress(src, argument), cancellationToken);
    }

    /// <summary>
    /// Decompresses the <see cref="Mat"/> from a byte array asynchronously.
    /// </summary>
    public Task DecompressAsync(byte[] compressedBytes, Mat dst, object? argument = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Decompress(compressedBytes, dst, argument), cancellationToken);
    }
}

#region None
public sealed class MatCompressorNone : MatCompressor
{
    public static readonly MatCompressorNone Instance = new();

    private MatCompressorNone() { }

    public override byte[] Compress(Mat src, object? argument = null)
    {
        return src.GetBytes();
    }

    public override void Decompress(byte[] compressedBytes, Mat dst, object? argument = null)
    {
        dst.SetBytes(compressedBytes);
    }

    public override string ToString() => "None";
}
#endregion

#region PNG
public sealed class MatCompressorPng : MatCompressor
{
    public static readonly MatCompressorPng Instance = new();

    private MatCompressorPng() { }

    public override byte[] Compress(Mat src, object? argument = null)
    {
        return src.GetPngByes();
    }

    public override void Decompress(byte[] compressedBytes, Mat dst, object? argument = null)
    {
        CvInvoke.Imdecode(compressedBytes, ImreadModes.Unchanged, dst);
    }
}

public sealed class MatCompressorPngGreyScale : MatCompressor
{
    public static readonly MatCompressorPngGreyScale Instance = new();

    private MatCompressorPngGreyScale() { }

    public override byte[] Compress(Mat src, object? argument = null)
    {
        return src.GetPngByes();
    }

    public override void Decompress(byte[] compressedBytes, Mat dst, object? argument = null)
    {
        CvInvoke.Imdecode(compressedBytes, ImreadModes.Grayscale, dst);
    }

    public override string ToString() => "PNG";
}
#endregion

#region Deflate
public sealed class MatCompressorDeflate : MatCompressor
{
    public static readonly MatCompressorDeflate Instance = new();

    private MatCompressorDeflate() { }

    public override byte[] Compress(Mat src, object? argument = null)
    {
        using var compressedStream = StreamExtensions.RecyclableMemoryStreamManager.GetStream();
        using (var deflateStream = new DeflateStream(compressedStream, CoreSettings.LayerCompressionLevel, leaveOpen: true))
        {
            CompressToStream(src, deflateStream, argument);
        }


        return compressedStream.TryGetBuffer(out var buffer)
            ? buffer.ToArray()
            : compressedStream.ToArray();
    }

    public override void Decompress(byte[] compressedBytes, Mat dst, object? argument = null)
    {
        unsafe
        {
            fixed (byte* pBuffer = compressedBytes)
            {
                using var compressedStream = new UnmanagedMemoryStream(pBuffer, compressedBytes.Length);
                using var deflateStream = new DeflateStream(compressedStream, CompressionMode.Decompress, leaveOpen: true);
                deflateStream.ReadExactly(dst.GetDataByteSpan());
            }
        }
    }

    public override string ToString() => "Deflate";
}
#endregion

#region GZip
public sealed class MatCompressorGZip : MatCompressor
{
    public static readonly MatCompressorGZip Instance = new();

    private MatCompressorGZip() { }

    public override byte[] Compress(Mat src, object? argument = null)
    {
        using var compressedStream = StreamExtensions.RecyclableMemoryStreamManager.GetStream();
        using (var gzipStream = new GZipStream(compressedStream, CoreSettings.LayerCompressionLevel, leaveOpen: true))
        {
            CompressToStream(src, gzipStream, argument);
        }

        return compressedStream.TryGetBuffer(out var buffer)
            ? buffer.ToArray()
            : compressedStream.ToArray();
    }

    public override void Decompress(byte[] compressedBytes, Mat dst, object? argument = null)
    {
        unsafe
        {
            fixed (byte* pBuffer = compressedBytes)
            {
                using var compressedStream = new UnmanagedMemoryStream(pBuffer, compressedBytes.Length);
                using var gZipStream = new GZipStream(compressedStream, CompressionMode.Decompress, leaveOpen: true);
                gZipStream.ReadExactly(dst.GetDataByteSpan());
            }
        }
    }

    public override string ToString() => "GZip";
}
#endregion

#region ZLib
public sealed class MatCompressorZLib : MatCompressor
{
    public static readonly MatCompressorZLib Instance = new();

    private MatCompressorZLib() { }

    public override byte[] Compress(Mat src, object? argument = null)
    {
        using var compressedStream = StreamExtensions.RecyclableMemoryStreamManager.GetStream();
        using (var zLibStream = new ZLibStream(compressedStream, CoreSettings.LayerCompressionLevel, leaveOpen: true))
        {
            CompressToStream(src, zLibStream, argument);
        }

        return compressedStream.TryGetBuffer(out var buffer)
            ? buffer.ToArray()
            : compressedStream.ToArray();
    }

    public override void Decompress(byte[] compressedBytes, Mat dst, object? argument = null)
    {
        unsafe
        {
            fixed (byte* pBuffer = compressedBytes)
            {
                using var compressedStream = new UnmanagedMemoryStream(pBuffer, compressedBytes.Length);
                using var zLibStream = new ZLibStream(compressedStream, CompressionMode.Decompress, leaveOpen: true);
                zLibStream.ReadExactly(dst.GetDataByteSpan());
            }
        }
    }

    public override string ToString() => "ZLib";
}
#endregion

#region Brotli
public sealed class MatCompressorBrotli : MatCompressor
{
    public static readonly MatCompressorBrotli Instance = new();

    private MatCompressorBrotli() { }

    public override byte[] Compress(Mat src, object? argument = null)
    {
        using var compressedStream = StreamExtensions.RecyclableMemoryStreamManager.GetStream();
        using (var brotliStream = new BrotliStream(compressedStream, CoreSettings.LayerCompressionLevel, leaveOpen: true))
        {
            CompressToStream(src, brotliStream, argument);
        }

        return compressedStream.TryGetBuffer(out var buffer)
            ? buffer.ToArray()
            : compressedStream.ToArray();
    }

    public override void Decompress(byte[] compressedBytes, Mat dst, object? argument = null)
    {
        unsafe
        {
            fixed (byte* pBuffer = compressedBytes)
            {
                using var compressedStream = new UnmanagedMemoryStream(pBuffer, compressedBytes.Length);
                using var brotliStream = new BrotliStream(compressedStream, CompressionMode.Decompress, leaveOpen: true);
                brotliStream.ReadExactly(dst.GetDataByteSpan());
            }
        }
    }

    public override string ToString() => "Brotli";
}
#endregion

#region LZ4
public sealed class MatCompressorLz4 : MatCompressor
{
    public static readonly MatCompressorLz4 Instance = new();

    private MatCompressorLz4() { }

    private static LZ4Level GetLZ4Level() => CoreSettings.DefaultLayerCompressionLevel switch
    {
        LayerCompressionLevel.Lowest => LZ4Level.L00_FAST,
        LayerCompressionLevel.Highest => LZ4Level.L12_MAX,
        _ => LZ4Level.L10_OPT
    };

    public override byte[] Compress(Mat src, object? argument = null)
    {
        using var compressedStream = StreamExtensions.RecyclableMemoryStreamManager.GetStream();
        using (var lz4Stream = LZ4Stream.Encode(compressedStream, GetLZ4Level(), leaveOpen: true))
        {
            CompressToStream(src, lz4Stream, argument);
        }

        return compressedStream.TryGetBuffer(out var buffer)
            ? buffer.ToArray()
            : compressedStream.ToArray();
    }

    public override void Decompress(byte[] compressedBytes, Mat dst, object? argument = null)
    {
        unsafe
        {
            fixed (byte* pBuffer = compressedBytes)
            {
                using var compressedStream = new UnmanagedMemoryStream(pBuffer, compressedBytes.Length);
                using var lz4Stream = LZ4Stream.Decode(compressedStream, leaveOpen: true);
                lz4Stream.ReadExactly(dst.GetDataByteSpan());
            }
        }
    }

    public override string ToString() => "LZ4";
}
#endregion
