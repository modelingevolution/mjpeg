var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// COOP/COEP required for SharedArrayBuffer (WASM threading)
app.Use(async (context, next) =>
{
    context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    context.Response.Headers["Cross-Origin-Embedder-Policy"] = "credentialless";
    await next();
});

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();
