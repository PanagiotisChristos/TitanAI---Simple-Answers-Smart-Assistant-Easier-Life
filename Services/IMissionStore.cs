using Project_no._1.Api;

namespace Project_no._1.Services;

public interface IMissionStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MissionSummaryResponse>> ListAsync(string ownerId, bool includeArchived, CancellationToken cancellationToken);
    Task<MissionResponse?> GetAsync(string ownerId, string missionId, CancellationToken cancellationToken);
    Task CreateAsync(string ownerId, MissionResponse mission, CancellationToken cancellationToken);
    Task<bool> UpdateMissionAsync(string ownerId, string missionId, MissionUpdateRequest request, CancellationToken cancellationToken);
    Task<bool> SetMissionStatusAsync(string ownerId, string missionId, string status, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string ownerId, string missionId, CancellationToken cancellationToken);
    Task<bool> AddTaskAsync(string ownerId, string missionId, MissionTaskResponse task, CancellationToken cancellationToken);
    Task<bool> UpdateTaskAsync(string ownerId, string missionId, string taskId, MissionTaskUpdateRequest request, CancellationToken cancellationToken);
    Task<bool> MoveTaskAsync(string ownerId, string missionId, string taskId, string direction, CancellationToken cancellationToken);
    Task<bool> DeleteTaskAsync(string ownerId, string missionId, string taskId, CancellationToken cancellationToken);
    Task<MissionDocumentAddResult> AddDocumentAsync(string ownerId, string missionId, string documentId, string fileName, IReadOnlyList<DocumentPassage> passages, CancellationToken cancellationToken);
    Task<bool> DeleteDocumentAsync(string ownerId, string missionId, string documentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MissionDocumentPassage>> FindRelevantDocumentPassagesAsync(string ownerId, string missionId, string query, int limit, CancellationToken cancellationToken);
    Task<bool> AddDeliverableAsync(string ownerId, string missionId, MissionDeliverableResponse deliverable, CancellationToken cancellationToken);
    Task<bool> DeleteDeliverableAsync(string ownerId, string missionId, string deliverableId, CancellationToken cancellationToken);
    Task<bool> AddMemoryAsync(string ownerId, string missionId, MissionMemoryResponse memory, CancellationToken cancellationToken);
    Task<bool> UpdateMemoryAsync(string ownerId, string missionId, string memoryId, string category, string content, CancellationToken cancellationToken);
    Task<bool> DeleteMemoryAsync(string ownerId, string missionId, string memoryId, CancellationToken cancellationToken);
    Task<bool> ClearMemoryAsync(string ownerId, string missionId, CancellationToken cancellationToken);
    Task<bool> AddActivityAsync(string ownerId, string missionId, string kind, string message, CancellationToken cancellationToken);
}

public enum MissionDocumentAddResult
{
    Added,
    MissionNotFound,
    CapacityExceeded
}

public sealed record MissionDocumentPassage(string FileName, DocumentPassage Passage);
