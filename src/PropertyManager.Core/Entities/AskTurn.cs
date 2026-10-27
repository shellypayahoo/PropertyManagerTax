namespace PropertyManager.Core.Entities;

/// <summary>One question and answer on the Ask page, kept until the owner clears the conversation.</summary>
public class AskTurn
{
    public int Id { get; set; }
    public DateTimeOffset AskedAt { get; set; }
    public string Question { get; set; } = "";

    /// <summary>Names of files attached to the question, separated by "\n".</summary>
    public string? Files { get; set; }

    public string? Answer { get; set; }
    public string? Provider { get; set; }
    public bool Declined { get; set; }
    public string? Error { get; set; }

    /// <summary>Lookups and proposed changes, as JSON.</summary>
    public string? LookupsJson { get; set; }
    public string? ChangesJson { get; set; }
}
