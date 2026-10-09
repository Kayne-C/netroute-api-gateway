var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var instance = app.Configuration["INSTANCE_ID"] ?? Environment.MachineName;
var healthy = true;
var inflight = 0;

app.MapGet("/healthz", () => healthy ? Results.Ok("ok") : Results.StatusCode(503));

app.MapPost("/_control/health/{state}", (string state) =>
{
    healthy = state == "up";
    return Results.Ok(new { healthy });
});

app.MapGet("/", async (HttpContext ctx, int? delayMs) =>
{
    Interlocked.Increment(ref inflight);
    try
    {
        if (delayMs is > 0)
        {
            await Task.Delay(delayMs.Value, ctx.RequestAborted);
        }

        return Results.Json(new
        {
            instance,
            userId = ctx.Request.Headers["X-User-Id"].ToString(),
            tier = ctx.Request.Headers["X-User-Tier"].ToString(),
            roles = ctx.Request.Headers["X-User-Roles"].ToString(),
        });
    }
    finally
    {
        Interlocked.Decrement(ref inflight);
    }
});

app.Run();
