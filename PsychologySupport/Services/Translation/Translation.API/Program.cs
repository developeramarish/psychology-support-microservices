using BuildingBlocks.Exceptions.Handler;
using Carter;
using Translation.API.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.LoadConfiguration(builder.Environment);

var services = builder.Services;

services.AddApplicationServices(builder.Configuration);
services.AddExceptionHandler<CustomExceptionHandler>();


// Configure the HTTP request pipeline
var app = builder.Build();

app.UseExceptionHandler(options => { });

// Apply CORS policy
app.UseCors("CorsPolicy");

app.UseStaticFiles();

app.MapCarter();

app.UseSwagger();
if (app.Environment.IsDevelopment())
{
    app.InitializeDatabaseAsync();
    app.UseSwaggerUI();
}
else
{
    app.UseSwaggerUI(c =>
    {
        c.RoutePrefix = string.Empty;
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Translation API v1");
    });
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();


app.Run();