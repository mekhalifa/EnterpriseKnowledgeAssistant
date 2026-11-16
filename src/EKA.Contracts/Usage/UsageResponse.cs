namespace EKA.Contracts.Usage;

/// <summary>A tenant's token usage for the current month, against its budget.</summary>
/// <param name="Period">The calendar month, <c>yyyy-MM</c> (UTC).</param>
/// <param name="ChatInputTokens">Prompt tokens sent to the chat model.</param>
/// <param name="ChatOutputTokens">Answer tokens produced by the chat model.</param>
/// <param name="EmbeddingTokens">Tokens embedded: questions and ingested documents.</param>
/// <param name="TotalTokens">The sum the budget is measured against.</param>
/// <param name="BudgetTokens">The month's budget. Zero means AI use is suspended for the tenant.</param>
/// <param name="RemainingTokens">Budget minus total, never below zero.</param>
public sealed record UsageResponse(
    string Period,
    long ChatInputTokens,
    long ChatOutputTokens,
    long EmbeddingTokens,
    long TotalTokens,
    long BudgetTokens,
    long RemainingTokens);
