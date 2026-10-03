using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.EntityFrameworkCore;
using FbrInvoicingApp.Web.Components;
using FbrInvoicingApp.Web.Data;
using FbrInvoicingApp.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddControllersWithViews();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
var authentication = builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    });
authentication.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var id = context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                var factory = context.HttpContext.RequestServices.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                if (id is null || !await db.AppUsers.AnyAsync(x => x.Id == id && x.IsActive))
                    context.Fail("Account no longer exists. Sign in again.");
            },
            OnMessageReceived = context =>
            {
                if (context.Request.Cookies.TryGetValue("fbr_access_token", out var token)) context.Token = token;
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                // Browser navigation to a protected page should enter the custom
                // login screen. API callers continue to receive the normal 401.
                if (!context.Response.HasStarted && !context.Request.Path.StartsWithSegments("/api"))
                {
                    context.HandleResponse();
                    var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
                    context.Response.Redirect($"/Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}");
                }

                return Task.CompletedTask;
            }
        };
        options.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = jwt.Issuer, ValidateAudience = true, ValidAudience = jwt.Audience, ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)), ValidateLifetime = true };
    });

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddHttpContextAccessor();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "keys")));
builder.Services.Configure<FbrOptions>(builder.Configuration.GetSection("Fbr"));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.AddHttpClient<IFbrClient, FbrClient>(client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddScoped<InvoiceService>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<RecoveryEmail>();
builder.Services.AddScoped<CsvInvoiceImportService>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<JwtService>();
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapControllers();

try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    // The project currently uses EnsureCreated because the old Identity migrations
    // are not compatible with the custom AppUsers schema. In production, replace
    // this with an application migration before deploying schema changes.
    await db.Database.EnsureCreatedAsync();
    await DatabaseSeeder.SeedUsersAsync(
        db,
        scope.ServiceProvider.GetRequiredService<PasswordService>(),
        app.Configuration,
        app.Logger);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Database initialization or initial user seeding failed. The UI will still start, but database actions may be unavailable.");
}

app.Run();
