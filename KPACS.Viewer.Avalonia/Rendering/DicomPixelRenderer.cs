// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - Rendering/DicomPixelRenderer.cs
// Ported from DCMImageClass.pas (TdcmImgObj) and dview.pas windowing logic.
//
// Renders raw DICOM pixel data to BGRA32 output buffers for display.
// Supports 8-bit, 16-bit grayscale (signed/unsigned), and 24-bit RGB.
// Uses lookup-table based windowing for high performance.
// Platform-independent — no UI framework dependencies.
// ------------------------------------------------------------------------------------------------

using System.Runtime.InteropServices;

namespace KPACS.Viewer.Rendering;

/// <summary>
/// Core DICOM pixel rendering engine.
/// Converts raw pixel buffers + window/level + color LUT → BGRA32 display buffer.
/// </summary>
public static class DicomPixelRenderer
{
    public static void Render(
        byte[] rawPixels,
        int width, int height,
        int bitsAllocated, int bitsStored,
        bool isSigned, int samplesPerPixel,
        double slope, double intercept,
        double windowCenter, double windowWidth,
        byte[] lutR, byte[] lutG, byte[] lutB,
        bool isMonochrome1,
        byte[] outputBgra)
    {
        if (samplesPerPixel >= 3)
        {
            RenderRgb(rawPixels, width, height,
                windowCenter, windowWidth, outputBgra);
        }
        else if (bitsAllocated >= 16)
        {
            Render16Bit(rawPixels, width, height, bitsStored, isSigned,
                slope, intercept, windowCenter, windowWidth,
                lutR, lutG, lutB, isMonochrome1, outputBgra);
        }
        else
        {
            Render8Bit(rawPixels, width, height,
                slope, intercept, windowCenter, windowWidth,
                lutR, lutG, lutB, isMonochrome1, outputBgra);
        }
    }

    public static (double Center, double Width) ComputeAutoWindow(
        byte[] rawPixels, int width, int height,
        int bitsAllocated, int bitsStored, bool isSigned,
        int samplesPerPixel, double slope, double intercept)
    {
        if (samplesPerPixel >= 3)
            return (127, 255);

        if (bitsAllocated >= 16)
        {
            var pixels = MemoryMarshal.Cast<byte, ushort>(rawPixels.AsSpan());
            int count = Math.Min(pixels.Length, width * height);
            double min = double.MaxValue, max = double.MinValue;
            int step = count > 500_000 ? count / 200_000 : 1;

            for (int i = 0; i < count; i += step)
            {
                double raw = isSigned ? (double)(short)pixels[i] : pixels[i];
                double rescaled = slope * raw + intercept;
                if (rescaled < min) min = rescaled;
                if (rescaled > max) max = rescaled;
            }

            double center = (min + max) / 2.0;
            double wid = max - min;
            if (wid < 1) wid = 1;
            return (center, wid);
        }
        else
        {
            int count = Math.Min(rawPixels.Length, width * height);
            double min = double.MaxValue, max = double.MinValue;

            for (int i = 0; i < count; i++)
            {
                double rescaled = slope * rawPixels[i] + intercept;
                if (rescaled < min) min = rescaled;
                if (rescaled > max) max = rescaled;
            }

            double center = (min + max) / 2.0;
            double wid = max - min;
            if (wid < 1) wid = 1;
            return (center, wid);
        }
    }

    private static void Render16Bit(
        byte[] rawPixels, int width, int height,
        int bitsStored, bool isSigned,
        double slope, double intercept,
        double windowCenter, double windowWidth,
        byte[] lutR, byte[] lutG, byte[] lutB,
        bool isMonochrome1,
        byte[] outputBgra)
    {
        int pixelCount = width * height;
        byte[] windowLut = new byte[65536];
        double wMin = windowCenter - windowWidth / 2.0;
        double wMax = windowCenter + windowWidth / 2.0;

        for (int i = 0; i < 65536; i++)
        {
            double rawValue = isSigned ? (double)(short)unchecked((ushort)i) : i;
            double rescaled = slope * rawValue + intercept;

            byte gray;
            if (windowWidth <= 0) gray = 128;
            else if (rescaled <= wMin) gray = 0;
            else if (rescaled >= wMax) gray = 255;
            else gray = (byte)((rescaled - wMin) / windowWidth * 255.0);

            if (isMonochrome1) gray = (byte)(255 - gray);
            windowLut[i] = gray;
        }

        var srcSpan = MemoryMarshal.Cast<byte, ushort>(rawPixels.AsSpan());
        int count = Math.Min(srcSpan.Length, pixelCount);
        int dstIdx = 0;

        for (int p = 0; p < count; p++)
        {
            byte gray = windowLut[srcSpan[p]];
            outputBgra[dstIdx] = lutB[gray];
            outputBgra[dstIdx + 1] = lutG[gray];
            outputBgra[dstIdx + 2] = lutR[gray];
            outputBgra[dstIdx + 3] = 255;
            dstIdx += 4;
        }
    }

