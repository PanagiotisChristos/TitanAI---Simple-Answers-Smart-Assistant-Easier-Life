namespace Project_no._1.Api;

/// <summary>A question and optional recent conversation context.</summary>
public sealed record AskRequest(string? Question, IReadOnlyList<ConversationMessage>? History = null, string? MissionId = null, bool SearchWeb = false);

/// <summary>A message from a recent chat turn.</summary>
public sealed record ConversationMessage(string Role, string Content);

/// <summary>An answer returned by the assistant.</summary>
public sealed record AskResponse(string Answer, IReadOnlyList<WebSearchSourceResponse>? Sources = null);

/// <summary>A web page used to support an assistant answer.</summary>
public sealed record WebSearchSourceResponse(string Title, string Url, string Description);

/// <summary>A text transformation request.</summary>
public sealed record TransformRequest(string? Action, string? Text, string? Tone, string? Language);

/// <summary>An AI-generated text result.</summary>
public sealed record TextResponse(string Answer);

/// <summary>A decision and the available options.</summary>
public sealed record DecisionRequest(string? Decision, IReadOnlyList<string?>? Options, IReadOnlyList<string?>? Priorities);

/// <summary>A goal to turn into a practical plan.</summary>
public sealed record PlanRequest(string? Goal);

/// <summary>A task generated for an action plan.</summary>
public sealed record PlanTask(string Title, string Description, string Timing);

/// <summary>An ordered list of proposed plan tasks.</summary>
public sealed record PlanResponse(IReadOnlyList<PlanTask> Tasks);

/// <summary>Temporary document metadata returned after upload.</summary>
public sealed record DocumentResponse(string DocumentId, string FileName, int PassageCount, int CharacterCount);

/// <summary>A document-backed question or study action.</summary>
public sealed record KnowledgeAskRequest(string? DocumentId, string? Action, string? Question);

/// <summary>A user-facing API validation error.</summary>
public sealed record ApiErrorResponse(string Message);

/// <summary>Mission creation preferences supplied before an AI plan is drafted.</summary>
public sealed record MissionDraftRequest(string? Goal, string? Deadline, string? AvailableTime, string? Constraints);

/// <summary>A task proposed for a Mission plan.</summary>
public sealed record MissionTaskDraft(string Title, string Description, string Priority, string? DueDate, IReadOnlyList<string> Subtasks);

/// <summary>A proposed Mission overview and ordered plan.</summary>
public sealed record MissionDraftResponse(string Title, string Goal, string ExpectedOutcome, IReadOnlyList<string> SuccessCriteria, IReadOnlyList<MissionTaskDraft> Tasks, string Summary, string NextAction);

/// <summary>A reviewed Mission plan accepted for persistent storage.</summary>
public sealed record MissionCreateRequest(MissionDraftResponse? Plan, string? Constraints, string? AvailableTime, string? Deadline);

/// <summary>A complete Mission with its tasks, memory, deliverables, and activity.</summary>
public sealed record MissionResponse(
    string Id,
    string Title,
    string Goal,
    string ExpectedOutcome,
    IReadOnlyList<string> SuccessCriteria,
    string Constraints,
    string AvailableTime,
    string? Deadline,
    string Status,
    string Summary,
    string NextAction,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<MissionTaskResponse> Tasks,
    IReadOnlyList<MissionDocumentResponse> Documents,
    IReadOnlyList<MissionDeliverableResponse> Deliverables,
    IReadOnlyList<MissionMemoryResponse> Memory,
    IReadOnlyList<MissionActivityResponse> Activity);

/// <summary>A concise Mission row with task progress calculated from persisted task states.</summary>
public sealed record MissionSummaryResponse(
    string Id,
    string Title,
    string Goal,
    string Status,
    string? Deadline,
    DateTimeOffset UpdatedAt,
    int TaskCount,
    int CompletedTaskCount,
    int ProgressPercent);

/// <summary>Edits to a Mission overview.</summary>
public sealed record MissionUpdateRequest(string? Title, string? Goal, string? ExpectedOutcome, IReadOnlyList<string>? SuccessCriteria, string? Constraints, string? AvailableTime, string? Deadline);

/// <summary>A persisted Mission task with its current state.</summary>
public sealed record MissionTaskResponse(string Id, string Title, string Description, string Priority, string Status, string? DueDate, IReadOnlyList<string> Subtasks, int Position);

/// <summary>Fields that can be changed on a Mission task.</summary>
public sealed record MissionTaskUpdateRequest(string? Title, string? Description, string? Priority, string? Status, string? DueDate, IReadOnlyList<string>? Subtasks);

/// <summary>A request to add a user-created task to a Mission.</summary>
public sealed record MissionTaskCreateRequest(string? Title, string? Description, string? Priority, string? DueDate, IReadOnlyList<string>? Subtasks);

/// <summary>The direction in which a task should move in the ordered plan.</summary>
public sealed record MoveMissionTaskRequest(string? Direction);

/// <summary>A Mission deliverable generated or saved by the user.</summary>
public sealed record MissionDeliverableResponse(string Id, string TaskId, string Title, string Content, DateTimeOffset CreatedAt);

/// <summary>Extracted document information linked to a Mission.</summary>
public sealed record MissionDocumentResponse(string Id, string FileName, int PassageCount, int CharacterCount, DateTimeOffset AddedAt);

/// <summary>A user-approved, editable fact or decision saved to Mission memory.</summary>
public sealed record MissionMemoryResponse(string Id, string Category, string Content, string Provenance, DateTimeOffset UpdatedAt);

/// <summary>A timestamped event in the Mission activity history.</summary>
public sealed record MissionActivityResponse(string Id, string Kind, string Message, DateTimeOffset CreatedAt);

/// <summary>A user-provided project memory entry.</summary>
public sealed record MissionMemoryCreateRequest(string? Category, string? Content);

/// <summary>Edits to a saved project memory entry.</summary>
public sealed record MissionMemoryUpdateRequest(string? Category, string? Content);

/// <summary>Optional document identifier associated with a Mission.</summary>
public sealed record MissionDocumentRequest(string? DocumentId);

/// <summary>Actions available to work on a Mission task.</summary>
public sealed record MissionAssistRequest(string? TaskId, string? Action, string? Prompt);

/// <summary>An AI response and optional persisted deliverable identifier.</summary>
public sealed record MissionAssistResponse(string Answer, string? DeliverableId, string? MemoryNotice);

/// <summary>Session readiness for a browser-scoped TitanAI workspace.</summary>
public sealed record WorkspaceResponse(bool Ready);
