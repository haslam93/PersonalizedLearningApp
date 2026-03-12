using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using UpskillTracker.Components;
using UpskillTracker.Data;
using UpskillTracker.Services;

var builder = WebApplication.CreateBuilder(args);
var connectionString = ResolveSqliteConnectionString(builder.Configuration, builder.Environment);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMemoryCache();
builder.Services.AddMudServices();
builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddDbContextFactory<TrackerDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped<TrackerService>();
builder.Services.AddHttpClient<AnnouncementFeedService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("UpskillTracker/1.0");
});

var app = builder.Build();

await DatabaseInitializer.InitializeAsync(app.Services);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string ResolveSqliteConnectionString(IConfiguration configuration, IWebHostEnvironment environment)
{
    var configured = configuration["Storage:ConnectionString"];
    var connectionString = string.IsNullOrWhiteSpace(configured)
        ? $"Data Source={Path.Combine(environment.ContentRootPath, "Data", "upskilltracker.db")}"
        : configured.Replace("%CONTENTROOT%", environment.ContentRootPath, StringComparison.OrdinalIgnoreCase);

    var dataSourceSegment = connectionString
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault(segment => segment.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase));

    var dataSource = dataSourceSegment?.Split('=', 2)[1];
    if (!string.IsNullOrWhiteSpace(dataSource))
    {
        var directory = Path.GetDirectoryName(dataSource);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    return connectionString;
}
