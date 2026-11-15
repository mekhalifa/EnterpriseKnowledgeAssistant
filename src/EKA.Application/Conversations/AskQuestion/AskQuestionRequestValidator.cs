using EKA.Contracts.Conversations;
using FluentValidation;

namespace EKA.Application.Conversations.AskQuestion;

/// <summary>
/// Shape only (ADR-0013): present, and not absurdly long.
/// </summary>
/// <remarks>
/// The character cap is a cheap first line that rejects a pasted novel before a database
/// query or a token estimate is spent on it. The real limit is the token budget, which
/// depends on the active template and is checked in the handler
/// (<c>conversation.question_too_long</c>).
/// </remarks>
public sealed class AskQuestionRequestValidator : AbstractValidator<AskQuestionRequest>
{
    public const int MaxQuestionLength = 8000;

    public AskQuestionRequestValidator() =>
        RuleFor(request => request.Question)
            .NotEmpty()
            .MaximumLength(MaxQuestionLength);
}
