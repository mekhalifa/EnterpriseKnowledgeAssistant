using EKA.Application.Identity;

namespace EKA.Application.Conversations.AskQuestion;

public sealed record AskQuestionCommand(CallerIdentity Caller, Guid ConversationId, string Question);
