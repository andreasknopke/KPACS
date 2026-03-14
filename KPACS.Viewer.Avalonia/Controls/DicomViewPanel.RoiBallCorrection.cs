using Avalonia;
using Avalonia.Input;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Controls;

public partial class DicomViewPanel
{
    private const int RoiBallMinRadiusPixels = 3;
    private const int RoiBallMaxRadiusPixels = 64;
    private const int RoiBallDefaultRadiusPixels = 10;

    private Point? _roiBallHoverImagePoint;
    private BallRoiDragSession? _roiBallDragSession;
    private int _roiBallRadiusPixels = RoiBallDefaultRadiusPixels;

    public int BallRoiRadiusPixels => _roiBallRadiusPixels;

    private readonly record struct RoiMask(int Left, int Top, bool[,] Pixels, int PixelCount)
    {
        public int Width => Pixels.GetLength(0);
        public int Height => Pixels.GetLength(1);
    }

    private readonly record struct RoiMaskComponent(int Left, int Top, bool[,] Pixels, int PixelCount, Point Centroid);

    private readonly record struct BallRoiBrushPreview(Point ImagePoint, int RadiusPixels, bool AddRegion, bool EdgeCollision);

    private sealed class BallRoiDragSession(bool addRegion, Guid? measurementId, bool targetsVolumeDraft, Point lastAppliedPoint)
    {
        public bool AddRegion { get; } = addRegion;
        public Guid? MeasurementId { get; } = measurementId;
        public bool TargetsVolumeDraft { get; } = targetsVolumeDraft;
        public Point LastAppliedPoint { get; set; } = lastAppliedPoint;
    }

    public bool TryAdjustBallRoiRadius(int deltaPixels, out int resultingRadius)
    {
        resultingRadius = _roiBallRadiusPixels;
        if (deltaPixels == 0)
        {
            return false;
        }

        int nextRadius = Math.Clamp(_roiBallRadiusPixels + deltaPixels, RoiBallMinRadiusPixels, RoiBallMaxRadiusPixels);
        resultingRadius = nextRadius;
        if (nextRadius == _roiBallRadiusPixels)
        {
            return false;
        }

        _roiBallRadiusPixels = nextRadius;
        UpdateMeasurementPresentation();
        return true;
    }

    private void ClearBallRoiInteraction(bool clearHover = true)
    {
        _roiBallDragSession = null;
        if (clearHover)
        {
            _roiBallHoverImagePoint = null;
        }
    }

    private void UpdateBallRoiHoverPoint(Point? imagePoint)
    {
        _roiBallHoverImagePoint = imagePoint is Point point ? ClampImagePoint(point) : null;
    }

    private bool TryStartBallRoiCorrectionSession(Point imagePoint, IPointer pointer)
    {
        Point clamped = ClampImagePoint(imagePoint);
        UpdateBallRoiHoverPoint(clamped);

        if (!TryGetBallRoiBrushPreview(clamped, forcedAddRegion: null, out BallRoiBrushPreview preview) || !preview.EdgeCollision)
        {
            UpdateMeasurementPresentation();
            return false;
        }

        if (!TryApplyBallRoiCorrection(clamped, preview.AddRegion, preview.RadiusPixels, requireEdgeCollision: true))
        {
            return false;
        }

        _roiBallDragSession = new BallRoiDragSession(
            preview.AddRegion,
            _selectedMeasurementId,
            _volumeRoiDraft is not null && CanRetainVolumeRoiDraftForCurrentSlice(),
            clamped);
        pointer.Capture(RootGrid);
        _capturedPointer = pointer;
        UpdateMeasurementPresentation();
        return true;
    }

    private bool TryContinueBallRoiCorrectionSession(Point imagePoint)
    {
        Point clamped = ClampImagePoint(imagePoint);
        UpdateBallRoiHoverPoint(clamped);
        if (_roiBallDragSession is not { } session || !IsBallRoiSessionCompatible(session))
        {
            ClearBallRoiInteraction(clearHover: false);
            UpdateMeasurementPresentation();
            return false;
        }

        double minimumSpacing = Math.Max(1.0, _roiBallRadiusPixels * 0.35);
        if (Distance(session.LastAppliedPoint, clamped) < minimumSpacing)
        {
            UpdateMeasurementPresentation();
            return false;
        }

        if (!TryApplyBallRoiCorrection(clamped, session.AddRegion, _roiBallRadiusPixels, requireEdgeCollision: true))
        {
            UpdateMeasurementPresentation();
            return false;
        }

        session.LastAppliedPoint = clamped;
        UpdateMeasurementPresentation();
        return true;
    }

    private bool IsBallRoiSessionCompatible(BallRoiDragSession session)
    {
        bool volumeDraftActive = _volumeRoiDraft is not null && CanRetainVolumeRoiDraftForCurrentSlice();
        if (session.TargetsVolumeDraft)
        {
            return volumeDraftActive;
        }

        return !volumeDraftActive && _selectedMeasurementId == session.MeasurementId;
    }

