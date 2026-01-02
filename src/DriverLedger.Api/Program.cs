using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using DriverLedger.Api.Common;
using DriverLedger.Api.Common.Auth;
using DriverLedger.Api.Common.Middleware;
using DriverLedger.Api.Modules.Auth;
using DriverLedger.Api.Modules.Files;
using DriverLedger.Api.Modules.Ledger;
using DriverLedger.Api.Modules.LiveStatement;
using DriverLedger.Api.Modules.Receipts;
using DriverLedger.Application.Auditing;
using DriverLedger.Application.Common;
using DriverLedger.Application.Receipts;
using DriverLedger.Application.Receipts.Extraction;
using DriverLedger.Infrastructure.Auditing;
using DriverLedger.Infrastructure.Common;
using DriverLedger.Infrastructure.Files;
using DriverLedger.Infrastructure.Ledger;
using DriverLedger.Infrastructure.Messaging;
using DriverLedger.Infrastructure.Options;
using DriverLedger.Infrastructure.Persistence.Interceptors;
using DriverLedger.Infrastructure.Receipts;
using DriverLedger.Infrastructure.Receipts.Extraction;
using DriverLedger.Infrastructure.Statements.Extraction;
using DriverLedger.Infrastructure.Statements.Snapshots;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

var builder = WebApplication.CreateBuilder(args);


builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy
            .WithOrigins("http://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
    );
});

// -----------------------------
// Observability
// -----------------------------
builder.Services.AddApplicationInsightsTelemetry();

// -----------------------------
// Config overrides (env-first for CI/CD)
// -----------------------------
static string? Env(string key) => Environment.GetEnvironmentVariable(key);

if (!string.IsNullOrWhiteSpace(Env("DRIVERLEDGER_SQL")))
builder.Configuration["ConnectionStrings:Sql"] = Env("DRIVERLEDGER_SQL");

if (!string.IsNullOrWhiteSpace(Env("DRIVERLEDGER_BLOB")))
builder.Configuration["Azure:BlobConnectionString"] = Env("DRIVERLEDGER_BLOB");

if (!string.IsNullOrWhiteSpace(Env("DRIVERLEDGER_SB")))
builder.Configuration["Azure:ServiceBusConnectionString"] = Env("DRIVERLEDGER_SB");

// OPTIONAL (if you use env vars in CI)
if (!string.IsNullOrWhiteSpace(Env("DRIVERLEDGER_DI_ENDPOINT")))
builder.Configuration["Azure:DocumentIntelligence:Endpoint"] = Env("DRIVERLEDGER_DI_ENDPOINT");
if (!string.IsNullOrWhiteSpace(Env("DRIVERLEDGER_DI_KEY")))
builder.Configuration["Azure:DocumentIntelligence:ApiKey"] = Env("DRIVERLEDGER_DI_KEY");

// -----------------------------
// EF Core
// -----------------------------
builder.Services.AddDbContext<DriverLedgerDbContext>(opt =>
{
opt.UseSqlServer(builder.Configuration.GetConnectionString("Sql"));
opt.AddInterceptors(new LedgerImmutabilityInterceptor());
});

// -----------------------------
// Cross-cutting services
// -----------------------------
builder.Services.AddScoped<ITenantProvider, TenantProvider>();
builder.Services.AddSingleton<IClock, SystemClock>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IRequestContext, RequestContext>();
builder.Services.AddScoped<IAuditWriter, AuditWriter>();

// Foundation "gate" handler (already exists)
builder.Services.AddScoped<IReceiptReceivedHandler, ReceiptReceivedHandler>();
builder.Services.AddScoped<ManualLedgerPostingHandler>();
builder.Services.AddScoped<AdjustmentLedgerPostingHandler>();


// -----------------------------
// M1 Pipeline: Extraction + Posting + Snapshots
// -----------------------------
builder.Services.AddScoped<ReceiptExtractionHandler>();
builder.Services.AddScoped<ReceiptToLedgerPostingHandler>();
builder.Services.AddScoped<SnapshotCalculator>();

