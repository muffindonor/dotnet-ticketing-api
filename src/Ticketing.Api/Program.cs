using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Ticketing.Api.Data;
using Ticketing.Api.Messaging;
using Ticketing.Api.Options;
using Ticketing.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<TicketingDbContext>("sql");
builder.Services.AddAuthRateLimit();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddOptions<JwtOptions>().BindConfiguration("Jwt").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<BookingOptions>().BindConfiguration("Booking").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<RabbitMqOptions>().BindConfiguration("RabbitMq").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<AuthRateLimitOptions>().BindConfiguration("RateLimiting").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SeedOptions>().BindConfiguration("Seed");

builder.Services.AddDbContextPool<TicketingDbContext>((services, options) =>
    options.UseSqlServer(services.GetRequiredService<IConfiguration>().GetConnectionString("Sql")));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((options, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            NameClaimType = "sub",
            RoleClaimType = "role",
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddScoped<ISeatHoldService, SeatHoldService>();
builder.Services.AddScoped<OrderPaymentService>();
builder.Services.AddScoped<HoldExpiryService>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<RabbitMqConnection>();
builder.Services.AddHostedService<OutboxPublisher>();
builder.Services.AddHostedService<PaymentResultConsumer>();
builder.Services.AddHostedService<HoldExpirySweeper>();

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    await DatabaseInitializer.InitializeAsync(app.Services);
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// /health: is the process alive? /health/ready: can it reach the database?
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");

await app.RunAsync();
