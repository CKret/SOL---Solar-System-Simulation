using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Sol.Api.Data;
using Sol.Api.Models;
using Sol.Api.Services;
using System.Globalization;

Console.SetOut(new TimestampedWriter(Console.Out));
Console.SetError(new TimestampedWriter(Console.Error));

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

builder.Services.AddMemoryCache(options =>
{
	// Keep total in-process cache bounded; entry sizes are set on write.
	options.SizeLimit = 50_000_000; // approx 50 MB budget
});
builder.Services.AddDbContextFactory<SolReadDbContext>(opts =>
    opts.UseSqlServer(builder.Configuration.GetConnectionString("EphemerisDb")
        ?? throw new InvalidOperationException("Missing connection string 'EphemerisDb'.")));
builder.Services.AddDbContextFactory<SolWriteDbContext>(opts =>
    opts.UseSqlServer(builder.Configuration.GetConnectionString("EphemerisDbWrite")
        ?? throw new InvalidOperationException("Missing connection string 'EphemerisDbWrite'.")));
builder.Services.AddScoped<IEphemerisRepository, SqlServerEphemerisRepository>();
builder.Services.AddHttpClient<IAuthoritativeBodyCatalogReader, AuthoritativeBodyCatalogReader>();
builder.Services.AddHttpClient<IEphemerisSampleImporter, HorizonsEphemerisSampleImporter>();
builder.Services.AddScoped<IBodyCatalogImporter, SqlBodyCatalogImporter>();
builder.Services.AddHttpClient<MpcorbImporter>();

builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

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

if (args.Length > 0 && string.Equals(args[0], "import-retry-zeros", StringComparison.OrdinalIgnoreCase)) {
	// import-retry-zeros [max_shrink_days]
	// Retries all EphemerisImportLog entries with SampleCount=0, incrementally
	// shrinking the boundary edge by 1 day at a time up to max_shrink_days.
	// Only the edge touching EphemerisMinJD/MaxJD is shrunk. Default: 10 days.
	var shrinkDays = args.Length > 1 && double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 10.0;
	using var scope = app.Services.CreateScope();
	var importer = scope.ServiceProvider.GetRequiredService<IEphemerisSampleImporter>();
	var inserted = await importer.RetryZeroSamplesAsync(shrinkDays, CancellationToken.None);
	Console.WriteLine($"Retry complete. Inserted {inserted:N0} new samples from previously zero-sample chunks.");
	return;
}

