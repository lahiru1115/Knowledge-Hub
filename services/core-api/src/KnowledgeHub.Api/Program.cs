using KnowledgeHub.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddInfrastructure();

var app = builder.Build();
app.UseExceptionHandler();

// Health endpoints sit outside /api/v1 (Doc 02 §8.2, IR-17).
// /health/ready will check the database once the DbContext exists.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

var api = app.MapGroup("/api/v1"); // module endpoint groups attach here
_ = api;

app.Run();

public partial class Program;
