using BuildingBlocks.Exceptions.Handler;
using Carter;
using LifeStyles.API.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.LoadConfiguration(builder.Environment);

var services = builder.Services;

services.AddApplicationServices(builder.Configuration);

services.AddExceptionHandler<CustomExceptionHandler>();

services.RegisterMapsterConfiguration();

// Configure the HTTP request pipeline
var app = builder.Build();

//Force .NET to use Custom Exception Handler
app.UseExceptionHandler(options => { });

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
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "LifeStyles API v1");
        c.RoutePrefix = string.Empty;
    });
}

// Apply CORS policy
app.UseCors("CorsPolicy");

app.UseAuthentication();   
app.UseAuthorization();

app.Run();