if (args.Length > 0 && string.Equals(args[0], "import-samples", StringComparison.OrdinalIgnoreCase)) {
	// import-samples [--bodies=slug1,slug2,...] [--bodyIds=1,2,3] [--skip-sync] [h_max] [startUtc] [endUtc] [step]
	// --bodies:    comma-separated slug list; bypasses CompletedEphemeris filter for targeted dense imports.
	// --bodyIds:   comma-separated body ID list; same bypass behaviour as --bodies.
	// --skip-sync: skip the body catalog sync (saves a few minutes on targeted re-imports).
	// h_max:       H magnitude cutoff — imports bodies where H <= h_max OR H IS NULL (authoritative bodies).
	//              Omit (or omit with --bodies/--bodyIds) to import all bodies with a stored Horizons range.
	// startUtc/endUtc: optional batch window; each body's range is clipped to its stored min/max.
	// step:        sample rate override (e.g. "daily", "1h"). Defaults to 1 day.
	var namedArgs      = args.Skip(1).Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToArray();
	var positionalArgs = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

	var syncCatalog = namedArgs.Any(a => string.Equals(a, "--sync-catalog", StringComparison.OrdinalIgnoreCase));
	var bodiesArg   = namedArgs.FirstOrDefault(a => a.StartsWith("--bodies=",   StringComparison.OrdinalIgnoreCase));
	var bodyIdsArg  = namedArgs.FirstOrDefault(a => a.StartsWith("--bodyIds=",  StringComparison.OrdinalIgnoreCase));

	var unknownArgs = namedArgs.Where(a =>
		!string.Equals(a, "--sync-catalog", StringComparison.OrdinalIgnoreCase) &&
		!a.StartsWith("--bodies=",  StringComparison.OrdinalIgnoreCase) &&
		!a.StartsWith("--bodyIds=", StringComparison.OrdinalIgnoreCase)).ToArray();
	if (unknownArgs.Length > 0) {
		Console.Error.WriteLine($"Unknown argument(s): {string.Join(", ", unknownArgs)}");
		Console.Error.WriteLine("Usage: import-samples [--sync-catalog] [--bodies=slug1,slug2] [--bodyIds=1,2,3] [h_max] [startUtc] [endUtc] [step]");
		return;
	}
	IReadOnlyList<string>? slugFilter = bodiesArg is not null
		? bodiesArg["--bodies=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
		: null;
	IReadOnlyList<int>? bodyIdFilter = bodyIdsArg is not null
		? bodyIdsArg["--bodyIds=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(int.Parse).ToArray()
		: null;

	// Cursor-based parsing: hMax only advances the cursor when it successfully parses as a number.
	int pIdx = 0;
	double? hMax = null;
	if (pIdx < positionalArgs.Length && double.TryParse(positionalArgs[pIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) {
		hMax = h;
		pIdx++;
	}
	DateTime? startUtc = pIdx < positionalArgs.Length ? DateTime.Parse(positionalArgs[pIdx++], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) : null;
	DateTime? endUtc   = pIdx < positionalArgs.Length ? DateTime.Parse(positionalArgs[pIdx++], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) : null;
	var sampleRate     = pIdx < positionalArgs.Length ? ParseSampleRate(positionalArgs[pIdx])   : null;

	using var scope = app.Services.CreateScope();
	if (syncCatalog) {
		var bodyImporter = scope.ServiceProvider.GetRequiredService<IBodyCatalogImporter>();
		var bodyImportResult = await bodyImporter.ImportAsync(CancellationToken.None);
		Console.WriteLine($"Body catalog synced. Inserted: {bodyImportResult.Inserted}, Updated: {bodyImportResult.Updated}, Total: {bodyImportResult.Total}.");
	}

	var importer = scope.ServiceProvider.GetRequiredService<IEphemerisSampleImporter>();
	var result = await importer.ImportAsync(hMax, startUtc, endUtc, sampleRate, slugFilter, bodyIdFilter, CancellationToken.None);
	Console.WriteLine($"Ephemeris import complete. Bodies: {result.BodyCount:N0}, Samples: {result.SampleCount:N0}.");
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
		"/api/bodies?h_max=<magnitude>&maxBodies=<count>",
		"/api/bodies/batch?h_max=<magnitude>&h_min_exclusive=<magnitude>&take=<count>&afterBodyId=<id>",
		"/api/bodies/search?q=<text>&limit=<count>&namedOnly=<bool>",
		"/api/bodies/{slug}",
		"/api/ephemeris/window?centerUtc=...&radiusDays=...&h_max=<magnitude>&maxBodies=<count>",
		"/api/ephemeris/bulk?startUtc=...&endUtc=...&h_max=<magnitude>&maxBodies=<count>",
		"/api/ephemeris/{bodyId}?startUtc=...&endUtc=...&limit=...",
		"/api/ephemeris/by-slug/{slug}?startUtc=...&endUtc=...&limit=..."
	}
}));

app.MapGet("/api/health", () => Results.Ok(new
{
	status = "ok",
	utc = DateTime.UtcNow
}));

app.MapGet("/api/bodies", async (double? h_max, int? maxBodies, IEphemerisRepository repository, CancellationToken cancellationToken) =>
{
	var bodies = await repository.GetBodiesAsync(h_max, maxBodies, cancellationToken);
	return Results.Ok(bodies);
});

app.MapGet("/api/bodies/batch", async (
	double? h_max,
	double? h_min_exclusive,
	int? take,
	int? afterBodyId,
	IEphemerisRepository repository,
	CancellationToken cancellationToken) =>
{
	var batchSize = Math.Clamp(take ?? 10000, 1, 50000);
	var rows = await repository.GetBodiesBatchAsync(h_min_exclusive, h_max, batchSize, afterBodyId, cancellationToken);
	var nextAfterBodyId = rows.Count > 0 ? rows[^1].Id : afterBodyId;
	var done = rows.Count < batchSize;

	return Results.Ok(new
	{
		hMax = h_max,
		hMinExclusive = h_min_exclusive,
		take = batchSize,
		afterBodyId,
		nextAfterBodyId,
		done,
		count = rows.Count,
		items = rows
	});
});

app.MapGet("/api/bodies/search", async (string? q, int? limit, bool? namedOnly, IEphemerisRepository repository, CancellationToken cancellationToken) =>
{
	var normalizedLimit = Math.Clamp(limit ?? 150, 1, 2000);
	var bodies = await repository.SearchBodiesAsync(
		q,
		normalizedLimit,
		true,
		namedOnly ?? true,
		cancellationToken);
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

app.MapGet("/api/ephemeris/window", async (DateTime centerUtc, double? radiusDays, double? h_max, int? step, int? maxBodies, IEphemerisRepository repository, IMemoryCache cache, CancellationToken cancellationToken) =>
{
	if (centerUtc == default)
		return Results.BadRequest(new { error = "centerUtc is required query parameter in UTC." });

	var radius = Math.Max(0.5, radiusDays ?? 1.0);
	var startUtc = centerUtc.AddDays(-radius);
	var endUtc = centerUtc.AddDays(radius);
	var stride = Math.Max(1, step ?? 1);
	var normalizedMaxBodies = maxBodies.HasValue ? Math.Clamp(maxBodies.Value, 1, 5000) : (int?)null;
	var isAuthoritativeOnly = h_max is null && !normalizedMaxBodies.HasValue;
	var shouldCache = isAuthoritativeOnly;

	var cacheKey = $"ephwin|{centerUtc:o}|{radius}|{h_max}|{stride}|{normalizedMaxBodies}";
	if (!shouldCache || !cache.TryGetValue(cacheKey, out EphemerisBulkResponse? response))
	{
		var samples = await repository.GetBulkSamplesAsync(startUtc, endUtc, h_max, stride, normalizedMaxBodies, cancellationToken);
		response = new EphemerisBulkResponse(startUtc, endUtc, samples.Count, samples);

		if (shouldCache)
		{
			// Very short lived + bounded entry size to avoid timeline-cardinality blowups.
			var approxBytes = Math.Max(1, samples.Count * 96);
			if (approxBytes <= 8_000_000)
			{
				cache.Set(cacheKey, response, new MemoryCacheEntryOptions
				{
					AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30),
					Size = approxBytes,
					Priority = CacheItemPriority.Low,
				});
			}
		}
	}

	return Results.Ok(response);
});

app.MapGet("/api/ephemeris/bulk", async (DateTime startUtc, DateTime endUtc, double? h_max, int? step, int? maxBodies, IEphemerisRepository repository, IMemoryCache cache, CancellationToken cancellationToken) =>
{
	if (startUtc == default || endUtc == default)
		return Results.BadRequest(new { error = "startUtc and endUtc are required query parameters in UTC." });
	if (endUtc < startUtc)
		return Results.BadRequest(new { error = "endUtc must be greater than or equal to startUtc." });

	var stride = Math.Max(1, step ?? 1);
	var normalizedMaxBodies = maxBodies.HasValue ? Math.Clamp(maxBodies.Value, 1, 5000) : (int?)null;
	var isAuthoritativeOnly = h_max is null && !normalizedMaxBodies.HasValue;
	var shouldCache = isAuthoritativeOnly;
	var cacheKey = $"eph|{startUtc:o}|{endUtc:o}|{h_max}|{stride}|{normalizedMaxBodies}";
	if (!shouldCache || !cache.TryGetValue(cacheKey, out EphemerisBulkResponse? response))
	{
		var samples = await repository.GetBulkSamplesAsync(startUtc, endUtc, h_max, stride, normalizedMaxBodies, cancellationToken);
		response = new EphemerisBulkResponse(startUtc, endUtc, samples.Count, samples);

		if (shouldCache)
		{
			var approxBytes = Math.Max(1, samples.Count * 96);
			if (approxBytes <= 8_000_000)
			{
				cache.Set(cacheKey, response, new MemoryCacheEntryOptions
				{
					AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30),
					Size = approxBytes,
					Priority = CacheItemPriority.Low,
				});
			}
		}
	}
	return Results.Ok(response);
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
	if (normalizedLimit <= 0) {
		return new(null, Results.BadRequest(new { error = "limit must be greater than 0." }));
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
		"daily"  => TimeSpan.FromDays(1),
		_ when normalized.EndsWith("m", StringComparison.Ordinal) && int.TryParse(normalized[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
			=> TimeSpan.FromMinutes(minutes),
		_ when normalized.EndsWith("h", StringComparison.Ordinal) && int.TryParse(normalized[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) && hours > 0
			=> TimeSpan.FromHours(hours),
		_ when normalized.EndsWith("d", StringComparison.Ordinal) && int.TryParse(normalized[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0
			=> TimeSpan.FromDays(days),
		_ => throw new ArgumentException("sample rate must be one of: auto, default, hourly, daily, <n>m, <n>h, <n>d")
	};
}

sealed record ValidatedRange(int Limit);
sealed record RangeValidationResult(ValidatedRange? Range, IResult? Error);

sealed class TimestampedWriter(TextWriter inner) : TextWriter
{
    public override System.Text.Encoding Encoding => inner.Encoding;
    public override void WriteLine(string? value) => inner.WriteLine($"[{DateTime.Now:HH:mm:ss}] {value}");
    public override void WriteLine()              => inner.WriteLine($"[{DateTime.Now:HH:mm:ss}]");
    public override void Write(char value)        => inner.Write(value);
    public override void Write(string? value)     => inner.Write(value);
}
