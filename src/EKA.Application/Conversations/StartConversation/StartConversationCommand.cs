using EKA.Application.Identity;

namespace EKA.Application.Conversations.StartConversation;

public sealed record StartConversationCommand(CallerIdentity Caller);
