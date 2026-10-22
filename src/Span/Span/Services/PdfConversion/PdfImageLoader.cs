using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Span.Services.PdfConversion;

/// <summary>
/// 이미지 파일(PNG/JPG/BMP/GIF/TIFF/WebP/HEIC 등)을 PDF 에 넣을 수 있는 PNG 또는 JPEG 바이트로 바꾼다.
/// Windows 이미지 디코더를 쓰므로 EXIF 회전이 반영되고, 여러 프레임(GIF/TIFF)은 첫 프레임만 쓴다.
/// </summary>
public static class PdfImageLoader
{
    public static async Task<byte[]> LoadAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(path);
        using IRandomAccessStream input = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);

        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        bool photo = ext is ".jpg" or ".jpeg" or ".jpe" or ".heic" or ".heif";   // 사진은 JPEG 로 (용량), 그 외는 PNG 로 (투명도 유지)
        var alpha = photo ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Straight;

        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, alpha, new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);

        using var output = new InMemoryRandomAccessStream();
        BitmapEncoder encoder;
        if (photo)
        {
            var props = new BitmapPropertySet { { "ImageQuality", new BitmapTypedValue(0.92f, PropertyType.Single) } };
            encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, props);
        }
        else
        {
            encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        }
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, alpha, decoder.OrientedPixelWidth, decoder.OrientedPixelHeight, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync();

        var bytes = new byte[output.Size];
        output.Seek(0);
        await output.ReadAsync(bytes.AsBuffer(), (uint)output.Size, InputStreamOptions.None);
        return bytes;
    }
}