    private static void Render8Bit(
        byte[] rawPixels, int width, int height,
        double slope, double intercept,
        double windowCenter, double windowWidth,
        byte[] lutR, byte[] lutG, byte[] lutB,
        bool isMonochrome1,
        byte[] outputBgra)
    {
        int pixelCount = width * height;
        byte[] windowLut = new byte[256];
        double wMin = windowCenter - windowWidth / 2.0;
        double wMax = windowCenter + windowWidth / 2.0;

        for (int i = 0; i < 256; i++)
        {
            double rescaled = slope * i + intercept;
            byte gray;
            if (windowWidth <= 0) gray = 128;
            else if (rescaled <= wMin) gray = 0;
            else if (rescaled >= wMax) gray = 255;
            else gray = (byte)((rescaled - wMin) / windowWidth * 255.0);

            if (isMonochrome1) gray = (byte)(255 - gray);
            windowLut[i] = gray;
        }

        int count = Math.Min(rawPixels.Length, pixelCount);
        int dstIdx = 0;

        for (int p = 0; p < count; p++)
        {
            byte gray = windowLut[rawPixels[p]];
            outputBgra[dstIdx] = lutB[gray];
            outputBgra[dstIdx + 1] = lutG[gray];
            outputBgra[dstIdx + 2] = lutR[gray];
            outputBgra[dstIdx + 3] = 255;
            dstIdx += 4;
        }
    }

    /// <summary>
    /// Renders a pre-rescaled 16-bit signed pixel buffer (from <see cref="VolumeReslicer"/>)
    /// to BGRA32. The values are already rescaled (HU or similar), so no slope/intercept
    /// is applied — only windowing + color LUT.
    /// </summary>
    public static void RenderRescaled16Bit(
        short[] rescaledPixels,
        int width, int height,
        double windowCenter, double windowWidth,
        byte[] lutR, byte[] lutG, byte[] lutB,
        bool isMonochrome1,
        byte[] outputBgra)
    {
        int pixelCount = width * height;
        double wMin = windowCenter - windowWidth / 2.0;
        double wMax = windowCenter + windowWidth / 2.0;

        // Build a 65536-entry LUT mapping ushort → gray byte.
        // We reinterpret short → ushort for indexing.
        byte[] windowLut = new byte[65536];
        for (int i = 0; i < 65536; i++)
        {
            double value = (short)unchecked((ushort)i); // interpret as signed
            byte gray;
            if (windowWidth <= 0) gray = 128;
            else if (value <= wMin) gray = 0;
            else if (value >= wMax) gray = 255;
            else gray = (byte)((value - wMin) / windowWidth * 255.0);

            if (isMonochrome1) gray = (byte)(255 - gray);
            windowLut[i] = gray;
        }

        int count = Math.Min(rescaledPixels.Length, pixelCount);
        int dstIdx = 0;

        for (int p = 0; p < count; p++)
        {
            byte gray = windowLut[unchecked((ushort)rescaledPixels[p])];
            outputBgra[dstIdx] = lutB[gray];
            outputBgra[dstIdx + 1] = lutG[gray];
            outputBgra[dstIdx + 2] = lutR[gray];
            outputBgra[dstIdx + 3] = 255;
            dstIdx += 4;
        }
    }

    /// <summary>
    /// Computes auto window center/width from a rescaled 16-bit signed buffer.
    /// </summary>
    public static (double Center, double Width) ComputeAutoWindowRescaled(short[] pixels, int width, int height)
    {
        int count = Math.Min(pixels.Length, width * height);
        if (count == 0)
            return (0, 1);

        short min = short.MaxValue, max = short.MinValue;
        int step = count > 500_000 ? count / 200_000 : 1;

        for (int i = 0; i < count; i += step)
        {
            short v = pixels[i];
            if (v < min) min = v;
            if (v > max) max = v;
        }

        double center = (min + max) / 2.0;
        double wid = Math.Max(1, max - min);
        return (center, wid);
    }

    private static void RenderRgb(
        byte[] rawPixels, int width, int height,
        double windowCenter, double windowWidth,
        byte[] outputBgra)
    {
        int pixelCount = width * height;
        byte[]? contrastLut = null;
        bool needsContrast = !(Math.Abs(windowCenter - 127) < 1 && Math.Abs(windowWidth - 255) < 1)
                             && windowWidth > 0;

        if (needsContrast)
        {
            contrastLut = new byte[256];
            double scale = 256.0 / windowWidth;
            for (int i = 0; i < 256; i++)
            {
                int temp = (int)(128 + (i - windowCenter) * scale);
                contrastLut[i] = (byte)Math.Clamp(temp, 0, 255);
            }
        }

        int srcIdx = 0;
        int dstIdx = 0;

        for (int p = 0; p < pixelCount && srcIdx + 2 < rawPixels.Length; p++)
        {
            byte r = rawPixels[srcIdx];
            byte g = rawPixels[srcIdx + 1];
            byte b = rawPixels[srcIdx + 2];
            srcIdx += 3;

            if (contrastLut != null)
            {
                r = contrastLut[r];
                g = contrastLut[g];
                b = contrastLut[b];
            }

            outputBgra[dstIdx] = b;
            outputBgra[dstIdx + 1] = g;
            outputBgra[dstIdx + 2] = r;
            outputBgra[dstIdx + 3] = 255;
            dstIdx += 4;
        }
    }
}
