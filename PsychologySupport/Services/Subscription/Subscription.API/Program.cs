using Carter;
using Subscription.API.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.LoadConfiguration(builder.Environment);

var services = builder.Services;

services.AddApplicationServices(builder.Configuration);

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
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Subscription API v1");
        c.RoutePrefix = string.Empty;
    });
}

app.UseAuthentication();
app.UseAuthorization();

app.UseRouting();

app.Run();