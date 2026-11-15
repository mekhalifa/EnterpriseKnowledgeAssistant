using EKA.Application.Identity;

namespace EKA.Application.Conversations.ListConversations;

public sealed record ListConversationsQuery(CallerIdentity Caller, int Limit);