// Document Intelligence options + extractor
builder.Services.Configure<DocumentIntelligenceOptions>(
    builder.Configuration.GetSection("Azure:DocumentIntelligence"));

builder.Services.AddScoped<IReceiptExtractor, AzureDocumentIntelligenceReceiptExtractor>();
builder.Services.AddScoped<IStatementExtractor, AzureDocumentIntelligenceStatementExtractor>();
builder.Services.AddScoped<IStatementExtractor, CsvStatementExtractor>();
builder.Services.AddScoped<StatementExtractionHandler>();
builder.Services.AddScoped<StatementToLedgerPostingHandler>();

// -----------------------------
// Azure clients
// -----------------------------
builder.Services.AddSingleton(_ =>
{
var cs = builder.Configuration["Azure:BlobConnectionString"];
if (string.IsNullOrWhiteSpace(cs))
throw new InvalidOperationException("Missing configuration: Azure:BlobConnectionString");
return new BlobServiceClient(cs);
});

builder.Services.AddSingleton(_ =>
{
var cs = builder.Configuration["Azure:ServiceBusConnectionString"];
if (string.IsNullOrWhiteSpace(cs))
throw new InvalidOperationException("Missing configuration: Azure:ServiceBusConnectionString");
return new ServiceBusClient(cs);
});

builder.Services.AddSingleton<IBlobStorage, BlobStorage>();
builder.Services.AddSingleton<IMessagePublisher, ServiceBusPublisher>();

// -----------------------------
// Auth
// -----------------------------
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((options, jwtOptions) =>
{
var jwt = jwtOptions.Value;

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.JwtKey))
{
KeyId = "driverledger-v1"
};

options.TokenHandlers.Clear();
options.TokenHandlers.Add(new JwtSecurityTokenHandler());

options.TokenValidationParameters = new TokenValidationParameters
{
ValidateIssuer = true,
ValidateAudience = true,
ValidateLifetime = true,
ValidateIssuerSigningKey = true,

ValidIssuer = jwt.JwtIssuer,
ValidAudience = jwt.JwtAudience,

IssuerSigningKey = signingKey,
TryAllIssuerSigningKeys = true,

ClockSkew = TimeSpan.FromMinutes(2)
};
});

builder.Services.AddAuthorization(options =>
{
options.AddPolicy("RequireDriver", p => p.RequireRole("Driver"));
options.AddPolicy("RequireAdmin", p => p.RequireRole("Admin"));
});

// -----------------------------
// Middleware
// -----------------------------
builder.Services.AddScoped<CorrelationMiddleware>();
builder.Services.AddScoped<TenantScopeMiddleware>();

// -----------------------------
// API essentials
// -----------------------------
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

// -----------------------------
// Build
// -----------------------------
var app = builder.Build();


// -----------------------------
// Global exception handler → ProblemDetails
// -----------------------------
app.UseExceptionHandler(errorApp =>
{
errorApp.Run(async context =>
{
var ex = context.Features.Get<IExceptionHandlerFeature>()?.Error;

var pd = new ProblemDetails
{
Title = "Unhandled error",
Status = StatusCodes.Status500InternalServerError,
Detail = ex?.Message
};

context.Response.StatusCode = pd.Status.Value;
context.Response.ContentType = "application/problem+json";
await context.Response.WriteAsJsonAsync(pd);
});
});

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
using var scope = app.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<DriverLedgerDbContext>();
db.Database.Migrate();
}

app.UseSwagger();
app.UseSwaggerUI();


app.UseCors("Frontend");

//app.UseHttpsRedirection();

app.UseMiddleware<CorrelationMiddleware>();
app.UseAuthentication();
app.UseMiddleware<TenantScopeMiddleware>();
app.UseAuthorization();

app.MapHealthChecks("/health");

// -----------------------------
// Modules
// -----------------------------
ApiAuth.MapAuthEndpoints(app);
ApiFiles.MapFileEndpoints(app);
ApiReceipts.MapReceiptEndpoints(app);
ApiLiveStatement.MapLiveStatement(app);
ApiLedger.MapLedger(app);

app.Run();

public partial class Program { }
