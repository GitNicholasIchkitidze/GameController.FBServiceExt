namespace GameController.FBServiceExt.Application.Abstractions.State;

public interface IUserProcessingLockManager
{
    ValueTask<IDistributedLockHandle?> TryAcquireAsync(string scope, TimeSpan ttl, CancellationToken cancellationToken);
}