    public bool TryApplyBallRoiCorrection(Point imagePoint)
    {
        return TryApplyBallRoiCorrection(imagePoint, forcedAddRegion: null, _roiBallRadiusPixels, requireEdgeCollision: true);
    }

    private bool TryApplyBallRoiCorrection(Point imagePoint, bool? forcedAddRegion, int radiusPixels, bool requireEdgeCollision)
    {
        Point clamped = ClampImagePoint(imagePoint);

        if (_volumeRoiDraft is not null && CanRetainVolumeRoiDraftForCurrentSlice() && TryApplyBallCorrectionToVolumeDraft(clamped, forcedAddRegion, radiusPixels, requireEdgeCollision))
        {
            return true;
        }

        if (_selectedMeasurementId is not Guid measurementId || SpatialMetadata is null)
        {
            return false;
        }

        StudyMeasurement? measurement = _measurements.FirstOrDefault(candidate => candidate.Id == measurementId);
        if (measurement is null)
        {
            return false;
        }

        return measurement.Kind switch
        {
            MeasurementKind.PolygonRoi => TryApplyBallCorrectionToPolygon(measurement, clamped, forcedAddRegion, radiusPixels, requireEdgeCollision),
            MeasurementKind.VolumeRoi => TryApplyBallCorrectionToVolumeMeasurement(measurement, clamped, forcedAddRegion, radiusPixels, requireEdgeCollision),
            _ => false,
        };
    }

    private bool TryApplyBallCorrectionToPolygon(StudyMeasurement measurement, Point imagePoint, bool? forcedAddRegion, int radiusPixels, bool requireEdgeCollision)
    {
        if (SpatialMetadata is null || !measurement.TryProjectTo(SpatialMetadata, out Point[] imagePoints) || imagePoints.Length < 3)
        {
            return false;
        }

        if (!TrySculptContour(imagePoints, imagePoint, forcedAddRegion, radiusPixels, requireEdgeCollision, out Point[] updatedPoints, out _, out _))
        {
            return false;
        }

        StudyMeasurement updated = measurement.WithAnchors(SpatialMetadata, updatedPoints);
        MeasurementUpdated?.Invoke(updated);
        UpdateMeasurementPresentation();
        return true;
    }

