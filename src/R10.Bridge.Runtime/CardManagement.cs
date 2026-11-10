using R10.Bridge.Client;
using R10.Core;

namespace R10.Bridge.Runtime;

public sealed record CardAccess(GameLoadEpoch Epoch, int Context);

public interface ICardManagementRuntime
{
    Task<CardReply> ExecuteCardAsync(CardAccess expected, CardRequest request);
}
