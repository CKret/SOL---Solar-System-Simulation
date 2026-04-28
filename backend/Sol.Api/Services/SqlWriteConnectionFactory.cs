using Microsoft.Data.SqlClient;

namespace Sol.Api.Services;

public sealed class SqlWriteConnectionFactory(IConfiguration configuration) : ISqlWriteConnectionFactory
{
  private readonly string _connectionString = configuration.GetConnectionString("EphemerisDbWrite")
    ?? throw new InvalidOperationException("Missing connection string 'EphemerisDbWrite'.");

  public SqlConnection CreateConnection() => new(_connectionString);
}