    private bool TryApplyBallCorrectionToVolumeMeasurement(StudyMeasurement measurement, Point imagePoint, bool? forcedAddRegion, int radiusPixels, bool requireEdgeCollision)
    {
        if (SpatialMetadata is null || measurement.VolumeContours is null || measurement.VolumeContours.Length == 0)
        {
            return false;
        }

        VolumeRoiContour[] currentSliceContours = measurement.VolumeContours
            .Where(IsContourOnCurrentSlice)
            .OrderBy(contour => contour.ComponentId)
            .ToArray();

        if (currentSliceContours.Length > 0)
        {
            List<(VolumeRoiContour Contour, Point[] Points)> candidates = [];
            foreach (VolumeRoiContour contour in currentSliceContours)
            {
                if (contour.IsClosed && contour.TryProjectTo(SpatialMetadata, out Point[] points) && points.Length >= 3)
                {
                    candidates.Add((contour, points));
                }
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            if (!TrySelectSculptedContour(candidates.Select(candidate => candidate.Points).ToArray(), imagePoint, forcedAddRegion, radiusPixels, requireEdgeCollision, out int contourIndex, out Point[] updatedPoints, out _, out _))
            {
                return false;
            }

            VolumeRoiContour target = candidates[contourIndex].Contour;
            VolumeRoiContour updatedTarget = CreateUpdatedVolumeContour(target, updatedPoints);
            VolumeRoiContour[] updatedContours = measurement.VolumeContours
                .Select(contour => ReferenceEquals(contour, target) ? updatedTarget : contour)
                .OrderBy(contour => contour.PlanePosition)
                .ThenBy(contour => contour.ComponentId)
                .ToArray();

            StudyMeasurement updatedMeasurement = measurement.WithVolumeContours(updatedContours);
            MeasurementUpdated?.Invoke(updatedMeasurement);
            UpdateMeasurementPresentation();
            return true;
        }

        if (!measurement.TryProjectVolumeContoursTo(SpatialMetadata, out Point[][] interpolatedContours) || interpolatedContours.Length == 0)
        {
            return false;
        }

        if (!TrySelectSculptedContour(interpolatedContours, imagePoint, forcedAddRegion, radiusPixels, requireEdgeCollision, out _, out Point[] interpolatedUpdatedPoints, out _, out _))
        {
            return false;
        }

        VolumeRoiContour insertedContour = CreateInterpolatedVolumeContour(measurement, interpolatedUpdatedPoints);
        VolumeRoiContour[] insertedContours = measurement.VolumeContours
            .Concat([insertedContour])
            .OrderBy(contour => contour.PlanePosition)
            .ThenBy(contour => contour.ComponentId)
            .ToArray();

        StudyMeasurement updated = measurement.WithVolumeContours(insertedContours);
        MeasurementUpdated?.Invoke(updated);
        UpdateMeasurementPresentation();
        return true;
    }

    private bool TryApplyBallCorrectionToVolumeDraft(Point imagePoint, bool? forcedAddRegion, int radiusPixels, bool requireEdgeCollision)
    {
        if (_volumeRoiDraft is null || SpatialMetadata is null)
        {
            return false;
        }

        VolumeRoiDraftContour[] currentContours = _volumeRoiDraft.Contours.Values
            .Where(contour => contour.IsClosed && string.Equals(contour.SliceKey, GetCurrentVolumeRoiSliceKey(SpatialMetadata), StringComparison.Ordinal))
            .OrderBy(contour => contour.ComponentId)
            .ToArray();
        if (currentContours.Length == 0)
        {
            return false;
        }

        List<(VolumeRoiDraftContour Contour, Point[] Points)> candidates = [];
        foreach (VolumeRoiDraftContour contour in currentContours)
        {
            if (contour.TryProjectTo(SpatialMetadata, out Point[] points) && points.Length >= 3)
            {
                candidates.Add((contour, points));
            }
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        if (!TrySelectSculptedContour(candidates.Select(candidate => candidate.Points).ToArray(), imagePoint, forcedAddRegion, radiusPixels, requireEdgeCollision, out int contourIndex, out Point[] updatedPoints, out _, out _))
        {
            return false;
        }

        VolumeRoiDraftContour target = candidates[contourIndex].Contour;
        target.Anchors.Clear();
        target.Anchors.AddRange(updatedPoints.Select(point => new MeasurementAnchor(point, SpatialMetadata.PatientPointFromPixel(point))));
        NotifyVolumeRoiDraftChanged();
        UpdateMeasurementPresentation();
        return true;
    }

    private bool TryBuildVolumeDraftSliceMask(VolumeRoiDraftContour[] contours, out RoiMask mask)
    {
        mask = default;
        if (SpatialMetadata is null)
        {
            return false;
        }

        Point[][] projected = contours
            .Select(contour => contour.TryProjectTo(SpatialMetadata, out Point[] points) ? points : [])
            .Where(points => points.Length >= 3)
            .ToArray();
        if (projected.Length == 0)
        {
            return false;
        }

        return TryBuildMultiPolygonMask(projected, _roiBallRadiusPixels + 2, out mask);
    }

    private bool TryBuildVolumeSliceMask(StudyMeasurement measurement, out RoiMask mask, out VolumeRoiContour[] existingContours)
    {
        mask = default;
        existingContours = [];
        if (SpatialMetadata is null || measurement.VolumeContours is null)
        {
            return false;
        }

        existingContours = measurement.VolumeContours
            .Where(IsContourOnCurrentSlice)
            .OrderBy(contour => contour.ComponentId)
            .ToArray();

        List<Point[]> projected = [];
        foreach (VolumeRoiContour contour in existingContours)
        {
            if (contour.IsClosed && contour.TryProjectTo(SpatialMetadata, out Point[] points) && points.Length >= 3)
            {
                projected.Add(points);
            }
        }

        if (projected.Count == 0 && measurement.TryProjectVolumeContoursTo(SpatialMetadata, out Point[][] interpolatedContours) && interpolatedContours.Length > 0)
        {
            projected.AddRange(interpolatedContours.Where(points => points.Length >= 3));
            existingContours = [];
        }

        if (projected.Count == 0)
        {
            return false;
        }

        return TryBuildMultiPolygonMask(projected, _roiBallRadiusPixels + 2, out mask);
    }

    private bool TryGetBallRoiBrushPreview(Point imagePoint, bool? forcedAddRegion, out BallRoiBrushPreview preview)
    {
        preview = default;
        Point clamped = ClampImagePoint(imagePoint);

        if (_volumeRoiDraft is not null && CanRetainVolumeRoiDraftForCurrentSlice())
        {
            Point[][] currentContours = _volumeRoiDraft.Contours.Values
                .Where(contour => contour.IsClosed && string.Equals(contour.SliceKey, GetCurrentVolumeRoiSliceKey(SpatialMetadata!), StringComparison.Ordinal))
                .OrderBy(contour => contour.ComponentId)
                .Select(contour => contour.TryProjectTo(SpatialMetadata!, out Point[] points) ? points : [])
                .Where(points => points.Length >= 3)
                .ToArray();
            return TryCreateBallRoiBrushPreview(clamped, currentContours, forcedAddRegion, out preview);
        }

        if (_selectedMeasurementId is not Guid measurementId || SpatialMetadata is null)
        {
            return false;
        }

        StudyMeasurement? measurement = _measurements.FirstOrDefault(candidate => candidate.Id == measurementId);
        if (measurement is null)
        {
            return false;
        }

        switch (measurement.Kind)
        {
            case MeasurementKind.PolygonRoi:
                if (!measurement.TryProjectTo(SpatialMetadata, out Point[] polygonPoints) || polygonPoints.Length < 3)
                {
                    return false;
                }
                return TryCreateBallRoiBrushPreview(clamped, [polygonPoints], forcedAddRegion, out preview);
            case MeasurementKind.VolumeRoi:
                Point[][] projectedContours;
                if (measurement.VolumeContours is not null)
                {
                    projectedContours = measurement.VolumeContours
                        .Where(IsContourOnCurrentSlice)
                        .Select(contour => contour.TryProjectTo(SpatialMetadata, out Point[] points) ? points : [])
                        .Where(points => points.Length >= 3)
                        .ToArray();
                }
                else
                {
                    projectedContours = [];
                }

                if (projectedContours.Length == 0 && (!measurement.TryProjectVolumeContoursTo(SpatialMetadata, out projectedContours) || projectedContours.Length == 0))
                {
                    return false;
                }

                return TryCreateBallRoiBrushPreview(clamped, projectedContours, forcedAddRegion, out preview);
            default:
                return false;
        }
    }

    private bool TryCreateBallRoiBrushPreview(Point imagePoint, Point[][] contours, bool? forcedAddRegion, out BallRoiBrushPreview preview)
    {
        bool addRegion = forcedAddRegion ?? !contours.Any(contour => IsPointInsidePolygon(imagePoint, contour));
        bool edgeCollision = false;
        double bestDistance = double.MaxValue;
        foreach (Point[] contour in contours)
        {
            if (TryGetBrushCollision(contour, imagePoint, _roiBallRadiusPixels, out _, out double distance) && distance < bestDistance)
            {
                bestDistance = distance;
                edgeCollision = true;
            }
        }

        preview = new BallRoiBrushPreview(imagePoint, _roiBallRadiusPixels, addRegion, edgeCollision);
        return true;
    }

    private bool TrySelectSculptedContour(Point[][] contours, Point imagePoint, bool? forcedAddRegion, int radiusPixels, bool requireEdgeCollision, out int contourIndex, out Point[] updatedPoints, out bool addRegion, out double collisionDistance)
    {
        contourIndex = -1;
        updatedPoints = [];
        addRegion = false;
        collisionDistance = double.MaxValue;

        for (int index = 0; index < contours.Length; index++)
        {
            if (!TrySculptContour(contours[index], imagePoint, forcedAddRegion, radiusPixels, requireEdgeCollision, out Point[] candidatePoints, out bool candidateAddRegion, out double candidateDistance))
            {
                continue;
            }

            if (candidateDistance < collisionDistance)
            {
                contourIndex = index;
                updatedPoints = candidatePoints;
                addRegion = candidateAddRegion;
                collisionDistance = candidateDistance;
            }
        }

        return contourIndex >= 0;
    }

    private bool TrySculptContour(Point[] contour, Point brushCenter, bool? forcedAddRegion, int radiusPixels, bool requireEdgeCollision, out Point[] updatedPoints, out bool addRegion, out double collisionDistance)
    {
        updatedPoints = contour;
        addRegion = forcedAddRegion ?? !IsPointInsidePolygon(brushCenter, contour);
        collisionDistance = double.MaxValue;
        if (!TryGetBrushCollision(contour, brushCenter, radiusPixels, out Point collisionPoint, out collisionDistance))
        {
            return !requireEdgeCollision && contour.Length >= 3;
        }

        if (requireEdgeCollision && collisionDistance > radiusPixels)
        {
            return false;
        }

        updatedPoints = SculptContourLocally(contour, brushCenter, collisionPoint, radiusPixels);
        return updatedPoints.Length >= 3;
    }

    private Point[] SculptContourLocally(Point[] contour, Point brushCenter, Point collisionPoint, int radiusPixels)
    {
        Point[] adjusted = contour.ToArray();
        double collisionDistance = Distance(brushCenter, collisionPoint);
        double penetration = Math.Clamp((radiusPixels - collisionDistance) / Math.Max(1.0, radiusPixels), 0.08, 0.85);
        double influenceRadius = Math.Max(radiusPixels * 1.8, 6.0);

        for (int index = 0; index < contour.Length; index++)
        {
            Point point = contour[index];
            double localDistance = Distance(point, collisionPoint);
            if (localDistance > influenceRadius)
            {
                continue;
            }

            Vector radial = point - brushCenter;
            double radialLength = Math.Sqrt((radial.X * radial.X) + (radial.Y * radial.Y));
            if (radialLength < 1e-3)
            {
                radial = collisionPoint - brushCenter;
                radialLength = Math.Sqrt((radial.X * radial.X) + (radial.Y * radial.Y));
                if (radialLength < 1e-3)
                {
                    continue;
                }
            }

            Vector unit = new(radial.X / radialLength, radial.Y / radialLength);
            Point target = new(
                brushCenter.X + (unit.X * radiusPixels),
                brushCenter.Y + (unit.Y * radiusPixels));
            double influence = Math.Pow(1.0 - (localDistance / influenceRadius), 2.0) * penetration;
            adjusted[index] = ClampImagePoint(new Point(
                point.X + ((target.X - point.X) * influence),
                point.Y + ((target.Y - point.Y) * influence)));
        }

        Point[] smoothed = adjusted.ToArray();
        for (int index = 0; index < contour.Length; index++)
        {
            double localDistance = Distance(contour[index], collisionPoint);
            if (localDistance > influenceRadius * 1.1)
            {
                continue;
            }

            Point previous = adjusted[(index - 1 + adjusted.Length) % adjusted.Length];
            Point current = adjusted[index];
            Point next = adjusted[(index + 1) % adjusted.Length];
            smoothed[index] = ClampImagePoint(new Point(
                (current.X * 0.5) + ((previous.X + next.X) * 0.25),
                (current.Y * 0.5) + ((previous.Y + next.Y) * 0.25)));
        }

        return smoothed;
    }

    private bool TryGetBrushCollision(Point[] contour, Point brushCenter, int radiusPixels, out Point collisionPoint, out double collisionDistance)
    {
        collisionPoint = default;
        collisionDistance = double.MaxValue;

        for (int index = 0; index < contour.Length; index++)
        {
            Point start = contour[index];
            Point end = contour[(index + 1) % contour.Length];
            Point closest = GetClosestPointOnSegment(brushCenter, start, end);
            double distance = Distance(brushCenter, closest);
            if (distance < collisionDistance)
            {
                collisionDistance = distance;
                collisionPoint = closest;
            }
        }

        return collisionDistance <= radiusPixels;
    }

    private static Point GetClosestPointOnSegment(Point point, Point start, Point end)
    {
        Vector segment = end - start;
        double lengthSquared = (segment.X * segment.X) + (segment.Y * segment.Y);
        if (lengthSquared <= double.Epsilon)
        {
            return start;
        }

        Vector pointVector = point - start;
        double t = ((pointVector.X * segment.X) + (pointVector.Y * segment.Y)) / lengthSquared;
        t = Math.Clamp(t, 0.0, 1.0);
        return new Point(start.X + (segment.X * t), start.Y + (segment.Y * t));
    }

    private VolumeRoiContour CreateUpdatedVolumeContour(VolumeRoiContour sourceContour, Point[] updatedPoints)
    {
        if (SpatialMetadata is null)
        {
            return sourceContour;
        }

        return new VolumeRoiContour(
            updatedPoints.Select(point => new MeasurementAnchor(point, SpatialMetadata.PatientPointFromPixel(point))).ToArray(),
            sourceContour.SourceFilePath,
            sourceContour.ReferencedSopInstanceUid,
            sourceContour.PlaneOrigin,
            sourceContour.RowDirection,
            sourceContour.ColumnDirection,
            sourceContour.Normal,
            sourceContour.PlanePosition,
            sourceContour.IsClosed,
            sourceContour.RowSpacing,
            sourceContour.ColumnSpacing,
            sourceContour.ComponentId);
    }

    private VolumeRoiContour CreateInterpolatedVolumeContour(StudyMeasurement measurement, Point[] updatedPoints)
    {
        if (SpatialMetadata is null)
        {
            throw new InvalidOperationException("Spatial metadata required for interpolated contour creation.");
        }

        int componentId = ResolveInterpolatedComponentId(measurement, GetPolygonCenter(updatedPoints));
        return new VolumeRoiContour(
            updatedPoints.Select(point => new MeasurementAnchor(point, SpatialMetadata.PatientPointFromPixel(point))).ToArray(),
            SpatialMetadata.FilePath,
            SpatialMetadata.SopInstanceUid,
            SpatialMetadata.Origin,
            SpatialMetadata.RowDirection,
            SpatialMetadata.ColumnDirection,
            SpatialMetadata.Normal,
            GetPlanePosition(SpatialMetadata),
            true,
            SpatialMetadata.RowSpacing,
            SpatialMetadata.ColumnSpacing,
            componentId);
    }

    private int ResolveInterpolatedComponentId(StudyMeasurement measurement, Point contourCenter)
    {
        if (SpatialMetadata is null || measurement.VolumeContours is null || measurement.VolumeContours.Length == 0)
        {
            return 0;
        }

        int bestComponentId = measurement.VolumeContours[0].ComponentId;
        double bestScore = double.MaxValue;
        double targetPlane = GetPlanePosition(SpatialMetadata);
        foreach (VolumeRoiContour contour in measurement.VolumeContours.Where(contour => contour.IsClosed && contour.Anchors.Length >= 3))
        {
            double score = Math.Abs(contour.PlanePosition - targetPlane);
            if (contour.TryProjectTo(SpatialMetadata, out Point[] projected) && projected.Length >= 3)
            {
                score += Distance(GetPolygonCenter(projected), contourCenter) * 0.01;
            }

            if (score < bestScore)
            {
                bestScore = score;
                bestComponentId = contour.ComponentId;
            }
        }

        return bestComponentId;
    }

    private VolumeRoiContour[] BuildReplacementVolumeContours(StudyMeasurement measurement, VolumeRoiContour[] existingContours, List<RoiMaskComponent> components)
    {
        if (SpatialMetadata is null)
        {
            return [];
        }

        List<(int ComponentId, Point Centroid)> existing = [];
        foreach (VolumeRoiContour contour in existingContours)
        {
            if (contour.TryProjectTo(SpatialMetadata, out Point[] points) && points.Length >= 3)
            {
                existing.Add((contour.ComponentId, GetPolygonCenter(points)));
            }
        }

        List<int> availableComponentIds = existing.Select(item => item.ComponentId).ToList();
        int nextComponentId = Math.Max(0, (measurement.VolumeContours?.Select(contour => contour.ComponentId).DefaultIfEmpty(-1).Max() ?? -1) + 1);
        List<VolumeRoiContour> replacement = [];

        foreach (RoiMaskComponent component in components.OrderByDescending(component => component.PixelCount))
        {
            Point[] imagePoints = TraceAutoOutlineBoundary(new AutoOutlineMask(component.Left, component.Top, component.Pixels, component.PixelCount), 80)
                .Select(point => new Point(point.X + component.Left, point.Y + component.Top))
                .ToArray();
            if (imagePoints.Length < 3)
            {
                continue;
            }

            int componentId;
            if (availableComponentIds.Count > 0)
            {
                int bestIndex = 0;
                double bestDistance = double.MaxValue;
                for (int index = 0; index < availableComponentIds.Count; index++)
                {
                    Point existingCentroid = existing.First(item => item.ComponentId == availableComponentIds[index]).Centroid;
                    double distance = Distance(existingCentroid, GetGlobalCentroid(component));
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestIndex = index;
                    }
                }

                componentId = availableComponentIds[bestIndex];
                availableComponentIds.RemoveAt(bestIndex);
            }
            else
            {
                componentId = nextComponentId++;
            }

            replacement.Add(new VolumeRoiContour(
                imagePoints.Select(point => new MeasurementAnchor(point, SpatialMetadata.PatientPointFromPixel(point))).ToArray(),
                SpatialMetadata.FilePath,
                SpatialMetadata.SopInstanceUid,
                SpatialMetadata.Origin,
                SpatialMetadata.RowDirection,
                SpatialMetadata.ColumnDirection,
                SpatialMetadata.Normal,
                GetPlanePosition(SpatialMetadata),
                true,
                SpatialMetadata.RowSpacing,
                SpatialMetadata.ColumnSpacing,
                componentId));
        }

        return replacement.ToArray();
    }

    private void ReplaceDraftSliceContours(VolumeRoiDraft draft, VolumeRoiDraftContour[] existingContours, List<RoiMaskComponent> components)
    {
        if (SpatialMetadata is null)
        {
            return;
        }

        string sliceKey = GetCurrentVolumeRoiSliceKey(SpatialMetadata);
        foreach (string key in draft.Contours.Where(entry => string.Equals(entry.Value.SliceKey, sliceKey, StringComparison.Ordinal)).Select(entry => entry.Key).ToArray())
        {
            draft.Contours.Remove(key);
        }

        List<(int ComponentId, Point Centroid)> existing = [];
        foreach (VolumeRoiDraftContour contour in existingContours)
        {
            if (contour.TryProjectTo(SpatialMetadata, out Point[] points) && points.Length >= 3)
            {
                existing.Add((contour.ComponentId, GetPolygonCenter(points)));
            }
        }

        List<int> availableComponentIds = existing.Select(item => item.ComponentId).ToList();
        int nextComponentId = Math.Max(draft.NextComponentId, existingContours.Select(contour => contour.ComponentId).DefaultIfEmpty(0).Max() + 1);

        foreach (RoiMaskComponent component in components.OrderByDescending(component => component.PixelCount))
        {
            Point[] imagePoints = TraceAutoOutlineBoundary(new AutoOutlineMask(component.Left, component.Top, component.Pixels, component.PixelCount), 80)
                .Select(point => new Point(point.X + component.Left, point.Y + component.Top))
                .ToArray();
            if (imagePoints.Length < 3)
            {
                continue;
            }

            int componentId;
            if (availableComponentIds.Count > 0)
            {
                int bestIndex = 0;
                double bestDistance = double.MaxValue;
                for (int index = 0; index < availableComponentIds.Count; index++)
                {
                    Point existingCentroid = existing.First(item => item.ComponentId == availableComponentIds[index]).Centroid;
                    double distance = Distance(existingCentroid, GetGlobalCentroid(component));
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestIndex = index;
                    }
                }

                componentId = availableComponentIds[bestIndex];
                availableComponentIds.RemoveAt(bestIndex);
            }
            else
            {
                componentId = nextComponentId++;
            }

            string contourKey = BuildVolumeRoiContourKey(sliceKey, componentId);
            draft.Contours[contourKey] = new VolumeRoiDraftContour(
                sliceKey,
                contourKey,
                componentId,
                SpatialMetadata.FilePath,
                SpatialMetadata.SopInstanceUid,
                SpatialMetadata.Origin,
                SpatialMetadata.RowDirection,
                SpatialMetadata.ColumnDirection,
                SpatialMetadata.Normal,
                GetPlanePosition(SpatialMetadata),
                SpatialMetadata.RowSpacing,
                SpatialMetadata.ColumnSpacing,
                imagePoints.Select(point => new MeasurementAnchor(point, SpatialMetadata.PatientPointFromPixel(point))).ToList(),
                true);
            draft.ActiveAddComponentId = componentId;
        }

        draft.NextComponentId = Math.Max(draft.NextComponentId, nextComponentId);
        draft.PendingAddContour = null;
    }

