using Demo.Server.Components;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(args.FirstOrDefault(a => a.StartsWith("http")) ?? "http://0.0.0.0:5100");

builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// COOP/COEP on all requests — required for SharedArrayBuffer in iframe.
// "credentialless" COEP is compatible with non-MT WASM pages too.
app.Use(async (context, next) =>
{
    context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    context.Response.Headers["Cross-Origin-Embedder-Policy"] = "credentialless";
    await next();
});

app.UseStaticFiles();

app.UseAntiforgery();

// Serve main app static assets (including _framework/blazor.web.js) via endpoint routing
app.MapStaticAssets();

// Fallback for Player SPA routes
app.MapFallbackToFile("/player/{*path:nonfile}", "player/index.html");

// Server-rendered Blazor components (Demo.Client via InteractiveWebAssembly)
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Demo.Client._Imports).Assembly);

app.Run();
