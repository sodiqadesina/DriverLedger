using System.Text;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using DriverLedger.Api.Common.Auth;
using DriverLedger.Api.Common.Middleware;
using DriverLedger.Api.Modules.Auth;
using DriverLedger.Api.Modules.Files;
using DriverLedger.Api.Modules.Receipts;
using DriverLedger.Application.Common;
using DriverLedger.Infrastructure.Common;
using DriverLedger.Infrastructure.Files;
using DriverLedger.Infrastructure.Messaging;
using DriverLedger.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// App Insights
builder.Services.AddApplicationInsightsTelemetry();

// Options
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Auth"));

// EF Core
builder.Services.AddDbContext<DriverLedgerDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("Sql")));

// Cross-cutting
builder.Services.AddScoped<ITenantProvider, TenantProvider>();
builder.Services.AddSingleton<IClock, SystemClock>();

// Azure clients
builder.Services.AddSingleton(_ => new BlobServiceClient(builder.Configuration["Azure:BlobConnectionString"]));
builder.Services.AddSingleton(_ => new ServiceBusClient(builder.Configuration["Azure:ServiceBusConnectionString"]));
builder.Services.AddSingleton<IBlobStorage, BlobStorage>();
builder.Services.AddSingleton<IMessagePublisher, ServiceBusPublisher>();

// Auth
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();

var jwt = builder.Configuration.GetSection("Auth").Get<JwtOptions>()!;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new()
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.JwtIssuer,
            ValidAudience = jwt.JwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.JwtKey))
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

app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<TenantScopeMiddleware>();

// Modules registration (below)
ApiAuth.MapAuthEndpoints(app);
ApiFiles.MapFileEndpoints(app);
ApiReceipts.MapReceiptEndpoints(app);

app.Run();

public partial class Program { } // for integration tests later