    private bool TryBuildPolygonMask(IReadOnlyList<Point> polygonPoints, int padding, out RoiMask mask)
    {
        mask = default;
        if (polygonPoints.Count < 3)
        {
            return false;
        }

        int left = Math.Clamp((int)Math.Floor(polygonPoints.Min(point => point.X)) - padding, 0, Math.Max(0, _imageWidth - 1));
        int right = Math.Clamp((int)Math.Ceiling(polygonPoints.Max(point => point.X)) + padding, 0, Math.Max(0, _imageWidth - 1));
        int top = Math.Clamp((int)Math.Floor(polygonPoints.Min(point => point.Y)) - padding, 0, Math.Max(0, _imageHeight - 1));
        int bottom = Math.Clamp((int)Math.Ceiling(polygonPoints.Max(point => point.Y)) + padding, 0, Math.Max(0, _imageHeight - 1));
        if (right <= left || bottom <= top)
        {
            return false;
        }

        Point[] polygon = polygonPoints as Point[] ?? polygonPoints.ToArray();
        bool[,] pixels = new bool[right - left + 1, bottom - top + 1];
        int pixelCount = 0;
        for (int y = 0; y < pixels.GetLength(1); y++)
        {
            for (int x = 0; x < pixels.GetLength(0); x++)
            {
                bool inside = IsPointInsidePolygon(new Point(left + x + 0.5, top + y + 0.5), polygon);
                pixels[x, y] = inside;
                if (inside)
                {
                    pixelCount++;
                }
            }
        }

        if (pixelCount < 3)
        {
            return false;
        }

        mask = new RoiMask(left, top, pixels, pixelCount);
        return true;
    }

