namespace GameController.FBServiceExt.Application.Abstractions.Processing;

public interface IUserAccountNameResolver
{
    ValueTask<string?> GetOrResolveAsync(string userId, CancellationToken cancellationToken);
}
