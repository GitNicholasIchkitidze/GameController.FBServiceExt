namespace GameController.FBServiceExt.Infrastructure.Options;

public sealed class SqlStorageOptions
{
    public const string SectionName = "SqlStorage";

    public string ConnectionString { get; set; } = "Server=localhost;Database=GameControllerFBServiceExt;Trusted_Connection=True;TrustServerCertificate=True;";
}
