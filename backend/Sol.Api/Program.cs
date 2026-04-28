using Sol.Api.Models;
using Sol.Api.Services;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddCors(options =>
{
	options.AddDefaultPolicy(policy =>
		policy
			.WithOrigins(
				"http://localhost:8000",
				"https://localhost:8000",
				"http://127.0.0.1:8000",
				"https://127.0.0.1:8000")
			.AllowAnyHeader()
			.AllowAnyMethod());
});

builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddSingleton<ISqlWriteConnectionFactory, SqlWriteConnectionFactory>();
builder.Services.AddScoped<IEphemerisRepository, SqlServerEphemerisRepository>();
builder.Services.AddHttpClient<IAuthoritativeBodyCatalogReader, AuthoritativeBodyCatalogReader>();
builder.Services.AddHttpClient<IEphemerisSampleImporter, HorizonsEphemerisSampleImporter>();
builder.Services.AddScoped<IBodyCatalogImporter, SqlBodyCatalogImporter>();
builder.Services.AddHttpClient<MpcorbImporter>();

var app = builder.Build();

if (args.Length > 0 && string.Equals(args[0], "import-bodies", StringComparison.OrdinalIgnoreCase)) {
	using var scope = app.Services.CreateScope();
	var importer = scope.ServiceProvider.GetRequiredService<IBodyCatalogImporter>();
	var result = await importer.ImportAsync(CancellationToken.None);
	Console.WriteLine($"Imported celestial bodies. Inserted: {result.Inserted}, Updated: {result.Updated}, Total: {result.Total}.");
	return;
}

if (args.Length > 0 && string.Equals(args[0], "import-mpcorb", StringComparison.OrdinalIgnoreCase)) {
	var fullCatalog = args.Length > 1 && string.Equals(args[1], "full", StringComparison.OrdinalIgnoreCase);
	using var scope = app.Services.CreateScope();
	var importer = scope.ServiceProvider.GetRequiredService<MpcorbImporter>();
	var (inserted, updated, total) = await importer.ImportAsync(fullCatalog, CancellationToken.None);
	Console.WriteLine($"MPCORB import complete. Total: {total:N0}, Inserted: {inserted:N0}, Updated: {updated:N0}.");
	return;
}

