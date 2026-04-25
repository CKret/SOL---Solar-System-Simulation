using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Sol.Api.Models;
using Sol.Api.Options;
using System.Data;

namespace Sol.Api.Services;

public sealed class SqlServerEphemerisRepository(
  ISqlConnectionFactory connectionFactory,
  IOptions<EphemerisSqlOptions> sqlOptions) : IEphemerisRepository
{
  private readonly ISqlConnectionFactory _connectionFactory = connectionFactory;
  private readonly EphemerisSqlOptions _sql = sqlOptions.Value;

  public async Task<IReadOnlyList<CelestialBodySummary>> GetBodiesAsync(CancellationToken cancellationToken)
  {
    await using var connection = _connectionFactory.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = new SqlCommand(_sql.BodiesQuery, connection);
    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);

    var results = new List<CelestialBodySummary>();
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new CelestialBodySummary(
        Id: ReadRequiredInt32(reader, "Id"),
        Slug: ReadRequiredString(reader, "Slug"),
        Name: ReadRequiredString(reader, "Name"),
        Category: ReadNullableString(reader, "Category")));
    }

    return results;
  }

  public async Task<CelestialBodySummary?> GetBodyBySlugAsync(string slug, CancellationToken cancellationToken)
  {
    await using var connection = _connectionFactory.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = new SqlCommand(_sql.BodyBySlugQuery, connection);
    command.Parameters.AddWithValue("@slug", slug);
    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);

    if (!await reader.ReadAsync(cancellationToken)) return null;

    return new CelestialBodySummary(
      Id: ReadRequiredInt32(reader, "Id"),
      Slug: ReadRequiredString(reader, "Slug"),
      Name: ReadRequiredString(reader, "Name"),
      Category: ReadNullableString(reader, "Category"));
  }

  public async Task<IReadOnlyList<EphemerisSample>> GetSamplesByBodyIdAsync(int bodyId, DateTime startUtc, DateTime endUtc, int limit, CancellationToken cancellationToken)
  {
    await using var connection = _connectionFactory.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = new SqlCommand(_sql.SamplesRangeQuery, connection);
    command.Parameters.AddWithValue("@bodyId", bodyId);
    command.Parameters.AddWithValue("@startUtc", DateTime.SpecifyKind(startUtc, DateTimeKind.Utc));
    command.Parameters.AddWithValue("@endUtc", DateTime.SpecifyKind(endUtc, DateTimeKind.Utc));
    command.Parameters.AddWithValue("@limit", limit);

    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
    var results = new List<EphemerisSample>();

    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new EphemerisSample(
        BodyId: ReadRequiredInt32(reader, "BodyId"),
        SampleTimeUtc: DateTime.SpecifyKind(ReadRequiredDateTime(reader, "SampleTimeUtc"), DateTimeKind.Utc),
        X: ReadRequiredDouble(reader, "X"),
        Y: ReadRequiredDouble(reader, "Y"),
        Z: ReadRequiredDouble(reader, "Z"),
        Vx: ReadNullableDouble(reader, "Vx"),
        Vy: ReadNullableDouble(reader, "Vy"),
        Vz: ReadNullableDouble(reader, "Vz"),
        Frame: ReadNullableString(reader, "Frame")));
    }

    return results;
  }

  private static int ReadRequiredInt32(SqlDataReader reader, string columnName)
  {
    var ordinal = reader.GetOrdinal(columnName);
    return reader.GetInt32(ordinal);
  }

  private static double ReadRequiredDouble(SqlDataReader reader, string columnName)
  {
    var ordinal = reader.GetOrdinal(columnName);
    return reader.GetDouble(ordinal);
  }

  private static DateTime ReadRequiredDateTime(SqlDataReader reader, string columnName)
  {
    var ordinal = reader.GetOrdinal(columnName);
    return reader.GetDateTime(ordinal);
  }

  private static string ReadRequiredString(SqlDataReader reader, string columnName)
  {
    var ordinal = reader.GetOrdinal(columnName);
    return reader.GetString(ordinal);
  }

  private static string? ReadNullableString(SqlDataReader reader, string columnName)
  {
    var ordinal = TryGetOrdinal(reader, columnName);
    return ordinal is null || reader.IsDBNull(ordinal.Value) ? null : reader.GetString(ordinal.Value);
  }

  private static double? ReadNullableDouble(SqlDataReader reader, string columnName)
  {
    var ordinal = TryGetOrdinal(reader, columnName);
    return ordinal is null || reader.IsDBNull(ordinal.Value) ? null : reader.GetDouble(ordinal.Value);
  }

  private static int? TryGetOrdinal(SqlDataReader reader, string columnName)
  {
    for (var index = 0; index < reader.FieldCount; index++) {
      if (string.Equals(reader.GetName(index), columnName, StringComparison.OrdinalIgnoreCase)) {
        return index;
      }
    }

    return null;
  }
}