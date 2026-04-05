using GameController.FBServiceExt.Application.Contracts.Persistence;

namespace GameController.FBServiceExt.Application.Abstractions.Persistence;

public interface IUserDataErasureService
{
    ValueTask<UserDataErasureResult> EraseUserDataAsync(string userId, CancellationToken cancellationToken);
}
