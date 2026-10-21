using System;
using System.IO;
using System.Threading;
using SkiaSharp;

namespace Uviewer.Services
{
    internal static class ArchiveThumbnailEncoder
    {
        // Archive workers must not create WinRT imaging objects: their COM callbacks can
        // contend with XAML property marshaling while the UI is opening another document.
        public static byte[]? Encode(Stream image, int size, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            image.Position = 0;
            using var input = new SKManagedStream(image, disposeManagedStream: false);
            using var codec = SKCodec.Create(input);
            if (codec == null) return null; // The UI decoder retains the original-format fallback.

            float scale = Math.Min(1f, (float)size / Math.Max(codec.Info.Width, codec.Info.Height));
            var dimensions = codec.GetScaledDimensions(scale);
            using var colorSpace = SKColorSpace.CreateSrgb();
            var info = new SKImageInfo(dimensions.Width, dimensions.Height, SKColorType.Bgra8888, SKAlphaType.Premul, colorSpace);
            using var decoded = new SKBitmap(info);
            if (codec.GetPixels(info, decoded.GetPixels()) != SKCodecResult.Success) return null;
            token.ThrowIfCancellationRequested();

            bool rotated = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or
                SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            double outputScale = Math.Min(1d, (double)size / Math.Max(info.Width, info.Height));
            int width = Math.Max(1, (int)Math.Round(info.Width * outputScale));
            int height = Math.Max(1, (int)Math.Round(info.Height * outputScale));
            using var thumbnail = new SKBitmap(rotated ? height : width, rotated ? width : height);
            using (var canvas = new SKCanvas(thumbnail))
            {
                canvas.Clear(SKColors.Transparent);
                switch (codec.EncodedOrigin)
                {
                    case SKEncodedOrigin.TopRight: canvas.Translate(width, 0); canvas.Scale(-1, 1); break;
                    case SKEncodedOrigin.BottomRight: canvas.Translate(width, height); canvas.RotateDegrees(180); break;
                    case SKEncodedOrigin.BottomLeft: canvas.Translate(0, height); canvas.Scale(1, -1); break;
                    case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
                    case SKEncodedOrigin.RightTop: canvas.Translate(height, 0); canvas.RotateDegrees(90); break;
                    case SKEncodedOrigin.RightBottom: canvas.Translate(height, width); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
                    case SKEncodedOrigin.LeftBottom: canvas.Translate(0, width); canvas.RotateDegrees(270); break;
                }
                using var source = SKImage.FromBitmap(decoded);
                canvas.DrawImage(source, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear));
            }
            token.ThrowIfCancellationRequested();
            using var result = SKImage.FromBitmap(thumbnail);
            using var png = result.Encode(SKEncodedImageFormat.Png, 100);
            token.ThrowIfCancellationRequested();
            return png?.ToArray();
        }
    }
}
