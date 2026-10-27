using System.Text.Json;
using Microsoft.Data.Sqlite;
using Project_no._1.Api;

namespace Project_no._1.Services;

public sealed class SqliteMissionStore(IConfiguration configuration, IWebHostEnvironment environment) : IMissionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString = BuildConnectionString(configuration, environment);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var connectionStringBuilder = new SqliteConnectionStringBuilder(_connectionString);
        var databasePath = connectionStringBuilder.DataSource;
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            SetDirectoryPermissions(directory);
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS missions (
                id TEXT PRIMARY KEY,
                owner_id TEXT NOT NULL,
                title TEXT NOT NULL,
                goal TEXT NOT NULL,
                outcome TEXT NOT NULL,
                success_criteria TEXT NOT NULL,
                constraints_text TEXT NOT NULL,
                available_time TEXT NOT NULL,
                deadline TEXT,
                status TEXT NOT NULL,
                summary TEXT NOT NULL,
                next_action TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_missions_owner_updated ON missions(owner_id, updated_at DESC);
            CREATE TABLE IF NOT EXISTS mission_tasks (
                id TEXT PRIMARY KEY,
                mission_id TEXT NOT NULL REFERENCES missions(id) ON DELETE CASCADE,
                title TEXT NOT NULL,
                description TEXT NOT NULL,
                priority TEXT NOT NULL,
                status TEXT NOT NULL,
                due_date TEXT,
                subtasks_json TEXT NOT NULL,
                position INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tasks_mission_position ON mission_tasks(mission_id, position);
            CREATE TABLE IF NOT EXISTS mission_documents (
                id TEXT PRIMARY KEY,
                mission_id TEXT NOT NULL REFERENCES missions(id) ON DELETE CASCADE,
                file_name TEXT NOT NULL,
                passages_json TEXT NOT NULL,
                passage_count INTEGER NOT NULL,
                character_count INTEGER NOT NULL,
                added_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS mission_deliverables (
                id TEXT PRIMARY KEY,
                mission_id TEXT NOT NULL REFERENCES missions(id) ON DELETE CASCADE,
                task_id TEXT NOT NULL,
                title TEXT NOT NULL,
                content TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS mission_memory (
                id TEXT PRIMARY KEY,
                mission_id TEXT NOT NULL REFERENCES missions(id) ON DELETE CASCADE,
                category TEXT NOT NULL,
                content TEXT NOT NULL,
                provenance TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS mission_memory_revisions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                memory_id TEXT NOT NULL REFERENCES mission_memory(id) ON DELETE CASCADE,
                mission_id TEXT NOT NULL REFERENCES missions(id) ON DELETE CASCADE,
                category TEXT NOT NULL,
                content TEXT NOT NULL,
                provenance TEXT NOT NULL,
                changed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS mission_activity (
                id TEXT PRIMARY KEY,
                mission_id TEXT NOT NULL REFERENCES missions(id) ON DELETE CASCADE,
                kind TEXT NOT NULL,
                message TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (!OperatingSystem.IsWindows() && File.Exists(databasePath))
        {
            File.SetUnixFileMode(databasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public async Task<IReadOnlyList<MissionSummaryResponse>> ListAsync(string ownerId, bool includeArchived, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.id, m.title, m.goal, m.status, m.deadline, m.updated_at,
                   COUNT(t.id) AS task_count, COALESCE(SUM(CASE WHEN t.status = 'done' THEN 1 ELSE 0 END), 0) AS completed_count
            FROM missions m
            LEFT JOIN mission_tasks t ON t.mission_id = m.id
            WHERE m.owner_id = $owner AND ($archived = 1 OR m.status <> 'archived')
            GROUP BY m.id
            ORDER BY m.updated_at DESC;
            """;
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$archived", includeArchived ? 1 : 0);
        var results = new List<MissionSummaryResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var taskCount = reader.GetInt32(6);
            var completed = reader.GetInt32(7);
            results.Add(new MissionSummaryResponse(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                NullableString(reader, 4), ParseDate(reader.GetString(5)), taskCount, completed,
                taskCount == 0 ? 0 : (int)Math.Round(completed * 100d / taskCount)));
        }

        return results;
    }

    public async Task<MissionResponse?> GetAsync(string ownerId, string missionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        MissionResponse? mission;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, title, goal, outcome, success_criteria, constraints_text, available_time,
                       deadline, status, summary, next_action, created_at, updated_at
                FROM missions WHERE owner_id = $owner AND id = $id;
                """;
            command.Parameters.AddWithValue("$owner", ownerId);
            command.Parameters.AddWithValue("$id", missionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            mission = new MissionResponse(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                DeserializeStrings(reader.GetString(4)), reader.GetString(5), reader.GetString(6),
                NullableString(reader, 7), reader.GetString(8), reader.GetString(9), reader.GetString(10),
                ParseDate(reader.GetString(11)), ParseDate(reader.GetString(12)), [], [], [], [], []);
        }

        var tasks = new List<MissionTaskResponse>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, title, description, priority, status, due_date, subtasks_json, position
                FROM mission_tasks WHERE mission_id = $id ORDER BY position;
                """;
            command.Parameters.AddWithValue("$id", missionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tasks.Add(new MissionTaskResponse(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), NullableString(reader, 5), DeserializeStrings(reader.GetString(6)), reader.GetInt32(7)));
            }
        }

        var documents = new List<MissionDocumentResponse>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, file_name, passage_count, character_count, added_at
                FROM mission_documents WHERE mission_id = $id ORDER BY added_at DESC;
                """;
            command.Parameters.AddWithValue("$id", missionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                documents.Add(new MissionDocumentResponse(
                    reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), ParseDate(reader.GetString(4))));
            }
        }

        var deliverables = new List<MissionDeliverableResponse>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, task_id, title, content, created_at FROM mission_deliverables
                WHERE mission_id = $id ORDER BY created_at DESC LIMIT 100;
                """;
            command.Parameters.AddWithValue("$id", missionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                deliverables.Add(new MissionDeliverableResponse(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), ParseDate(reader.GetString(4))));
            }
        }

        var memory = new List<MissionMemoryResponse>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, category, content, provenance, updated_at FROM mission_memory
                WHERE mission_id = $id ORDER BY updated_at DESC;
                """;
            command.Parameters.AddWithValue("$id", missionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                memory.Add(new MissionMemoryResponse(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), ParseDate(reader.GetString(4))));
            }
        }

        var activity = new List<MissionActivityResponse>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, kind, message, created_at FROM mission_activity
                WHERE mission_id = $id ORDER BY created_at DESC LIMIT 50;
                """;
            command.Parameters.AddWithValue("$id", missionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                activity.Add(new MissionActivityResponse(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseDate(reader.GetString(3))));
            }
        }

        return mission with
        {
            Tasks = tasks,
            Documents = documents,
            Deliverables = deliverables,
            Memory = memory,
            Activity = activity
        };
    }

    public async Task CreateAsync(string ownerId, MissionResponse mission, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO missions(id, owner_id, title, goal, outcome, success_criteria, constraints_text,
                    available_time, deadline, status, summary, next_action, created_at, updated_at)
                VALUES($id, $owner, $title, $goal, $outcome, $criteria, $constraints, $time, $deadline,
                    $status, $summary, $next, $created, $updated);
                """;
            command.Parameters.AddWithValue("$id", mission.Id);
            command.Parameters.AddWithValue("$owner", ownerId);
            command.Parameters.AddWithValue("$title", mission.Title);
            command.Parameters.AddWithValue("$goal", mission.Goal);
            command.Parameters.AddWithValue("$outcome", mission.ExpectedOutcome);
            command.Parameters.AddWithValue("$criteria", JsonSerializer.Serialize(mission.SuccessCriteria, JsonOptions));
            command.Parameters.AddWithValue("$constraints", mission.Constraints);
            command.Parameters.AddWithValue("$time", mission.AvailableTime);
            command.Parameters.AddWithValue("$deadline", (object?)mission.Deadline ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", "active");
            command.Parameters.AddWithValue("$summary", mission.Summary);
            command.Parameters.AddWithValue("$next", mission.NextAction);
            command.Parameters.AddWithValue("$created", FormatDate(mission.CreatedAt));
            command.Parameters.AddWithValue("$updated", FormatDate(mission.UpdatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var task in mission.Tasks)
        {
            await InsertTaskAsync(connection, (SqliteTransaction)transaction, mission.Id, task, cancellationToken);
        }

        await InsertActivityAsync(connection, (SqliteTransaction)transaction, mission.Id, "created", "Mission created.", mission.CreatedAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> UpdateMissionAsync(string ownerId, string missionId, MissionUpdateRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE missions SET title = $title, goal = $goal, outcome = $outcome, success_criteria = $criteria,
                constraints_text = $constraints, available_time = $time, deadline = $deadline, updated_at = $updated
            WHERE owner_id = $owner AND id = $id;
            """;
        command.Parameters.AddWithValue("$title", request.Title);
        command.Parameters.AddWithValue("$goal", request.Goal);
        command.Parameters.AddWithValue("$outcome", request.ExpectedOutcome);
        command.Parameters.AddWithValue("$criteria", JsonSerializer.Serialize(request.SuccessCriteria, JsonOptions));
        command.Parameters.AddWithValue("$constraints", request.Constraints);
        command.Parameters.AddWithValue("$time", request.AvailableTime);
        command.Parameters.AddWithValue("$deadline", (object?)request.Deadline ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", FormatDate(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$id", missionId);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (changed)
        {
            await AddActivityAsync(ownerId, missionId, "updated", "Mission overview updated.", cancellationToken);
        }

        return changed;
    }

    public async Task<bool> SetMissionStatusAsync(string ownerId, string missionId, string status, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE missions SET status = $status, updated_at = $updated WHERE owner_id = $owner AND id = $id;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updated", FormatDate(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$id", missionId);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (changed)
        {
            await AddActivityAsync(ownerId, missionId, "status", $"Mission marked {status}.", cancellationToken);
        }

        return changed;
    }

    public async Task<bool> DeleteAsync(string ownerId, string missionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM missions WHERE owner_id = $owner AND id = $id;";
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$id", missionId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> AddTaskAsync(string ownerId, string missionId, MissionTaskResponse task, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await OwnsMissionAsync(connection, (SqliteTransaction)transaction, ownerId, missionId, cancellationToken))
        {
            return false;
        }

        await InsertTaskAsync(connection, (SqliteTransaction)transaction, missionId, task, cancellationToken);
        await UpdateMissionTimestampAsync(connection, (SqliteTransaction)transaction, missionId, cancellationToken);
        await UpdateMissionTaskStateAsync(connection, (SqliteTransaction)transaction, missionId, cancellationToken);
        await InsertActivityAsync(connection, (SqliteTransaction)transaction, missionId, "task", $"Added task: {task.Title}", DateTimeOffset.UtcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UpdateTaskAsync(string ownerId, string missionId, string taskId, MissionTaskUpdateRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE mission_tasks SET title = $title, description = $description, priority = $priority,
                status = $status, due_date = $due, subtasks_json = $subtasks
            WHERE id = $task AND mission_id = $mission
              AND EXISTS(SELECT 1 FROM missions WHERE id = $mission AND owner_id = $owner);
            """;
        command.Parameters.AddWithValue("$title", request.Title);
        command.Parameters.AddWithValue("$description", request.Description);
        command.Parameters.AddWithValue("$priority", request.Priority);
        command.Parameters.AddWithValue("$status", request.Status);
        command.Parameters.AddWithValue("$due", (object?)request.DueDate ?? DBNull.Value);
        command.Parameters.AddWithValue("$subtasks", JsonSerializer.Serialize(request.Subtasks, JsonOptions));
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$mission", missionId);
        command.Parameters.AddWithValue("$owner", ownerId);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (!changed)
        {
            return false;
        }

        await UpdateMissionTimestampAsync(connection, (SqliteTransaction)transaction, missionId, cancellationToken);
        await InsertActivityAsync(connection, (SqliteTransaction)transaction, missionId, "task", $"Updated task: {request.Title}", DateTimeOffset.UtcNow, cancellationToken);
        await UpdateMissionTaskStateAsync(connection, (SqliteTransaction)transaction, missionId, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> MoveTaskAsync(string ownerId, string missionId, string taskId, string direction, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await OwnsMissionAsync(connection, (SqliteTransaction)transaction, ownerId, missionId, cancellationToken))
        {
            return false;
        }

        var ids = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = (SqliteTransaction)transaction;
            select.CommandText = "SELECT id FROM mission_tasks WHERE mission_id = $mission ORDER BY position;";
            select.Parameters.AddWithValue("$mission", missionId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(0));
            }
        }

        var index = ids.IndexOf(taskId);
        var target = direction == "up" ? index - 1 : index + 1;
        if (index < 0 || target < 0 || target >= ids.Count)
        {
            return false;
        }

        (ids[index], ids[target]) = (ids[target], ids[index]);
        for (var position = 0; position < ids.Count; position++)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = "UPDATE mission_tasks SET position = $position WHERE id = $id AND mission_id = $mission;";
            update.Parameters.AddWithValue("$position", position);
            update.Parameters.AddWithValue("$id", ids[position]);
            update.Parameters.AddWithValue("$mission", missionId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await UpdateMissionTimestampAsync(connection, (SqliteTransaction)transaction, missionId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteTaskAsync(string ownerId, string missionId, string taskId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            DELETE FROM mission_tasks WHERE id = $task AND mission_id = $mission
              AND EXISTS(SELECT 1 FROM missions WHERE id = $mission AND owner_id = $owner);
            """;
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$mission", missionId);
        command.Parameters.AddWithValue("$owner", ownerId);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (changed)
        {
            await using var reorder = connection.CreateCommand();
            reorder.Transaction = (SqliteTransaction)transaction;
            reorder.CommandText = """
                UPDATE mission_tasks SET position = (SELECT COUNT(*) FROM mission_tasks preceding
                  WHERE preceding.mission_id = mission_tasks.mission_id AND preceding.position < mission_tasks.position)
                WHERE mission_id = $mission;
                """;
            reorder.Parameters.AddWithValue("$mission", missionId);
            await reorder.ExecuteNonQueryAsync(cancellationToken);
            await UpdateMissionTimestampAsync(connection, (SqliteTransaction)transaction, missionId, cancellationToken);
            await UpdateMissionTaskStateAsync(connection, (SqliteTransaction)transaction, missionId, cancellationToken);
            await InsertActivityAsync(connection, (SqliteTransaction)transaction, missionId, "task", "Removed a task.", DateTimeOffset.UtcNow, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    public async Task<MissionDocumentAddResult> AddDocumentAsync(string ownerId, string missionId, string documentId, string fileName, IReadOnlyList<DocumentPassage> passages, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await OwnsMissionAsync(connection, (SqliteTransaction)transaction, ownerId, missionId, cancellationToken))
        {
            return MissionDocumentAddResult.MissionNotFound;
        }

        var documentCharacters = passages.Sum(passage => passage.Text.Length);
        await using (var usage = connection.CreateCommand())
        {
            usage.Transaction = (SqliteTransaction)transaction;
            usage.CommandText = """
                SELECT COUNT(*), COALESCE(SUM(character_count), 0)
                FROM mission_documents WHERE mission_id = $mission;
                """;
            usage.Parameters.AddWithValue("$mission", missionId);
            await using var reader = await usage.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (reader.GetInt32(0) >= 20 || reader.GetInt64(1) + documentCharacters > 500_000)
            {
                return MissionDocumentAddResult.CapacityExceeded;
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO mission_documents(id, mission_id, file_name, passages_json, passage_count, character_count, added_at)
                VALUES($id, $mission, $file, $passages, $count, $characters, $added);
                """;
            command.Parameters.AddWithValue("$id", documentId);
            command.Parameters.AddWithValue("$mission", missionId);
            command.Parameters.AddWithValue("$file", fileName);
            command.Parameters.AddWithValue("$passages", JsonSerializer.Serialize(passages, JsonOptions));
            command.Parameters.AddWithValue("$count", passages.Count);
            command.Parameters.AddWithValue("$characters", documentCharacters);
            command.Parameters.AddWithValue("$added", FormatDate(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await UpdateMissionTimestampAsync(connection, (SqliteTransaction)transaction, missionId, cancellationToken);
        await InsertActivityAsync(connection, (SqliteTransaction)transaction, missionId, "document", $"Attached document: {fileName}", DateTimeOffset.UtcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MissionDocumentAddResult.Added;
    }

    public async Task<bool> DeleteDocumentAsync(string ownerId, string missionId, string documentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM mission_documents WHERE id = $document AND mission_id = $mission
              AND EXISTS(SELECT 1 FROM missions WHERE id = $mission AND owner_id = $owner);
            """;
        command.Parameters.AddWithValue("$document", documentId);
        command.Parameters.AddWithValue("$mission", missionId);
        command.Parameters.AddWithValue("$owner", ownerId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<MissionDocumentPassage>> FindRelevantDocumentPassagesAsync(
        string ownerId, string missionId, string query, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var documents = new List<(string FileName, IReadOnlyList<DocumentPassage> Passages)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT d.file_name, d.passages_json FROM mission_documents d
                JOIN missions m ON m.id = d.mission_id
                WHERE d.mission_id = $mission AND m.owner_id = $owner;
                """;
            command.Parameters.AddWithValue("$mission", missionId);
            command.Parameters.AddWithValue("$owner", ownerId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                documents.Add((reader.GetString(0), DeserializePassages(reader.GetString(1))));
            }
        }

        return documents
            .SelectMany(document => RankPassages(document.FileName, document.Passages, query))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Passage.Number)
            .Take(limit)
            .Select(item => new MissionDocumentPassage(item.FileName, item.Passage))
            .ToArray();
    }

    public async Task<bool> AddDeliverableAsync(string ownerId, string missionId, MissionDeliverableResponse deliverable, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await OwnsMissionAsync(connection, (SqliteTransaction)transaction, ownerId, missionId, cancellationToken))
        {
            return false;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO mission_deliverables(id, mission_id, task_id, title, content, created_at)
                VALUES($id, $mission, $task, $title, $content, $created);
                """;
            command.Parameters.AddWithValue("$id", deliverable.Id);
            command.Parameters.AddWithValue("$mission", missionId);
            command.Parameters.AddWithValue("$task", deliverable.TaskId);
            command.Parameters.AddWithValue("$title", deliverable.Title);
            command.Parameters.AddWithValue("$content", deliverable.Content);
            command.Parameters.AddWithValue("$created", FormatDate(deliverable.CreatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await UpdateMissionTimestampAsync(connection, (SqliteTransaction)transaction, missionId, cancellationToken);
        await InsertActivityAsync(connection, (SqliteTransaction)transaction, missionId, "deliverable", $"Created deliverable: {deliverable.Title}", DateTimeOffset.UtcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteDeliverableAsync(string ownerId, string missionId, string deliverableId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM mission_deliverables WHERE id = $deliverable AND mission_id = $mission
              AND EXISTS(SELECT 1 FROM missions WHERE id = $mission AND owner_id = $owner);
            """;
        command.Parameters.AddWithValue("$deliverable", deliverableId);
        command.Parameters.AddWithValue("$mission", missionId);
        command.Parameters.AddWithValue("$owner", ownerId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> AddMemoryAsync(string ownerId, string missionId, MissionMemoryResponse memory, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await OwnsMissionAsync(connection, (SqliteTransaction)transaction, ownerId, missionId, cancellationToken))
        {
            return false;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO mission_memory(id, mission_id, category, content, provenance, updated_at)
                VALUES($id, $mission, $category, $content, $provenance, $updated);
                """;
            command.Parameters.AddWithValue("$id", memory.Id);
            command.Parameters.AddWithValue("$mission", missionId);
            command.Parameters.AddWithValue("$category", memory.Category);
            command.Parameters.AddWithValue("$content", memory.Content);
            command.Parameters.AddWithValue("$provenance", "user-confirmed");
            command.Parameters.AddWithValue("$updated", FormatDate(memory.UpdatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertActivityAsync(connection, (SqliteTransaction)transaction, missionId, "memory", $"Added project memory: {memory.Category}.", DateTimeOffset.UtcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UpdateMemoryAsync(string ownerId, string missionId, string memoryId, string category, string content, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = FormatDate(DateTimeOffset.UtcNow);
        await using (var revision = connection.CreateCommand())
        {
            revision.Transaction = (SqliteTransaction)transaction;
            revision.CommandText = """
                INSERT INTO mission_memory_revisions(memory_id, mission_id, category, content, provenance, changed_at)
                SELECT id, mission_id, category, content, provenance, $changed FROM mission_memory
                WHERE id = $memory AND mission_id = $mission
                  AND EXISTS(SELECT 1 FROM missions WHERE id = $mission AND owner_id = $owner);
                """;
            revision.Parameters.AddWithValue("$changed", now);
            revision.Parameters.AddWithValue("$memory", memoryId);
            revision.Parameters.AddWithValue("$mission", missionId);
            revision.Parameters.AddWithValue("$owner", ownerId);
            await revision.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText = """
            UPDATE mission_memory SET category = $category, content = $content, updated_at = $updated
            WHERE id = $memory AND mission_id = $mission
              AND EXISTS(SELECT 1 FROM missions WHERE id = $mission AND owner_id = $owner);
            """;
        update.Parameters.AddWithValue("$category", category);
        update.Parameters.AddWithValue("$content", content);
        update.Parameters.AddWithValue("$updated", now);
        update.Parameters.AddWithValue("$memory", memoryId);
        update.Parameters.AddWithValue("$mission", missionId);
        update.Parameters.AddWithValue("$owner", ownerId);
        var changed = await update.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (changed)
        {
            await InsertActivityAsync(connection, (SqliteTransaction)transaction, missionId, "memory", "Edited a project memory entry; its previous revision was retained.", DateTimeOffset.UtcNow, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    public async Task<bool> DeleteMemoryAsync(string ownerId, string missionId, string memoryId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM mission_memory WHERE id = $memory AND mission_id = $mission
              AND EXISTS(SELECT 1 FROM missions WHERE id = $mission AND owner_id = $owner);
            """;
        command.Parameters.AddWithValue("$memory", memoryId);
        command.Parameters.AddWithValue("$mission", missionId);
        command.Parameters.AddWithValue("$owner", ownerId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> ClearMemoryAsync(string ownerId, string missionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await OwnsMissionAsync(connection, (SqliteTransaction)transaction, ownerId, missionId, cancellationToken))
        {
            return false;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM mission_memory WHERE mission_id = $mission;";
        command.Parameters.AddWithValue("$mission", missionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await InsertActivityAsync(connection, (SqliteTransaction)transaction, missionId, "memory", "Cleared project memory.", DateTimeOffset.UtcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> AddActivityAsync(string ownerId, string missionId, string kind, string message, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await OwnsMissionAsync(connection, (SqliteTransaction)transaction, ownerId, missionId, cancellationToken))
        {
            return false;
        }

        await InsertActivityAsync(connection, (SqliteTransaction)transaction, missionId, kind, message, DateTimeOffset.UtcNow, cancellationToken);
        await UpdateMissionTimestampAsync(connection, (SqliteTransaction)transaction, missionId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string BuildConnectionString(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var databasePath = configuration["TITANAI_DB_PATH"];
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            var userDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            databasePath = Path.Combine(
                string.IsNullOrWhiteSpace(userDataPath) ? environment.ContentRootPath : userDataPath,
                "TitanAI",
                "missions.db");
        }

        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    private static void SetDirectoryPermissions(string directory)
    {
        if (!OperatingSystem.IsWindows() &&
            string.Equals(Path.GetFileName(directory), "TitanAI", StringComparison.Ordinal))
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static async Task<bool> OwnsMissionAsync(SqliteConnection connection, SqliteTransaction? transaction, string ownerId, string missionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM missions WHERE id = $mission AND owner_id = $owner);";
        command.Parameters.AddWithValue("$mission", missionId);
        command.Parameters.AddWithValue("$owner", ownerId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task InsertTaskAsync(SqliteConnection connection, SqliteTransaction transaction, string missionId, MissionTaskResponse task, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mission_tasks(id, mission_id, title, description, priority, status, due_date, subtasks_json, position)
            VALUES($id, $mission, $title, $description, $priority, $status, $due, $subtasks, $position);
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$mission", missionId);
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$description", task.Description);
        command.Parameters.AddWithValue("$priority", task.Priority);
        command.Parameters.AddWithValue("$status", task.Status);
        command.Parameters.AddWithValue("$due", (object?)task.DueDate ?? DBNull.Value);
        command.Parameters.AddWithValue("$subtasks", JsonSerializer.Serialize(task.Subtasks, JsonOptions));
        command.Parameters.AddWithValue("$position", task.Position);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertActivityAsync(SqliteConnection connection, SqliteTransaction transaction, string missionId, string kind, string message, DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mission_activity(id, mission_id, kind, message, created_at)
            VALUES($id, $mission, $kind, $message, $created);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$mission", missionId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$message", message[..Math.Min(message.Length, 400)]);
        command.Parameters.AddWithValue("$created", FormatDate(timestamp));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateMissionTimestampAsync(SqliteConnection connection, SqliteTransaction transaction, string missionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE missions SET updated_at = $updated WHERE id = $mission;";
        command.Parameters.AddWithValue("$updated", FormatDate(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$mission", missionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateMissionTaskStateAsync(SqliteConnection connection, SqliteTransaction transaction, string missionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE missions SET
                status = CASE
                    WHEN NOT EXISTS(SELECT 1 FROM mission_tasks WHERE mission_id = $mission AND status <> 'done')
                        AND EXISTS(SELECT 1 FROM mission_tasks WHERE mission_id = $mission)
                        AND status = 'active' THEN 'completed'
                    WHEN EXISTS(SELECT 1 FROM mission_tasks WHERE mission_id = $mission AND status <> 'done')
                        AND status = 'completed' THEN 'active'
                    ELSE status END,
                summary = (SELECT CASE WHEN COUNT(*) = 0 THEN 'No tasks yet.'
                    ELSE printf('%d of %d tasks complete',
                        SUM(CASE WHEN status = 'done' THEN 1 ELSE 0 END), COUNT(*)) END
                    FROM mission_tasks WHERE mission_id = $mission),
                next_action = CASE
                    WHEN NOT EXISTS(SELECT 1 FROM mission_tasks WHERE mission_id = $mission)
                        THEN 'Add a task to continue this Mission.'
                    ELSE COALESCE((SELECT title FROM mission_tasks WHERE mission_id = $mission
                        AND status <> 'done' ORDER BY position LIMIT 1),
                        'All current tasks are complete. Review the deliverables or add a new task.')
                    END
            WHERE id = $mission;
            """;
        command.Parameters.AddWithValue("$mission", missionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyList<string> DeserializeStrings(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static IReadOnlyList<DocumentPassage> DeserializePassages(string json)
    {
        try { return JsonSerializer.Deserialize<List<DocumentPassage>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static IEnumerable<(DocumentPassage Passage, string FileName, int Score)> RankPassages(
        string fileName, IReadOnlyList<DocumentPassage> passages, string query)
    {
        var terms = System.Text.RegularExpressions.Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}")
            .Select(match => match.Value)
            .Distinct()
            .ToArray();
        foreach (var passage in passages)
        {
            var score = terms.Sum(term => CountOccurrences(passage.Text, term));
            if (terms.Length == 0 || score > 0)
            {
                yield return (passage, fileName, score);
            }
        }
    }

    private static int CountOccurrences(string text, string term)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(term, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            offset += term.Length;
        }

        return count;
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, out var result) ? result : DateTimeOffset.MinValue;

    private static string FormatDate(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
