using Microsoft.Data.SqlClient;

namespace Sol.Api.Services;

public interface ISqlConnectionFactory
{
  SqlConnection CreateConnection();
}