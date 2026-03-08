using System.Globalization;
using KPACS.DCMClasses;
using KPACS.DCMClasses.Models;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

internal static class LegacyStudyInfoMapper
{
    public static void PopulateLegacyStudyInfo(this StudyDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        details.LegacyStudy = details.ToLegacyStudyInfo();
    }

    public static StudyInfo ToLegacyStudyInfo(this StudyDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);

        var legacyStudy = new StudyInfo
        {
            PatientName = details.Study.PatientName,
            PatientId = details.Study.PatientId,
            PatientBD = details.Study.PatientBirthDate,
            StudyDate = details.Study.StudyDate,
            StudyDescription = details.Study.StudyDescription,
            StudyInstanceUid = details.Study.StudyInstanceUid,
            PhysiciansName = details.Study.ReferringPhysician,
            Modalities = details.Study.Modalities,
            AccessionNumber = details.Study.AccessionNumber,
            Local = !details.Study.IsPreviewOnly,
        };

        foreach (SeriesRecord series in details.Series.OrderBy(item => item.SeriesNumber).ThenBy(item => item.SeriesDescription))
        {
            var legacySeries = new SeriesInfo
            {
                SerDesc = series.SeriesDescription,
                SeriesNumber = series.SeriesNumber.ToString(CultureInfo.InvariantCulture),
                SerModality = ResolveModality(series.Modality, series.Instances.FirstOrDefault()?.SopClassUid),
                SerInstUid = series.SeriesInstanceUid,
                StudyInstanceUid = details.Study.StudyInstanceUid,
            };

            foreach (InstanceRecord instance in series.Instances.OrderBy(item => item.InstanceNumber).ThenBy(item => item.FilePath))
            {
                legacySeries.Images.Add(new ImageInfo
                {
                    SopInstUid = instance.SopInstanceUid,
                    SopClassUid = instance.SopClassUid,
                    SeriesNumber = series.SeriesNumber.ToString(CultureInfo.InvariantCulture),
                    SerInstUid = series.SeriesInstanceUid,
                    StudyInstUid = details.Study.StudyInstanceUid,
                    ImgFilename = instance.FilePath,
                    ImageNumber = instance.InstanceNumber > 0 ? instance.InstanceNumber.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    NumberOfFrames = instance.FrameCount > 0 ? instance.FrameCount.ToString(CultureInfo.InvariantCulture) : string.Empty,
                });
            }

            legacyStudy.Series.Add(legacySeries);
        }

        if (string.IsNullOrWhiteSpace(legacyStudy.Modalities))
        {
            legacyStudy.Modalities = string.Join(", ",
                legacyStudy.Series
                    .Select(series => series.SerModality)
                    .Where(modality => !string.IsNullOrWhiteSpace(modality))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(modality => modality));
        }

        return legacyStudy;
    }

    public static string ResolveModality(string? explicitModality, string? sopClassUid)
    {
        if (!string.IsNullOrWhiteSpace(explicitModality))
        {
            return explicitModality;
        }

        if (string.IsNullOrWhiteSpace(sopClassUid))
        {
            return string.Empty;
        }

        return DicomBaseObject.GetModalityFromSOPClass(sopClassUid);
    }
}