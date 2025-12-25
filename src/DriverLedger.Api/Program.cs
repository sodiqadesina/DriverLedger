using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using DriverLedger.Api.Common;
using DriverLedger.Api.Common.Auth;
using DriverLedger.Api.Common.Middleware;
using DriverLedger.Api.Modules.Auth;
using DriverLedger.Api.Modules.Files;
using DriverLedger.Api.Modules.Receipts;
using DriverLedger.Application.Auditing;
using DriverLedger.Application.Common;
using DriverLedger.Application.Receipts;
using DriverLedger.Infrastructure.Auditing;
using DriverLedger.Infrastructure.Common;
using DriverLedger.Infrastructure.Files;
using DriverLedger.Infrastructure.Messaging;
using DriverLedger.Infrastructure.Receipts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// App Insights
builder.Services.AddApplicationInsightsTelemetry();


// EF Core
var sql = Environment.GetEnvironmentVariable("DRIVERLEDGER_SQL");
if (!string.IsNullOrWhiteSpace(sql))
{
    builder.Configuration["ConnectionStrings:Sql"] = sql;
}

builder.Services.AddDbContext<DriverLedgerDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("Sql")));


// Cross-cutting
builder.Services.AddScoped<ITenantProvider, TenantProvider>();
builder.Services.AddSingleton<IClock, SystemClock>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IRequestContext, RequestContext>();
builder.Services.AddScoped<IAuditWriter, AuditWriter>();

builder.Services.AddScoped<IReceiptReceivedHandler, ReceiptReceivedHandler>();

// Azure clients
builder.Services.AddSingleton(_ => new BlobServiceClient(builder.Configuration["Azure:BlobConnectionString"]));
builder.Services.AddSingleton(_ => new ServiceBusClient(builder.Configuration["Azure:ServiceBusConnectionString"]));
builder.Services.AddSingleton<IBlobStorage, BlobStorage>();
builder.Services.AddSingleton<IMessagePublisher, ServiceBusPublisher>();

// Auth
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(); // configure below via options pipeline

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((options, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.JwtKey))
        {
            KeyId = "driverledger-v1"
        };

        // Use JwtSecurityTokenHandler path
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

// Middleware
builder.Services.AddScoped<CorrelationMiddleware>();
builder.Services.AddScoped<TenantScopeMiddleware>();

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

app.UseMiddleware<CorrelationMiddleware>();
app.UseAuthentication();
app.UseMiddleware<TenantScopeMiddleware>();
app.UseAuthorization();


// Modules registration (below)
ApiAuth.MapAuthEndpoints(app);
ApiFiles.MapFileEndpoints(app);
ApiReceipts.MapReceiptEndpoints(app);

app.Run();

public partial class Program { } // for integration tests later