    private bool TryBuildMultiPolygonMask(IEnumerable<Point[]> polygons, int padding, out RoiMask mask)
    {
        mask = default;
        Point[][] polygonArray = polygons.Where(points => points.Length >= 3).ToArray();
        if (polygonArray.Length == 0)
        {
            return false;
        }

        int left = Math.Clamp((int)Math.Floor(polygonArray.Min(points => points.Min(point => point.X))) - padding, 0, Math.Max(0, _imageWidth - 1));
        int right = Math.Clamp((int)Math.Ceiling(polygonArray.Max(points => points.Max(point => point.X))) + padding, 0, Math.Max(0, _imageWidth - 1));
        int top = Math.Clamp((int)Math.Floor(polygonArray.Min(points => points.Min(point => point.Y))) - padding, 0, Math.Max(0, _imageHeight - 1));
        int bottom = Math.Clamp((int)Math.Ceiling(polygonArray.Max(points => points.Max(point => point.Y))) + padding, 0, Math.Max(0, _imageHeight - 1));
        if (right <= left || bottom <= top)
        {
            return false;
        }

        bool[,] pixels = new bool[right - left + 1, bottom - top + 1];
        int pixelCount = 0;
        for (int y = 0; y < pixels.GetLength(1); y++)
        {
            for (int x = 0; x < pixels.GetLength(0); x++)
            {
                Point sample = new(left + x + 0.5, top + y + 0.5);
                bool inside = polygonArray.Any(points => IsPointInsidePolygon(sample, points));
                pixels[x, y] = inside;
                if (inside)
                {
                    pixelCount++;
                }
            }
        }

        if (pixelCount < 3)
        {
            return false;
        }

        mask = new RoiMask(left, top, pixels, pixelCount);
        return true;
    }

