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

public record PlaneComment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("comment_html")] string CommentHtml
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
    [property: JsonPropertyName("results")] List<PlaneIssue> Results
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

[JsonSerializable(typeof(PlaneIssue))]
[JsonSerializable(typeof(PlaneIssueList))]
[JsonSerializable(typeof(PlaneState))]
[JsonSerializable(typeof(PlaneStateList))]
[JsonSerializable(typeof(PlaneLabel))]
[JsonSerializable(typeof(PlaneLabelList))]
[JsonSerializable(typeof(PlaneComment))]
[JsonSerializable(typeof(PlaneRelationItem))]
[JsonSerializable(typeof(PlaneRelationList))]
[JsonSerializable(typeof(TransitionRequest))]
[JsonSerializable(typeof(AppendDescriptionRequest))]
[JsonSerializable(typeof(CreateCommentRequest))]
[JsonSerializable(typeof(ApplyLabelsRequest))]
[JsonSerializable(typeof(List<PlaneState>))]
[JsonSerializable(typeof(List<PlaneLabel>))]
[JsonSerializable(typeof(List<PlaneRelationItem>))]
[JsonSerializable(typeof(List<string>))]
internal partial class PlaneJsonContext : JsonSerializerContext { }
