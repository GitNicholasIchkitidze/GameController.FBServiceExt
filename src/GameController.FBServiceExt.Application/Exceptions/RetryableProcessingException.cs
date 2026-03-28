namespace GameController.FBServiceExt.Application.Exceptions;

public sealed class RetryableProcessingException : Exception
{
    public RetryableProcessingException(string message) : base(message)
    {
    }
}
