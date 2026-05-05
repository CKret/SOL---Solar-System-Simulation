using Microsoft.Data.SqlClient;

namespace Sol.Api.Services;

public interface ISqlWriteConnectionFactory
{
  SqlConnection CreateConnection();
}