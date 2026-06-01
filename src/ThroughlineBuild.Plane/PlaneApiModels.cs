using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThroughlineBuild.Plane;

// Wire-format DTOs - separate from Contracts records
public record PlaneIssue(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sequence_id")] int SequenceId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description_html")] string? DescriptionHtml,
    [property: JsonPropertyName("state")] string StateId,
    [property: JsonPropertyName("label_ids")] List<string>? LabelIds,
    [property: JsonPropertyName("parent")] string? ParentId,
    [property: JsonPropertyName("type")] string? Type
);

public record PlaneState(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("group")] string Group
);

public record PlaneStateList(
    [property: JsonPropertyName("results")] List<PlaneState> Results
);

public record PlaneLabel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name
);

public record PlaneLabelList(
    [property: JsonPropertyName("results")] List<PlaneLabel> Results
);

public record PlaneIssueType(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name
);

public record PlaneIssueTypeList(
    [property: JsonPropertyName("results")] List<PlaneIssueType> Results
);

public record PlaneComment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("comment_html")] string CommentHtml
);

public record PlaneCommentListItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("comment_html")] string? CommentHtml,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt
);

public record PlaneCommentList(
    [property: JsonPropertyName("results")] List<PlaneCommentListItem> Results
);

public record PlaneRelationItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("relation_type")] string RelationType,
    [property: JsonPropertyName("related_issue")] string RelatedIssue
);

public record PlaneRelationList(
    [property: JsonPropertyName("results")] List<PlaneRelationItem> Results
);

public record PlaneIssueList(
    [property: JsonPropertyName("results")] List<PlaneIssue> Results,
    [property: JsonPropertyName("next_cursor")] string? NextCursor = null,
    // Plane echoes a non-empty, advancing next_cursor even past the last page; this flag is the
    // authoritative "is there another page" signal (false on the final page). Without it the
    // cursor-only loop walks to the page cap on every load.
    [property: JsonPropertyName("next_page_results")] bool? NextPageResults = null
);

// Expanded DTOs - used for ?expand=state requests (state is an object, not a UUID string)
internal record PlaneStateExpansion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name
);

internal record PlaneIssueExpanded(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sequence_id")] int SequenceId,
    [property: JsonPropertyName("parent")] string? ParentId,
    [property: JsonPropertyName("state")] JsonElement State  // defensive: string UUID or object
);

internal record PlaneIssueExpandedList(
    [property: JsonPropertyName("results")] List<PlaneIssueExpanded> Results,
    [property: JsonPropertyName("next_cursor")] string? NextCursor = null,
    [property: JsonPropertyName("next_page_results")] bool? NextPageResults = null
);

// Request body types
public record TransitionRequest(
    [property: JsonPropertyName("state")] string StateId
);

public record AppendDescriptionRequest(
    [property: JsonPropertyName("description_html")] string DescriptionHtml
);

public record CreateCommentRequest(
    [property: JsonPropertyName("comment_html")] string CommentHtml
);

public record ApplyLabelsRequest(
    [property: JsonPropertyName("label_ids")] List<string> LabelIds
);

public record CreateIssueRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description_html")] string DescriptionHtml,
    [property: JsonPropertyName("type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type,
    [property: JsonPropertyName("label_ids")] List<string> LabelIds
)
{
    [JsonPropertyName("parent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentId { get; init; }
}

public record PlaneCreateIssueResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sequence_id")] int SequenceId,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt
);

public record SetParentRequest(
    [property: JsonPropertyName("parent")] string Parent
);

public record UpdateDescriptionRequest(
    [property: JsonPropertyName("description_html")] string DescriptionHtml
);

public record CreateRelationRequest(
    [property: JsonPropertyName("relation_type")] string RelationType,
    [property: JsonPropertyName("related_issue")] string RelatedIssue
);

[JsonSerializable(typeof(PlaneIssue))]
[JsonSerializable(typeof(PlaneIssueList))]
[JsonSerializable(typeof(PlaneState))]
[JsonSerializable(typeof(PlaneStateList))]
[JsonSerializable(typeof(PlaneLabel))]
[JsonSerializable(typeof(PlaneLabelList))]
[JsonSerializable(typeof(PlaneIssueType))]
[JsonSerializable(typeof(PlaneIssueTypeList))]
[JsonSerializable(typeof(PlaneComment))]
[JsonSerializable(typeof(PlaneCommentListItem))]
[JsonSerializable(typeof(PlaneCommentList))]
[JsonSerializable(typeof(List<PlaneCommentListItem>))]
[JsonSerializable(typeof(PlaneRelationItem))]
[JsonSerializable(typeof(PlaneRelationList))]
[JsonSerializable(typeof(PlaneStateExpansion))]
[JsonSerializable(typeof(PlaneIssueExpanded))]
[JsonSerializable(typeof(PlaneIssueExpandedList))]
[JsonSerializable(typeof(TransitionRequest))]
[JsonSerializable(typeof(AppendDescriptionRequest))]
[JsonSerializable(typeof(CreateCommentRequest))]
[JsonSerializable(typeof(ApplyLabelsRequest))]
[JsonSerializable(typeof(CreateIssueRequest))]
[JsonSerializable(typeof(PlaneCreateIssueResponse))]
[JsonSerializable(typeof(SetParentRequest))]
[JsonSerializable(typeof(UpdateDescriptionRequest))]
[JsonSerializable(typeof(CreateRelationRequest))]
[JsonSerializable(typeof(List<PlaneState>))]
[JsonSerializable(typeof(List<PlaneLabel>))]
[JsonSerializable(typeof(List<PlaneRelationItem>))]
[JsonSerializable(typeof(List<string>))]
internal partial class PlaneJsonContext : JsonSerializerContext { }
