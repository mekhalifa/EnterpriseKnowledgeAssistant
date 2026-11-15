using EKA.Application.Abstractions;
using EKA.Application.Common;
using EKA.Application.Identity;
using EKA.Contracts.Conversations;
using EKA.Domain.Conversations;

namespace EKA.Application.Conversations.StartConversation;

/// <summary>Opens an empty conversation for the caller, provisioning them if this is their
/// first contact.</summary>
public sealed class StartConversationHandler(
    UserProvisioner provisioner,
    IConversationRepository conversations,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Result<ConversationResponse>> HandleAsync(
        StartConversationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var user = await provisioner.GetOrProvisionAsync(command.Caller, cancellationToken).ConfigureAwait(false);
        if (user.IsFailure)
        {
            return user.Error;
        }

        var conversation = new Conversation(
            Guid.CreateVersion7(),
            command.Caller.TenantId,
            user.Value.Id,
            timeProvider.GetUtcNow());

        conversations.Add(conversation);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return conversation.ToResponse();
    }
}
