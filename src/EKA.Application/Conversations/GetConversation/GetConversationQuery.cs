using EKA.Application.Identity;

namespace EKA.Application.Conversations.GetConversation;

public sealed record GetConversationQuery(CallerIdentity Caller, Guid ConversationId);
