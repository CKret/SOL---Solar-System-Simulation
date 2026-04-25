using Sol.Api.Models;
using Sol.Api.Options;
using Sol.Api.Services;

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

builder.Services.Configure<EphemerisSqlOptions>(builder.Configuration.GetSection(EphemerisSqlOptions.SectionName));
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddScoped<IEphemerisRepository, SqlServerEphemerisRepository>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();

app.MapGet("/", () => Results.Ok(new
{
	service = "SOL Ephemeris API",
	status = "ok",
	endpoints = new[]
	{
		"/api/health",
		"/api/bodies",
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

app.MapGet("/api/bodies", async (IEphemerisRepository repository, CancellationToken cancellationToken) =>
{
	var bodies = await repository.GetBodiesAsync(cancellationToken);
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

sealed record ValidatedRange(int Limit);
sealed record RangeValidationResult(ValidatedRange? Range, IResult? Error);
