using Microsoft.Data.SqlClient;

namespace Sol.Api.Services;

public sealed class SqlConnectionFactory(IConfiguration configuration) : ISqlConnectionFactory
{
  private readonly string _connectionString = configuration.GetConnectionString("EphemerisDb")
    ?? throw new InvalidOperationException("Missing connection string 'EphemerisDb'.");

  public SqlConnection CreateConnection() => new(_connectionString);
}