using HoldTheLine.Server;

// Bind ServerOptions from appsettings + HTL_-prefixed environment variables, then boot.
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("HTL_")
    .Build();

var options = new ServerOptions();
config.GetSection("Server").Bind(options);

var app = ServerApp.Build(options);

app.Logger.LogInformation("Hold the Line server listening on {Urls}", options.Urls);
app.Run();
