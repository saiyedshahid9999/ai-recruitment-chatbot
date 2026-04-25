using ChatBot.Services;
using Xceed.Document.NET;

var builder = WebApplication.CreateBuilder(args);

// MVC + Services
builder.Services.AddControllersWithViews();
// Add session services
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// Configure SQL Server distributed cache for session storage
builder.Services.AddDistributedSqlServerCache(options =>
{
    options.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    options.SchemaName = "dbo";
    options.TableName = "AspNetSession";
});

builder.Services.AddSingleton<ChatGPTService>();
builder.Services.AddScoped<ChatDbService>();
builder.Services.AddHostedService<ScraperHostedService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<NotificationService>();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();

// Enable session middleware
app.UseSession();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Chat}/{action=Index}/{id?}"
);

app.Run();