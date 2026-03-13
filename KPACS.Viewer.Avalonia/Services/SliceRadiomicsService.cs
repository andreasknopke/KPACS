using Avalonia;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;

namespace KPACS.Viewer.Services;

public static class SliceRadiomicsService
{
    public static bool TryExtract(
        StudyMeasurement measurement,
        DicomSpatialMetadata sliceMetadata,
        SeriesVolume volume,
        out SliceRadiomicsResult result)
    {
        ArgumentNullException.ThrowIfNull(sliceMetadata);
        ArgumentNullException.ThrowIfNull(volume);

        result = default!;

        if (measurement.Kind is not MeasurementKind.RectangleRoi and not MeasurementKind.PolygonRoi)
        {
            return false;
        }

        if (!measurement.TryProjectTo(sliceMetadata, out Point[] imagePoints) || imagePoints.Length < 2)
        {
            return false;
        }

        double averageSlice = measurement.Anchors
            .Where(anchor => anchor.PatientPoint is not null)
            .Select(anchor => volume.PatientToVoxel(anchor.PatientPoint!.Value).Z)
            .DefaultIfEmpty(0)
            .Average();
        int sliceIndex = Math.Clamp((int)Math.Round(averageSlice), 0, volume.SizeZ - 1);

        List<short> samples = measurement.Kind switch
        {
            MeasurementKind.RectangleRoi => ExtractRectangleSamples(imagePoints, sliceIndex, volume),
            MeasurementKind.PolygonRoi => ExtractPolygonSamples(imagePoints, sliceIndex, volume),
            _ => [],
        };

        if (samples.Count == 0)
        {
            return false;
        }

        samples.Sort();
        double mean = samples.Average(static value => (double)value);
        double variance = samples.Average(value => (value - mean) * (value - mean));
        double area = samples.Count * sliceMetadata.RowSpacing * sliceMetadata.ColumnSpacing;

        result = new SliceRadiomicsResult(
            measurement.Id,
            samples.Count,
            area,
            mean,
            Math.Sqrt(Math.Max(variance, 0)),
            samples[0],
            samples[^1],
            Percentile(samples, 0.50),
            Percentile(samples, 0.10),
            Percentile(samples, 0.90),
            ComputeEntropy(samples),
            ComputeUniformity(samples));
        return true;
    }

    private static List<short> ExtractRectangleSamples(Point[] imagePoints, int sliceIndex, SeriesVolume volume)
    {
        Point start = imagePoints[0];
        Point end = imagePoints[1];
        int left = Math.Clamp((int)Math.Floor(Math.Min(start.X, end.X)), 0, volume.SizeX - 1);
        int right = Math.Clamp((int)Math.Ceiling(Math.Max(start.X, end.X)), 0, volume.SizeX - 1);
        int top = Math.Clamp((int)Math.Floor(Math.Min(start.Y, end.Y)), 0, volume.SizeY - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(Math.Max(start.Y, end.Y)), 0, volume.SizeY - 1);

        var samples = new List<short>(Math.Max(1, (right - left + 1) * (bottom - top + 1)));
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                samples.Add(volume.GetVoxel(x, y, sliceIndex));
            }
        }

        return samples;
    }

    private static List<short> ExtractPolygonSamples(Point[] imagePoints, int sliceIndex, SeriesVolume volume)
    {
        int left = Math.Clamp((int)Math.Floor(imagePoints.Min(point => point.X)), 0, volume.SizeX - 1);
        int right = Math.Clamp((int)Math.Ceiling(imagePoints.Max(point => point.X)), 0, volume.SizeX - 1);
        int top = Math.Clamp((int)Math.Floor(imagePoints.Min(point => point.Y)), 0, volume.SizeY - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(imagePoints.Max(point => point.Y)), 0, volume.SizeY - 1);

        var polygon = imagePoints.ToArray();
        var samples = new List<short>();
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                if (!IsInsidePolygon(new Point(x + 0.5, y + 0.5), polygon))
                {
                    continue;
                }

                samples.Add(volume.GetVoxel(x, y, sliceIndex));
            }
        }

        return samples;
    }

    private static bool IsInsidePolygon(Point point, IReadOnlyList<Point> polygon)
    {
        bool inside = false;
        for (int index = 0, previous = polygon.Count - 1; index < polygon.Count; previous = index++)
        {
            Point a = polygon[index];
            Point b = polygon[previous];
            bool intersects = ((a.Y > point.Y) != (b.Y > point.Y)) &&
                              (point.X < ((b.X - a.X) * (point.Y - a.Y) / Math.Max(1e-6, b.Y - a.Y)) + a.X);
            if (intersects)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double Percentile(IReadOnlyList<short> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        double index = Math.Clamp(percentile, 0, 1) * (sortedValues.Count - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        double fraction = index - lower;
        return sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * fraction);
    }

    private static double ComputeEntropy(IReadOnlyList<short> sortedValues)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        short min = sortedValues[0];
        short max = sortedValues[^1];
        if (min == max)
        {
            return 0;
        }

        Span<int> histogram = stackalloc int[64];
        double scale = 63.0 / (max - min);
        foreach (short value in sortedValues)
        {
            int bin = Math.Clamp((int)Math.Round((value - min) * scale), 0, 63);
            histogram[bin]++;
        }

        double entropy = 0;
        foreach (int count in histogram)
        {
            if (count == 0)
            {
                continue;
            }

            double probability = count / (double)sortedValues.Count;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }

    private static double ComputeUniformity(IReadOnlyList<short> sortedValues)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        short min = sortedValues[0];
        short max = sortedValues[^1];
        if (min == max)
        {
            return 1;
        }

        Span<int> histogram = stackalloc int[64];
        double scale = 63.0 / (max - min);
        foreach (short value in sortedValues)
        {
            int bin = Math.Clamp((int)Math.Round((value - min) * scale), 0, 63);
            histogram[bin]++;
        }

        double uniformity = 0;
        foreach (int count in histogram)
        {
            if (count == 0)
            {
                continue;
            }

            double probability = count / (double)sortedValues.Count;
            uniformity += probability * probability;
        }

        return uniformity;
    }
}