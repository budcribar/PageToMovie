namespace PageToMovie.Core.Models;

/// <summary>One row from generation_errors (admin panel read model). Write side is Engine <c>GenerationErrorRecord</c>.</summary>
public sealed class GenerationErrorRow
{
    public long Id { get; set; }
    public string Ts { get; set; } = "";
    public string? UserId { get; set; }
    public string? ProjectId { get; set; }
    public string? JobId { get; set; }
    public int? Scene { get; set; }
    public int? Clip { get; set; }
    public string Stage { get; set; } = "";
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string ErrorType { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public int? HttpStatus { get; set; }
    public int? RequestedCount { get; set; }
    public int? ReturnedCount { get; set; }
    public string? MissingIdsJson { get; set; }
    public int Attempt { get; set; }
    public bool Resolved { get; set; }
    public string? RequestSummary { get; set; }
    public string? ResponseSummary { get; set; }
}
