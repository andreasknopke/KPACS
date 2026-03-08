using System.Globalization;
using Microsoft.Data.Sqlite;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class ImageboxRepository
{
    private readonly string _connectionString;

    public ImageboxRepository(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        string sql = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS studies (
                study_key INTEGER PRIMARY KEY AUTOINCREMENT,
                study_instance_uid TEXT NOT NULL UNIQUE,
                patient_name TEXT NOT NULL,
                patient_id TEXT NOT NULL,
                patient_birth_date TEXT NOT NULL DEFAULT '',
                accession_number TEXT NOT NULL,
                study_description TEXT NOT NULL,
                referring_physician TEXT NOT NULL,
                study_date TEXT NOT NULL,
                modalities TEXT NOT NULL,
                storage_path TEXT NOT NULL,
                imported_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS series (
                series_key INTEGER PRIMARY KEY AUTOINCREMENT,
                study_key INTEGER NOT NULL,
                series_instance_uid TEXT NOT NULL UNIQUE,
                modality TEXT NOT NULL,
                series_description TEXT NOT NULL,
                series_number INTEGER NOT NULL,
                instance_count INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (study_key) REFERENCES studies(study_key) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS instances (
                instance_key INTEGER PRIMARY KEY AUTOINCREMENT,
                series_key INTEGER NOT NULL,
                sop_instance_uid TEXT NOT NULL UNIQUE,
                file_path TEXT NOT NULL,
                instance_number INTEGER NOT NULL,
                frame_count INTEGER NOT NULL DEFAULT 1,
                FOREIGN KEY (series_key) REFERENCES series(series_key) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_studies_patient_name ON studies(patient_name);
            CREATE INDEX IF NOT EXISTS ix_studies_patient_id ON studies(patient_id);
            CREATE INDEX IF NOT EXISTS ix_studies_study_date ON studies(study_date);
            CREATE INDEX IF NOT EXISTS ix_series_study_key ON series(study_key);
            CREATE INDEX IF NOT EXISTS ix_instances_series_key ON instances(series_key);
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await EnsureColumnExistsAsync(connection, "studies", "patient_birth_date", "TEXT NOT NULL DEFAULT ''", cancellationToken);
    }

    public async Task<List<StudyListItem>> SearchStudiesAsync(StudyQuery query, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = BuildSearchSql(query);
        AddQueryParameters(command, query);

        var results = new List<StudyListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new StudyListItem
            {
                StudyKey = reader.GetInt64(0),
                StudyInstanceUid = reader.GetString(1),
                PatientName = reader.GetString(2),
                PatientId = reader.GetString(3),
                PatientBirthDate = reader.GetString(4),
                AccessionNumber = reader.GetString(5),
                StudyDescription = reader.GetString(6),
                ReferringPhysician = reader.GetString(7),
                StudyDate = reader.GetString(8),
                Modalities = reader.GetString(9),
                StoragePath = reader.GetString(10),
                ImportedAtUtc = ParseImportedAtUtc(reader.GetString(11)),
                SeriesCount = reader.GetInt32(12),
                InstanceCount = reader.GetInt32(13),
            });
        }

        return results;
    }

    public async Task<StudyDetails?> GetStudyDetailsAsync(long studyKey, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        StudyListItem? study = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                  SELECT s.study_key, s.study_instance_uid, s.patient_name, s.patient_id, s.patient_birth_date, s.accession_number,
                       s.study_description, s.referring_physician, s.study_date, s.modalities,
                       s.storage_path, s.imported_at_utc,
                       COALESCE((SELECT COUNT(*) FROM series sr WHERE sr.study_key = s.study_key), 0),
                       COALESCE((SELECT COUNT(*) FROM instances i JOIN series sr ON sr.series_key = i.series_key WHERE sr.study_key = s.study_key), 0)
                FROM studies s
                WHERE s.study_key = $studyKey
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$studyKey", studyKey);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                study = new StudyListItem
                {
                    StudyKey = reader.GetInt64(0),
                    StudyInstanceUid = reader.GetString(1),
                    PatientName = reader.GetString(2),
                    PatientId = reader.GetString(3),
                    PatientBirthDate = reader.GetString(4),
                    AccessionNumber = reader.GetString(5),
                    StudyDescription = reader.GetString(6),
                    ReferringPhysician = reader.GetString(7),
                    StudyDate = reader.GetString(8),
                    Modalities = reader.GetString(9),
                    StoragePath = reader.GetString(10),
                    ImportedAtUtc = ParseImportedAtUtc(reader.GetString(11)),
                    SeriesCount = reader.GetInt32(12),
                    InstanceCount = reader.GetInt32(13),
                };
            }
        }

        if (study is null)
        {
            return null;
        }

        var details = new StudyDetails { Study = study };
        var seriesLookup = new Dictionary<long, SeriesRecord>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT sr.series_key, sr.study_key, sr.series_instance_uid, sr.modality,
                       sr.series_description, sr.series_number, sr.instance_count,
                       i.instance_key, i.series_key, i.sop_instance_uid, i.file_path, i.instance_number, i.frame_count
                FROM series sr
                LEFT JOIN instances i ON i.series_key = sr.series_key
                WHERE sr.study_key = $studyKey
                ORDER BY sr.series_number, sr.series_description, i.instance_number, i.file_path;
                """;
            command.Parameters.AddWithValue("$studyKey", studyKey);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                long seriesKey = reader.GetInt64(0);
                if (!seriesLookup.TryGetValue(seriesKey, out SeriesRecord? series))
                {
                    series = new SeriesRecord
                    {
                        SeriesKey = seriesKey,
                        StudyKey = reader.GetInt64(1),
                        SeriesInstanceUid = reader.GetString(2),
                        Modality = reader.GetString(3),
                        SeriesDescription = reader.GetString(4),
                        SeriesNumber = reader.GetInt32(5),
                        InstanceCount = reader.GetInt32(6),
                    };
                    seriesLookup.Add(seriesKey, series);
                    details.Series.Add(series);
                }

                if (reader.IsDBNull(7))
                {
                    continue;
                }

                series.Instances.Add(new InstanceRecord
                {
                    InstanceKey = reader.GetInt64(7),
                    SeriesKey = reader.GetInt64(8),
                    SopInstanceUid = reader.GetString(9),
                    FilePath = reader.GetString(10),
                    InstanceNumber = reader.GetInt32(11),
                    FrameCount = reader.GetInt32(12),
                });
            }
        }

        details.PopulateLegacyStudyInfo();

        return details;
    }

    public async Task<StudyDetails?> GetStudyDetailsByStudyInstanceUidAsync(string studyInstanceUid, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT study_key FROM studies WHERE study_instance_uid = $studyInstanceUid LIMIT 1;";
        command.Parameters.AddWithValue("$studyInstanceUid", studyInstanceUid);

        object? scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar == DBNull.Value)
        {
            return null;
        }

        return await GetStudyDetailsAsync((long)scalar, cancellationToken);
    }

    public async Task<long> UpsertStudyAsync(StudyListItem study, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO studies(study_instance_uid, patient_name, patient_id, patient_birth_date, accession_number, study_description,
                                referring_physician, study_date, modalities, storage_path, imported_at_utc, updated_at_utc)
            VALUES ($uid, $patientName, $patientId, $patientBirthDate, $accessionNumber, $studyDescription, $refPhys, $studyDate,
                    $modalities, $storagePath, $importedAtUtc, $updatedAtUtc)
            ON CONFLICT(study_instance_uid) DO UPDATE SET
                patient_name = excluded.patient_name,
                patient_id = excluded.patient_id,
                patient_birth_date = excluded.patient_birth_date,
                accession_number = excluded.accession_number,
                study_description = excluded.study_description,
                referring_physician = excluded.referring_physician,
                study_date = excluded.study_date,
                modalities = excluded.modalities,
                storage_path = excluded.storage_path,
                updated_at_utc = excluded.updated_at_utc
            RETURNING study_key;
            """;
        FillStudyParameters(command, study);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    public async Task<long> UpsertSeriesAsync(long studyKey, SeriesRecord series, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO series(study_key, series_instance_uid, modality, series_description, series_number, instance_count)
            VALUES ($studyKey, $uid, $modality, $seriesDescription, $seriesNumber, $instanceCount)
            ON CONFLICT(series_instance_uid) DO UPDATE SET
                study_key = excluded.study_key,
                modality = excluded.modality,
                series_description = excluded.series_description,
                series_number = excluded.series_number,
                instance_count = excluded.instance_count
            RETURNING series_key;
            """;
        command.Parameters.AddWithValue("$studyKey", studyKey);
        command.Parameters.AddWithValue("$uid", series.SeriesInstanceUid);
        command.Parameters.AddWithValue("$modality", series.Modality);
        command.Parameters.AddWithValue("$seriesDescription", series.SeriesDescription);
        command.Parameters.AddWithValue("$seriesNumber", series.SeriesNumber);
        command.Parameters.AddWithValue("$instanceCount", series.InstanceCount);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    public async Task UpsertInstanceAsync(long seriesKey, InstanceRecord instance, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO instances(series_key, sop_instance_uid, file_path, instance_number, frame_count)
            VALUES ($seriesKey, $uid, $filePath, $instanceNumber, $frameCount)
            ON CONFLICT(sop_instance_uid) DO UPDATE SET
                series_key = excluded.series_key,
                file_path = excluded.file_path,
                instance_number = excluded.instance_number,
                frame_count = excluded.frame_count;
            """;
        command.Parameters.AddWithValue("$seriesKey", seriesKey);
        command.Parameters.AddWithValue("$uid", instance.SopInstanceUid);
        command.Parameters.AddWithValue("$filePath", instance.FilePath);
        command.Parameters.AddWithValue("$instanceNumber", instance.InstanceNumber);
        command.Parameters.AddWithValue("$frameCount", instance.FrameCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateStudyAfterPseudonymizeAsync(long studyKey, PseudonymizeRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE studies
            SET patient_name = $patientName,
                patient_id = $patientId,
                patient_birth_date = $patientBirthDate,
                accession_number = $accessionNumber,
                referring_physician = $refPhys,
                updated_at_utc = $updatedAtUtc
            WHERE study_key = $studyKey;
            """;
        command.Parameters.AddWithValue("$studyKey", studyKey);
        command.Parameters.AddWithValue("$patientName", request.PatientName ?? string.Empty);
        command.Parameters.AddWithValue("$patientId", request.PatientId ?? string.Empty);
        command.Parameters.AddWithValue("$patientBirthDate", request.PatientBirthDate ?? string.Empty);
        command.Parameters.AddWithValue("$accessionNumber", request.AccessionNumber ?? string.Empty);
        command.Parameters.AddWithValue("$refPhys", request.ReferringPhysician ?? string.Empty);
        command.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteStudyAsync(long studyKey, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM studies WHERE study_key = $studyKey;";
        command.Parameters.AddWithValue("$studyKey", studyKey);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnExistsAsync(SqliteConnection connection, string tableName, string columnName, string columnSql, CancellationToken cancellationToken)
    {
        await using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = $"PRAGMA table_info({tableName});";

        bool exists = false;
        await using (var reader = await pragmaCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnSql};";
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildSearchSql(StudyQuery query)
    {
        var whereParts = new List<string> { "1 = 1" };
        if (!string.IsNullOrWhiteSpace(query.PatientId)) whereParts.Add("s.patient_id LIKE $patientId");
        if (!string.IsNullOrWhiteSpace(query.PatientName)) whereParts.Add("s.patient_name LIKE $patientName");
        if (!string.IsNullOrWhiteSpace(query.PatientBirthDate)) whereParts.Add("s.patient_birth_date = $patientBirthDate");
        if (!string.IsNullOrWhiteSpace(query.AccessionNumber)) whereParts.Add("s.accession_number LIKE $accessionNumber");
        if (!string.IsNullOrWhiteSpace(query.ReferringPhysician)) whereParts.Add("s.referring_physician LIKE $refPhysician");
        if (!string.IsNullOrWhiteSpace(query.StudyDescription)) whereParts.Add("s.study_description LIKE $studyDescription");
        if (!string.IsNullOrWhiteSpace(query.QuickSearch))
        {
            whereParts.Add("(s.patient_name LIKE $quickSearch OR s.patient_id LIKE $quickSearch OR s.study_description LIKE $quickSearch OR s.modalities LIKE $quickSearch)");
        }
        if (query.Modalities.Count > 0)
        {
            whereParts.Add("(" + string.Join(" OR ", query.Modalities.Select((_, index) => $"s.modalities LIKE $modality{index}")) + ")");
        }
        if (query.FromStudyDate is not null) whereParts.Add("s.study_date >= $fromStudyDate");
        if (query.ToStudyDate is not null) whereParts.Add("s.study_date <= $toStudyDate");

        return $"""
            SELECT s.study_key, s.study_instance_uid, s.patient_name, s.patient_id, s.patient_birth_date, s.accession_number,
                   s.study_description, s.referring_physician, s.study_date, s.modalities,
                   s.storage_path, s.imported_at_utc,
                   COALESCE((SELECT COUNT(*) FROM series sr WHERE sr.study_key = s.study_key), 0) AS series_count,
                   COALESCE((SELECT COUNT(*) FROM instances i JOIN series sr ON sr.series_key = i.series_key WHERE sr.study_key = s.study_key), 0) AS instance_count
            FROM studies s
            WHERE {string.Join(" AND ", whereParts)}
            ORDER BY s.study_date DESC, s.imported_at_utc DESC, s.patient_name ASC;
            """;
    }

    private static DateTime ParseImportedAtUtc(string value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed))
        {
            return parsed;
        }

        return DateTime.UtcNow;
    }

    private static void AddQueryParameters(SqliteCommand command, StudyQuery query)
    {
        command.Parameters.AddWithValue("$patientId", Like(query.PatientId));
        command.Parameters.AddWithValue("$patientName", Like(query.PatientName));
        command.Parameters.AddWithValue("$patientBirthDate", query.PatientBirthDate.Trim());
        command.Parameters.AddWithValue("$accessionNumber", Like(query.AccessionNumber));
        command.Parameters.AddWithValue("$refPhysician", Like(query.ReferringPhysician));
        command.Parameters.AddWithValue("$studyDescription", Like(query.StudyDescription));
        command.Parameters.AddWithValue("$quickSearch", Like(query.QuickSearch));
        for (int index = 0; index < query.Modalities.Count; index++)
        {
            command.Parameters.AddWithValue($"$modality{index}", Like(query.Modalities[index]));
        }
        command.Parameters.AddWithValue("$fromStudyDate", query.FromStudyDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty);
        command.Parameters.AddWithValue("$toStudyDate", query.ToStudyDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? "99999999");
    }

    private static string Like(string value) => $"%{value?.Trim() ?? string.Empty}%";

    private static void FillStudyParameters(SqliteCommand command, StudyListItem study)
    {
        command.Parameters.AddWithValue("$uid", study.StudyInstanceUid);
        command.Parameters.AddWithValue("$patientName", study.PatientName);
        command.Parameters.AddWithValue("$patientId", study.PatientId);
        command.Parameters.AddWithValue("$patientBirthDate", study.PatientBirthDate);
        command.Parameters.AddWithValue("$accessionNumber", study.AccessionNumber);
        command.Parameters.AddWithValue("$studyDescription", study.StudyDescription);
        command.Parameters.AddWithValue("$refPhys", study.ReferringPhysician);
        command.Parameters.AddWithValue("$studyDate", study.StudyDate);
        command.Parameters.AddWithValue("$modalities", study.Modalities);
        command.Parameters.AddWithValue("$storagePath", study.StoragePath);
        command.Parameters.AddWithValue("$importedAtUtc", study.ImportedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }
}
