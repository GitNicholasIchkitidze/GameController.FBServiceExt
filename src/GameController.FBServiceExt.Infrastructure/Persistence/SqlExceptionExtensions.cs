using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace GameController.FBServiceExt.Infrastructure.Persistence;

internal static class SqlExceptionExtensions
{
    public static bool IsUniqueConstraintViolation(this Exception exception)
    {
        if (exception is DbUpdateException { InnerException: SqlException sqlException })
        {
            return sqlException.Number is 2601 or 2627;
        }

        if (exception is SqlException directSqlException)
        {
            return directSqlException.Number is 2601 or 2627;
        }

        return exception.InnerException is not null && exception.InnerException.IsUniqueConstraintViolation();
    }
}
