namespace GameController.FBServiceExt.Application.Abstractions.State;

public interface IUserAccountNameStore
{
    ValueTask<string?> GetAsync(string userId, CancellationToken cancellationToken);

    ValueTask SetAsync(string userId, string accountName, TimeSpan retention, CancellationToken cancellationToken);

    ValueTask RemoveAsync(string userId, CancellationToken cancellationToken);
}
