using AURA.Billing.Api.Data;
using AURA.Billing.Api.Models;
using AURA.Billing.Api.Services;
using Npgsql;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// This application is a local development PoC.
if (!builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "Billing cache PoC is restricted to Development.");
}

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
{
    var connectionString =
        builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException(
            "Missing PostgreSQL connection string.");

    return NpgsqlDataSource.Create(connectionString);
});

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var connectionString =
        builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException(
            "Missing Redis connection string.");

    var options = ConfigurationOptions.Parse(connectionString);

    options.AbortOnConnectFail = false;
    options.ConnectTimeout = 1000;
    options.ConnectRetry = 1;
    options.SyncTimeout = 1000;
    options.AsyncTimeout = 1000;

    return ConnectionMultiplexer.Connect(options);
});

builder.Services.AddSingleton<PackageRepository>();
builder.Services.AddSingleton<PackageCatalogService>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI();

await app.Services
    .GetRequiredService<PackageRepository>()
    .InitializeAsync(CancellationToken.None);

app.MapHealthChecks("/health");

app.MapGet(
    "/api/v1/billing/packages",
    async (
        PackageCatalogService service,
        CancellationToken cancellationToken) =>
    {
        var response = await service.GetAllAsync(cancellationToken);
        return Results.Ok(response);
    })
    .WithName("GetPackageCatalog")
    .WithTags("Package Catalog PoC")
    .Produces<PackageCatalogResponse>();

app.MapPut(
    "/api/v1/billing/packages/{id:int}",
    async Task<IResult> (
        int id,
        UpdatePackageRequest request,
        PackageCatalogService service,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(request.Name)
            || request.Name.Length > 100
            || request.Price < 0
            || request.Credits <= 0)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_PACKAGE",
                message =
                    "Name is required and at most 100 characters; " +
                    "price must be non-negative; credits must be positive."
            });
        }

        var response =
            await service.UpdateAsync(id, request, cancellationToken);

        return response is null
            ? Results.NotFound()
            : Results.Ok(response);
    })
    .WithName("UpdatePackage")
    .WithTags("Package Catalog PoC")
    .Produces<UpdatePackageResponse>()
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status404NotFound);

app.Run();