if (args.Length > 0 && string.Equals(args[0], "import-mpcorb-samples", StringComparison.OrdinalIgnoreCase)) {
	var hMax     = args.Length > 1 && double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : (double?)null;
	var startUtc = args.Length > 2 ? DateTime.Parse(args[2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) : new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
	var endUtc   = args.Length > 3 ? DateTime.Parse(args[3], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) : new DateTime(2200, 1, 1, 0, 0, 0, DateTimeKind.Utc);
	var step     = args.Length > 4 ? ParseSampleRate(args[4]) ?? TimeSpan.FromDays(1) : TimeSpan.FromDays(1);

	using var scope = app.Services.CreateScope();
	var importer = scope.ServiceProvider.GetRequiredService<IEphemerisSampleImporter>() as HorizonsEphemerisSampleImporter
		?? throw new InvalidOperationException("IEphemerisSampleImporter is not HorizonsEphemerisSampleImporter.");
	var (bodies, samples) = await importer.ImportMpcorbSamplesAsync(hMax, startUtc, endUtc, step, CancellationToken.None);
	Console.WriteLine($"MPC ephemeris import complete. Bodies: {bodies:N0}, Samples: {samples:N0}.");
	return;
}

if (args.Length > 0 && string.Equals(args[0], "import-samples", StringComparison.OrdinalIgnoreCase)) {
	var startUtc = args.Length > 1 ? DateTime.Parse(args[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) : new DateTime(2024, 12, 30, 0, 0, 0, DateTimeKind.Utc);
	var endUtc = args.Length > 2 ? DateTime.Parse(args[2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) : new DateTime(2025, 1, 3, 0, 0, 0, DateTimeKind.Utc);
	var sampleRate = args.Length > 3 ? ParseSampleRate(args[3]) : null;

	using var scope = app.Services.CreateScope();
	var importer = scope.ServiceProvider.GetRequiredService<IEphemerisSampleImporter>();
	var result = await importer.ImportAsync(startUtc, endUtc, sampleRate, CancellationToken.None);
	Console.WriteLine($"Imported ephemeris samples. Bodies: {result.BodyCount}, Samples: {result.SampleCount}, Replaced: {result.DeletedCount}.");
	return;
}

app.UseExceptionHandler();
app.UseCors();

app.MapGet("/", () => Results.Ok(new
{
	service = "SOL Ephemeris API",
	status = "ok",
	endpoints = new[]
	{
		"/api/health",
		"/api/bodies?h_max=<magnitude>",
		"/api/bodies/{slug}",
		"/api/ephemeris/{bodyId}?startUtc=...&endUtc=...&limit=...",
		"/api/ephemeris/by-slug/{slug}?startUtc=...&endUtc=...&limit=..."
	}
}));

app.MapGet("/api/health", () => Results.Ok(new
{
	status = "ok",
	utc = DateTime.UtcNow
}));

app.MapGet("/api/bodies", async (double? h_max, IEphemerisRepository repository, CancellationToken cancellationToken) =>
{
	var bodies = await repository.GetBodiesAsync(h_max, cancellationToken);
	return Results.Ok(bodies);
});

app.MapGet("/api/bodies/{slug}", async (string slug, IEphemerisRepository repository, CancellationToken cancellationToken) =>
{
	var body = await repository.GetBodyBySlugAsync(slug, cancellationToken);
	return body is null ? Results.NotFound() : Results.Ok(body);
});

app.MapGet("/api/ephemeris/{bodyId:int}", async (int bodyId, DateTime startUtc, DateTime endUtc, int? limit, IEphemerisRepository repository, CancellationToken cancellationToken) =>
{
	var validated = ValidateRange(startUtc, endUtc, limit);
	if (validated.Error is not null) return validated.Error;

	var samples = await repository.GetSamplesByBodyIdAsync(bodyId, startUtc, endUtc, validated.Range!.Limit, cancellationToken);
	return Results.Ok(new EphemerisRangeResponse(bodyId, null, startUtc, endUtc, samples.Count, samples));
});

app.MapGet("/api/ephemeris/by-slug/{slug}", async (string slug, DateTime startUtc, DateTime endUtc, int? limit, IEphemerisRepository repository, CancellationToken cancellationToken) =>
{
	var validated = ValidateRange(startUtc, endUtc, limit);
	if (validated.Error is not null) return validated.Error;

	var body = await repository.GetBodyBySlugAsync(slug, cancellationToken);
	if (body is null) return Results.NotFound();

	var samples = await repository.GetSamplesByBodyIdAsync(body.Id, startUtc, endUtc, validated.Range!.Limit, cancellationToken);
	return Results.Ok(new EphemerisRangeResponse(body.Id, body.Slug, startUtc, endUtc, samples.Count, samples));
});

app.Run();

static RangeValidationResult ValidateRange(DateTime startUtc, DateTime endUtc, int? limit)
{
	if (startUtc == default || endUtc == default) {
		return new(null, Results.BadRequest(new { error = "startUtc and endUtc are required query parameters in UTC." }));
	}

	if (endUtc < startUtc) {
		return new(null, Results.BadRequest(new { error = "endUtc must be greater than or equal to startUtc." }));
	}

	var normalizedLimit = limit ?? 1440;
	if (normalizedLimit <= 0 || normalizedLimit > 50000) {
		return new(null, Results.BadRequest(new { error = "limit must be between 1 and 50000." }));
	}

	return new(new ValidatedRange(normalizedLimit), null);
}

static TimeSpan? ParseSampleRate(string value)
{
	if (string.IsNullOrWhiteSpace(value)) {
		return null;
	}

	var normalized = value.Trim().ToLowerInvariant();
	if (normalized is "default" or "auto") {
		return null;
	}

	return normalized switch
	{
		"hourly" => TimeSpan.FromHours(1),
		"daily" => TimeSpan.FromDays(1),
		_ when normalized.EndsWith("h", StringComparison.Ordinal) && int.TryParse(normalized[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) && hours > 0
			=> TimeSpan.FromHours(hours),
		_ when normalized.EndsWith("d", StringComparison.Ordinal) && int.TryParse(normalized[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0
			=> TimeSpan.FromDays(days),
		_ => throw new ArgumentException("sample rate must be one of: auto, default, hourly, daily, <n>h, <n>d")
	};
}

sealed record ValidatedRange(int Limit);
sealed record RangeValidationResult(ValidatedRange? Range, IResult? Error);
