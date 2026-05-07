using CloseManager.Web.Auth;
using CloseManager.Web.Data;
using CloseManager.Web.Data.Services;
using CloseManager.Web.Jobs;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using MudBlazor.Services;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting CloseManager");
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, config) =>
        config.ReadFrom.Configuration(ctx.Configuration)
              .ReadFrom.Services(services)
              .Enrich.FromLogContext()
              .Enrich.WithProperty("Application", "CloseManager"));

    builder.Services
        .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

    builder.Services.AddControllersWithViews()
        .AddMicrosoftIdentityUI();

    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    });

    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlServer(
            builder.Configuration.GetConnectionString("DefaultConnection"),
            sql => sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)));

    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(
            builder.Configuration.GetConnectionString("DefaultConnection"),
            new SqlServerStorageOptions
            {
                CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                QueuePollInterval = TimeSpan.Zero,
                UseRecommendedIsolationLevel = true,
                DisableGlobalLocks = true
            }));

    builder.Services.AddHangfireServer(options => { options.WorkerCount = 2; });
    builder.Services.AddMudServices();
    builder.Services.AddRazorPages();
    builder.Services.AddServerSideBlazor()
        .AddMicrosoftIdentityConsentHandler();

    builder.Services.AddScoped<UserSyncService>();
    builder.Services.AddScoped<AuditService>();
    builder.Services.AddScoped<AppSettingService>();
    builder.Services.AddScoped<CurrentUserService>();
    builder.Services.AddScoped<PeriodService>();
    builder.Services.AddScoped<WorkstreamService>();
    builder.Services.AddScoped<Graph.SharePointService>();
    builder.Services.AddHttpContextAccessor();

    // SignalR for period-open progress
    builder.Services.AddSignalR();

    // Admin group ID for role-gating
    builder.Services.Configure<AdminOptions>(options =>
        options.AdminGroupId = builder.Configuration["AdminGroupId"] ?? string.Empty);

    var app = builder.Build();

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error");
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseStaticFiles();
    app.UseSerilogRequestLogging(opts =>
    {
        opts.GetLevel = (ctx, elapsed, ex) =>
            ex != null ? Serilog.Events.LogEventLevel.Error :
            elapsed > 1000 ? Serilog.Events.LogEventLevel.Warning :
            Serilog.Events.LogEventLevel.Debug;
    });

    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseHangfireDashboard(app.Configuration["Hangfire:DashboardPath"] ?? "/hangfire");
    app.MapControllers();
    app.MapBlazorHub();
    app.MapHub<PeriodProgressHub>("/hubs/period-progress");
    app.MapFallbackToPage("/_Host");

    // Register recurring Hangfire jobs
    RecurringJob.AddOrUpdate<LockExpirySweepJob>(
        "lock-expiry-sweep",
        j => j.ExecuteAsync(),
        "*/2 * * * *");  // every 2 minutes
    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "CloseManager terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