    private static void ApplyBallToMask(bool[,] pixels, int centerX, int centerY, int radius, bool add)
    {
        int width = pixels.GetLength(0);
        int height = pixels.GetLength(1);
        int radiusSquared = radius * radius;
        for (int y = Math.Max(0, centerY - radius); y <= Math.Min(height - 1, centerY + radius); y++)
        {
            for (int x = Math.Max(0, centerX - radius); x <= Math.Min(width - 1, centerX + radius); x++)
            {
                int dx = x - centerX;
                int dy = y - centerY;
                if ((dx * dx) + (dy * dy) > radiusSquared)
                {
                    continue;
                }

                pixels[x, y] = add;
            }
        }
    }

    private static bool DoesBrushIntersectRoiEdge(bool[,] pixels, int centerX, int centerY, int radius)
    {
        int width = pixels.GetLength(0);
        int height = pixels.GetLength(1);
        bool hasInside = false;
        bool hasOutside = false;
        int radiusSquared = radius * radius;
        for (int y = Math.Max(0, centerY - radius); y <= Math.Min(height - 1, centerY + radius); y++)
        {
            for (int x = Math.Max(0, centerX - radius); x <= Math.Min(width - 1, centerX + radius); x++)
            {
                int dx = x - centerX;
                int dy = y - centerY;
                if ((dx * dx) + (dy * dy) > radiusSquared)
                {
                    continue;
                }

                if (pixels[x, y])
                {
                    hasInside = true;
                }
                else
                {
                    hasOutside = true;
                }

                if (hasInside && hasOutside)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool[,] CloneMask(bool[,] source)
    {
        int width = source.GetLength(0);
        int height = source.GetLength(1);
        bool[,] clone = new bool[width, height];
        Array.Copy(source, clone, source.Length);
        return clone;
    }

    private List<RoiMaskComponent> ExtractMaskComponents(bool[,] pixels, int leftOffset, int topOffset)
    {
        int width = pixels.GetLength(0);
        int height = pixels.GetLength(1);
        bool[,] visited = new bool[width, height];
        List<RoiMaskComponent> components = [];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!pixels[x, y] || visited[x, y])
                {
                    continue;
                }

                Queue<(int X, int Y)> queue = new();
                List<(int X, int Y)> points = [];
                queue.Enqueue((x, y));
                visited[x, y] = true;
                int left = x;
                int right = x;
                int top = y;
                int bottom = y;
                double sumX = 0;
                double sumY = 0;

                while (queue.Count > 0)
                {
                    (int currentX, int currentY) = queue.Dequeue();
                    if (!pixels[currentX, currentY])
                    {
                        continue;
                    }

                    points.Add((currentX, currentY));
                    left = Math.Min(left, currentX);
                    right = Math.Max(right, currentX);
                    top = Math.Min(top, currentY);
                    bottom = Math.Max(bottom, currentY);
                    sumX += currentX + 0.5;
                    sumY += currentY + 0.5;

                    for (int offsetY = -1; offsetY <= 1; offsetY++)
                    {
                        for (int offsetX = -1; offsetX <= 1; offsetX++)
                        {
                            if (offsetX == 0 && offsetY == 0)
                            {
                                continue;
                            }

                            int nextX = currentX + offsetX;
                            int nextY = currentY + offsetY;
                            if ((uint)nextX >= (uint)width || (uint)nextY >= (uint)height || visited[nextX, nextY])
                            {
                                continue;
                            }

                            visited[nextX, nextY] = true;
                            queue.Enqueue((nextX, nextY));
                        }
                    }
                }

                if (points.Count < 3)
                {
                    continue;
                }

                bool[,] componentPixels = new bool[right - left + 1, bottom - top + 1];
                foreach ((int pointX, int pointY) in points)
                {
                    componentPixels[pointX - left, pointY - top] = true;
                }

                components.Add(new RoiMaskComponent(
                    left + leftOffset,
                    top + topOffset,
                    componentPixels,
                    points.Count,
                    new Point((sumX / points.Count) + leftOffset, (sumY / points.Count) + topOffset)));
            }
        }

        return components;
    }

    private RoiMaskComponent ChoosePrimaryComponent(List<RoiMaskComponent> components, Point originalCentroid, Point imagePoint, bool addRegion)
    {
        if (components.Count == 1)
        {
            return components[0];
        }

        Point preferredPoint = addRegion ? imagePoint : originalCentroid;
        return components
            .OrderBy(component => Distance(GetGlobalCentroid(component), preferredPoint))
            .ThenByDescending(component => component.PixelCount)
            .First();
    }

    private static Point GetGlobalCentroid(RoiMaskComponent component) => component.Centroid;

    private bool IsContourOnCurrentSlice(VolumeRoiContour contour)
    {
        if (SpatialMetadata is null)
        {
            return false;
        }

        return string.Equals(
            BuildVolumeRoiSliceKey(SpatialMetadata.SeriesInstanceUid, SpatialMetadata.FrameOfReferenceUid, contour.PlanePosition),
            GetCurrentVolumeRoiSliceKey(SpatialMetadata),
            StringComparison.Ordinal);
    }
}

internal static class RoiBallCorrectionExtensions
{
    public static bool ContainsPoint(this bool[,] pixels, int x, int y)
    {
        return (uint)x < (uint)pixels.GetLength(0) && (uint)y < (uint)pixels.GetLength(1) && pixels[x, y];
    }
}